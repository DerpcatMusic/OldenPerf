using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace OldenPerf;

// Fog-of-war culling driven by the tile grid. Work happens only when the fog bits change; an unchanged frame costs one
// 25,600-byte comparison.
//
// Layer 1, deep fog (whole cells). bxm.ppf(Camera) is the game's own map culler: for each 16x16-tile cell it tests the
// cell bounds (bxm+0x40, Bounds[]) against the camera frustum and calls SetActive(result) on the cell root, every frame.
// The game already runs any cell inactive whenever it is off screen. For a cell with no revealed tile in itself or its
// eight neighbours, a prefix hands ppf an empty far-away bounds and a postfix restores the original right after, so the
// game itself keeps the cell inactive: no draw, no shadow caster, no OnEnable cascade, no child scripts. Panning across
// fog therefore activates nothing. A cell qualifies only if every renderer and light it owns stays inside that ring.
//
// Layer 2, frontier (single renderers). In cells next to revealed ground, a renderer whose cached tile footprint plus one
// tile has no revealed tile gets forceRenderingOff. Combined prop batches are filtered by BatchFogCulling instead.
//
// Verified against the installed interop and native code (current-isil bxm.ppf): cbhw +0x38 Transform[] cell roots,
// cbhx +0x40 Bounds[], cbhy +0x48 bool[] stored visibility; fqw.bntc() -> Side.fogData.visibilityMap, row-major,
// true = revealed; tile t is centred at world t - size/2 (SectionCullState.TileSpan). Cell i must be named "Section Root: i % cellsX, i / cellsX"
// or the cell layer stays off. Anything unproven fails open (drawn).
static class FogCulling
{
    const float MaxRendererUnits = 20f;     // larger renderers are shared scenery: never hidden, cell never switched off
    const float LooseRadius = 4f;           // particles/lines/sprites: transform +- half a cell
    const double InspectBudgetMs = 2.0;     // per frame, spread across frames after a map loads
    static readonly Bounds Away = new(new Vector3(0, -1e6f, 0), Vector3.zero);

    sealed class Cell
    {
        public Transform Root;
        public int Sx, Sz, ChildCount = -1;
        public bool Inspected, Eligible, Deep;
        public Renderer[] Renderers = Array.Empty<Renderer>();
        public bool[] OriginalForce = Array.Empty<bool>(), Hidden = Array.Empty<bool>();
        public int[] Rects = Array.Empty<int>(); // x0,z0,x1,z1 per renderer with margin; x0 < 0 = never hide
    }

    static bool _installed;
    static fqw _side;
    static Hex.Session.Data.DataFogOfWar _data;
    static MethodInfo _sideGetter;
    static float _nextResolve;

    static readonly List<Il2CppStructArray<bool>> _grids = new();
    static bool[] _union = Array.Empty<bool>();
    static int _w, _h, _revision, _frame = -1;
    static bool[] _revealed = Array.Empty<bool>();
    static int[] _sat = Array.Empty<int>();

    static bxm _culler;
    static Cell[] _cells = Array.Empty<Cell>();
    static int _cellsX, _inspectCursor;
    static bool _cellLayer, _cellsBound, _wasOn = true;
    static readonly List<int> Deep = new();
    static Bounds[] _saved = Array.Empty<Bounds>();
    static Il2CppStructArray<Bounds> _swapped;
    static int[] _swappedIdx = Array.Empty<int>();
    static int _swappedCount;

    // Why cells fail inspection: cells per reason, plus the first offender seen. Cleared when cells are rebound.
    static readonly SortedDictionary<string, int> Rejects = new();
    static readonly Dictionary<string, string> RejectExample = new();
    static bool _rejectsLogged;

    static int _hiddenRenderers, _writes, _errors, _swapCalls, _fogChanges;
    static long _pollTicks, _pollMax, _inspectTicks;

    public static void Init()
    {
        var harmony = new Harmony("derpcat.oldenperf.fog");
        try
        {
            if (!BuildIdentity.IsVerified) throw new InvalidOperationException("unverified build");
            var bind = typeof(bek).GetMethod("lmj") ?? throw new MissingMethodException("bek.lmj");
            var cull = typeof(bxm).GetMethod("ppf", new[] { typeof(Camera) }) ?? throw new MissingMethodException("bxm.ppf(Camera)");
            HarmonyMethod Hook(string n) => new(typeof(FogCulling).GetMethod(n, BindingFlags.NonPublic | BindingFlags.Static));
            harmony.Patch(bind, postfix: Hook(nameof(MapBound)));
            harmony.Patch(cull, prefix: Hook(nameof(BeforeCull)), postfix: Hook(nameof(AfterCull)));
            BatchFogCulling.Init();
            _installed = true;
            Plugin.I.Log.LogInfo("fog culling armed: deep-fog cells via the game's culler, frontier renderers, combined prop batches");
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf();
            Plugin.I.Log.LogWarning("fog culling unavailable: " + e.Message);
        }
    }

    // bek.lmj(Map, int, fqw, float[]) binds a new map and the local side.
    static void MapBound(fqw __2)
    {
        RestoreAll();
        BatchFogCulling.ForgetMeshBounds();
        _side = __2; _data = null; _grids.Clear(); _nextResolve = 0;
        _culler = null; _cells = Array.Empty<Cell>(); _cellsBound = false; _cellLayer = false;
        Deep.Clear(); _frame = -1; _revision++;
    }

    // ---- Layer 1: runs inside the game's culler, once per camera call.
    static void BeforeCull(bxm __instance)
    {
        if (!_installed) return;
        RestoreSwapped(); // a skipped postfix must never leave a cell's bounds replaced
        try
        {
            if (!Plugin.I.FogCullingOn.Value) { if (_wasOn) { RestoreAll(); _wasOn = false; } return; }
            _wasOn = true;
            if (_frame != Time.frameCount) { _frame = Time.frameCount; Poll(__instance); }
            if (!_cellLayer || Deep.Count == 0) return;
            var bounds = __instance.cbhx;
            if (bounds == null || bounds.Length != _cells.Length) { DisableCellLayer("cell bounds changed size"); return; }
            if (_saved.Length < _cells.Length) { _saved = new Bounds[_cells.Length]; _swappedIdx = new int[_cells.Length]; }
            _swapped = bounds; _swappedCount = 0;
            foreach (int i in Deep) { _swappedIdx[_swappedCount] = i; _saved[_swappedCount++] = bounds[i]; bounds[i] = Away; }
            _swapCalls++;
        }
        catch (Exception e) { Fail("cull prefix", e); }
    }

    static void AfterCull() => RestoreSwapped();

    static void RestoreSwapped()
    {
        var bounds = _swapped;
        if (bounds == null) return;
        _swapped = null;
        try { for (int k = 0; k < _swappedCount; k++) bounds[_swappedIdx[k]] = _saved[k]; }
        catch (Exception e) { Fail("cull restore", e); }
        _swappedCount = 0;
    }

    // Once per frame: resolve the fog source, detect changes, then update both layers and the batches.
    static void Poll(bxm culler)
    {
        long start = Stopwatch.GetTimestamp();
        if (!ResolveFog()) { if (Deep.Count > 0 || _hiddenRenderers > 0) RestoreAll(); return; }
        if (!_cellsBound || _culler == null || _culler.Pointer != culler.Pointer) BindCells(culler);
        int n = _w * _h;
        if (_union.Length != n) _union = new bool[n];
        Union(_union);
        bool changed = _revealed.Length != n || !_union.AsSpan().SequenceEqual(_revealed);
        if (changed)
        {
            if (_revealed.Length != n) { _revealed = new bool[n]; _sat = new int[(_w + 1) * (_h + 1)]; }
            _union.CopyTo(_revealed, 0);
            SectionCullState.BuildSummedArea(_revealed, _w, _h, _sat);
            _revision++; _fogChanges++;
            Reclassify();
        }
        BatchFogCulling.Update(_sat, _w, _h, _revision);
        long t = Stopwatch.GetTimestamp() - start;
        Stats.ModTicks += t;
        _pollTicks += t; _pollMax = Math.Max(_pollMax, t);
    }

    // A tile is revealed when any member of the local side's alliance has revealed it. This is the game's own rule:
    // the fog renderer's source (ehc.bdru) asks Alliance.baqj, which scans every member's DataFogOfWar (edf+0x10 Side,
    // Side+0x40 fogData) and reports revealed if any member's visibilityMap entry is set.
    static bool ResolveFog()
    {
        if (_side != null && Time.realtimeSinceStartup >= _nextResolve)
        {
            _nextResolve = Time.realtimeSinceStartup + 2f; // alliances can change mid-game
            try { ResolveMembers(); }
            catch (Exception e) { _data = null; _grids.Clear(); if (_errors++ < 3) Plugin.I.Log.LogWarning("fog sources unresolved, drawing everything: " + e.Message); }
        }
        int w = _data?.sizeX ?? 0, h = _data?.sizeZ ?? 0;
        if (_grids.Count == 0 || w <= 0 || h <= 0) return false;
        foreach (var g in _grids) if (g == null || (long)g.Length < (long)w * h) return false;
        if (w != _w || h != _h) { _w = w; _h = h; _revealed = Array.Empty<bool>(); _cellsBound = false; }
        return true;
    }

    static void ResolveMembers()
    {
        // fqw.bntc() is compiler-visible only as an accessor, so it is invoked by its verified name.
        _sideGetter ??= typeof(fqw).GetMethod("bntc", Type.EmptyTypes);
        var side = (Hex.Session.Data.Side)_sideGetter?.Invoke(_side, null) ?? throw new InvalidOperationException("no local side");
        var own = side.fogData ?? throw new InvalidOperationException("no local fog");
        var self = _side.crib;
        if (self != null && self.cjuy != null && self.cjuy.Pointer != side.Pointer) throw new InvalidOperationException("local side mismatch");
        var grids = new List<Il2CppStructArray<bool>> { own.visibilityMap };
        var members = self?.cjuz?.cjat;
        if (members != null)
            for (int i = 0; i < members.Count; i++)
            {
                var fog = members[i]?.cjuy?.fogData ?? throw new InvalidOperationException("ally without fog data");
                if (fog.sizeX != own.sizeX || fog.sizeZ != own.sizeZ) throw new InvalidOperationException("ally fog size differs");
                if (fog.Pointer != own.Pointer) grids.Add(fog.visibilityMap);
            }
        if (grids.Count != _grids.Count) Plugin.I.Log.LogInfo($"fog sources: {grids.Count} side(s) in the local alliance");
        _data = own; _grids.Clear(); _grids.AddRange(grids);
    }

    // OR of every alliance member's grid (bools are 0/1 bytes, so whole words can be ORed).
    static void Union(bool[] into)
    {
        int n = into.Length;
        _grids[0].AsSpan().Slice(0, n).CopyTo(into);
        var dst = MemoryMarshal.Cast<bool, ulong>(into.AsSpan());
        for (int g = 1; g < _grids.Count; g++)
        {
            var src = _grids[g].AsSpan().Slice(0, n);
            var words = MemoryMarshal.Cast<bool, ulong>(src);
            for (int i = 0; i < words.Length; i++) dst[i] |= words[i];
            for (int i = words.Length * 8; i < n; i++) into[i] |= src[i];
        }
    }

    // Map each culler slot to its cell root and prove the layout from the roots' own names.
    static void BindCells(bxm culler)
    {
        _culler = culler; _cellsBound = true; _cellLayer = false; Deep.Clear();
        Rejects.Clear(); RejectExample.Clear(); _rejectsLogged = false;
        var roots = culler.cbhw;
        _cellsX = SectionCullState.CellsAcross(_w);
        int cellsZ = SectionCullState.CellsAcross(_h);
        if (roots == null || roots.Length != _cellsX * cellsZ) { DisableCellLayer($"cells {roots?.Length ?? -1}, expected {_cellsX}x{cellsZ}"); return; }
        var cells = new Cell[roots.Length];
        for (int i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (root == null || !SectionCullState.ParseCell(root.name, out int sx, out int sz) || sx != i % _cellsX || sz != i / _cellsX)
            { DisableCellLayer($"cell {i} is '{root?.name}'"); return; }
            cells[i] = new Cell { Root = root, Sx = sx, Sz = sz };
        }
        _cells = cells; _inspectCursor = 0; _cellLayer = true;
        Plugin.I.Log.LogInfo($"fog cells bound: {cells.Length} cells ({_cellsX}x{cellsZ}) on a {_w}x{_h} grid");
        LogCellGeometry(culler);
    }

    // Diagnostic: the game's own cell bounds against where each cell's renderers actually are.
    static void LogCellGeometry(bxm culler)
    {
        try
        {
            var bounds = culler.cbhx;
            foreach (int i in new[] { 0, 1, 9, 10, 11, 55, 90, 99 })
            {
                if (i >= _cells.Length) continue;
                var b = bounds[i]; var root = _cells[i].Root; var rp = root.position;
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue; int n = 0, inside = 0;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    var p = r.transform.position; n++;
                    x0 = Math.Min(x0, p.x); x1 = Math.Max(x1, p.x); z0 = Math.Min(z0, p.z); z1 = Math.Max(z1, p.z);
                    if (Math.Abs(p.x - b.center.x) <= b.extents.x && Math.Abs(p.z - b.center.z) <= b.extents.z) inside++;
                }
                Plugin.I.Log.LogInfo(FormattableString.Invariant(
                    $"fog cell {i} '{root.name}': gameBounds c=({b.center.x:F1},{b.center.y:F1},{b.center.z:F1}) e=({b.extents.x:F1},{b.extents.y:F1},{b.extents.z:F1}) root=({rp.x:F1},{rp.z:F1}) renderers={n} insideGameBounds={inside} pos x=[{x0:F1},{x1:F1}] z=[{z0:F1},{z1:F1}]"));
            }
        }
        catch (Exception e) { Plugin.I.Log.LogWarning("cell geometry: " + e.Message); }
    }

    static void DisableCellLayer(string why)
    {
        if (_cellLayer || _cells.Length > 0) Plugin.I.Log.LogWarning("fog cell layer off: " + why);
        _cellLayer = false; Deep.Clear();
    }

    // ---- Main-thread upkeep from Runner.Update: inspect cells within a time budget, pick up new children.
    public static void Tick()
    {
        if (!_installed || !_cellsBound || _cells.Length == 0 || _grids.Count == 0 || !Plugin.I.FogCullingOn.Value) return;
        long start = Stopwatch.GetTimestamp(), budget = (long)(InspectBudgetMs * Stopwatch.Frequency / 1000);
        bool any = false;
        for (int step = 0; step < _cells.Length; step++)
        {
            var c = _cells[_inspectCursor];
            _inspectCursor = (_inspectCursor + 1) % _cells.Length;
            try
            {
                if (c.Root == null) continue;
                int children = c.Root.childCount;
                if (c.Inspected && children == c.ChildCount) continue;
                Inspect(c, children);
                any = true;
            }
            catch (Exception e) { c.Inspected = true; c.Eligible = false; Fail("inspect " + c.Root?.name, e); }
            if (Stopwatch.GetTimestamp() - start > budget) break;
        }
        if (any) Reclassify();
        long spent = Stopwatch.GetTimestamp() - start;
        _inspectTicks += spent; Stats.ModTicks += spent;
    }

    // Footprints come from Renderer.localBounds and the transform matrix, which do not depend on the cell being active.
    static void Inspect(Cell c, int children)
    {
        var go = c.Root.gameObject;
        bool eligible = true;
        var reasons = new HashSet<string>();
        void Reject(string reason, Component what, string detail)
        {
            eligible = false;
            if (!reasons.Add(reason)) return;
            Rejects[reason] = Rejects.GetValueOrDefault(reason) + 1;
            if (!RejectExample.ContainsKey(reason))
                RejectExample[reason] = $"cell {c.Sx},{c.Sz} '{what?.name}' {what?.GetIl2CppType().Name} {detail}";
        }
        // Canvas, reflection probes and terrain affect rendering outside their own bounds. Typed queries are filtered
        // natively, so only the few matching components cross into managed code.
        foreach (var x in go.GetComponentsInChildren<Canvas>(true)) { Reject("canvas", x, ""); break; }
        foreach (var x in go.GetComponentsInChildren<ReflectionProbe>(true)) { Reject("reflectionProbe", x, ""); break; }
        foreach (var x in go.GetComponentsInChildren<Terrain>(true)) { Reject("terrain", x, ""); break; }
        foreach (var light in go.GetComponentsInChildren<Light>(true))
        {
            if (light.type == LightType.Directional) { Reject("directionalLight", light, ""); continue; }
            var p = light.transform.position; float r = light.range;
            if (!SectionCullState.FootprintInRing(p.x - r, p.x + r, p.z - r, p.z + r, c.Sx, c.Sz, _w, _h))
                Reject("lightOutsideRing", light, FormattableString.Invariant($"pos=({p.x:F1},{p.z:F1}) range={r:F1}"));
        }

        var renderers = go.GetComponentsInChildren<Renderer>(true);
        var old = c.Renderers; var oldForce = c.OriginalForce; var oldHidden = c.Hidden;
        var list = new List<Renderer>(renderers.Length);
        foreach (var r in renderers) if (r != null) list.Add(r);
        int n = list.Count;
        var force = new bool[n]; var hidden = new bool[n]; var rects = new int[n * 4];
        for (int i = 0; i < n; i++)
        {
            var r = list[i];
            int j = Array.FindIndex(old, o => o != null && o.Pointer == r.Pointer);
            force[i] = j >= 0 ? oldForce[j] : r.forceRenderingOff;
            hidden[i] = j >= 0 && oldHidden[j];
            rects[i * 4] = -1;
            if (!Footprint(r, out float minX, out float maxX, out float minZ, out float maxZ, out bool batch, out bool empty))
            { if (!empty) Reject("noFootprint", r, ""); continue; }
            if (!SectionCullState.FootprintInRing(minX, maxX, minZ, maxZ, c.Sx, c.Sz, _w, _h))
                Reject(batch ? "batchOutsideRing" : "rendererOutsideRing", r,
                    FormattableString.Invariant($"x=[{minX:F1},{maxX:F1}] z=[{minZ:F1},{maxZ:F1}]"));
            // Batches are filtered per source model by BatchFogCulling; oversized renderers are never hidden.
            if (batch || maxX - minX > MaxRendererUnits || maxZ - minZ > MaxRendererUnits) continue;
            if (SectionCullState.TileRange(minX, maxX, _w, 1, out int x0, out int x1) &&
                SectionCullState.TileRange(minZ, maxZ, _h, 1, out int z0, out int z1))
            { rects[i * 4] = x0; rects[i * 4 + 1] = z0; rects[i * 4 + 2] = x1; rects[i * 4 + 3] = z1; }
        }
        c.Renderers = list.ToArray(); c.OriginalForce = force; c.Hidden = hidden; c.Rects = rects;
        c.ChildCount = children; c.Eligible = eligible; c.Inspected = true;
        for (int i = 0; i < n; i++) WriteFlag(c, i); // keeps flags of renderers we knew; new ones start drawn
    }

    // empty: a mesh renderer without vertices (empty road segments) draws nothing wherever it is.
    static bool Footprint(Renderer r, out float minX, out float maxX, out float minZ, out float maxZ, out bool batch, out bool empty)
    {
        batch = empty = false;
        minX = maxX = minZ = maxZ = 0;
        var mr = r.TryCast<MeshRenderer>();
        if (mr != null)
        {
            var filter = r.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || mesh.vertexCount == 0) { empty = true; return false; }
            if (BatchFogCulling.TryExtent(mesh.Pointer, out minX, out maxX, out minZ, out maxZ)) { batch = true; return true; }
            return Transformed(r.localBounds, r.transform.localToWorldMatrix, out minX, out maxX, out minZ, out maxZ);
        }
        var skinned = r.TryCast<SkinnedMeshRenderer>();
        if (skinned != null)
        {
            var space = skinned.rootBone != null ? skinned.rootBone : r.transform;
            return Transformed(skinned.localBounds, space.localToWorldMatrix, out minX, out maxX, out minZ, out maxZ);
        }
        // Particles, lines, trails, sprites: no stored local bounds; assume half a cell around the transform.
        var p = r.transform.position;
        minX = p.x - LooseRadius; maxX = p.x + LooseRadius; minZ = p.z - LooseRadius; maxZ = p.z + LooseRadius;
        return float.IsFinite(p.x) && float.IsFinite(p.z);
    }

    static bool Transformed(Bounds b, Matrix4x4 m, out float minX, out float maxX, out float minZ, out float maxZ)
    {
        var c = m.MultiplyPoint3x4(b.center); var e = b.extents;
        float ex = Math.Abs(m.m00) * e.x + Math.Abs(m.m01) * e.y + Math.Abs(m.m02) * e.z;
        float ez = Math.Abs(m.m20) * e.x + Math.Abs(m.m21) * e.y + Math.Abs(m.m22) * e.z;
        minX = c.x - ex; maxX = c.x + ex; minZ = c.z - ez; maxZ = c.z + ez;
        return float.IsFinite(minX) && float.IsFinite(maxX) && float.IsFinite(minZ) && float.IsFinite(maxZ) && (ex > 0 || ez > 0);
    }

    // Recompute both layers from the current summed-area table. Cheap: four reads per renderer, no interop reads.
    static void Reclassify()
    {
        if (_sat.Length == 0) return;
        Deep.Clear(); _hiddenRenderers = 0;
        for (int i = 0; i < _cells.Length; i++)
        {
            var c = _cells[i];
            if (!c.Inspected) continue;
            c.Deep = _cellLayer && c.Eligible && SectionCullState.CellInDeepFog(_sat, _w, _h, c.Sx, c.Sz);
            if (c.Deep) Deep.Add(i);
            for (int k = 0; k < c.Renderers.Length; k++)
            {
                int x0 = c.Rects[k * 4];
                bool hide = x0 >= 0 && !SectionCullState.RectHasReveal(_sat, _w, x0, c.Rects[k * 4 + 1], c.Rects[k * 4 + 2], c.Rects[k * 4 + 3]);
                if (hide) _hiddenRenderers++;
                if (hide == c.Hidden[k]) continue;
                c.Hidden[k] = hide;
                WriteFlag(c, k);
            }
        }
    }

    static void WriteFlag(Cell c, int i)
    {
        var r = c.Renderers[i];
        if (r == null) return;
        bool want = c.Hidden[i] || c.OriginalForce[i];
        if (r.forceRenderingOff == want) return;
        r.forceRenderingOff = want;
        _writes++;
    }

    static void RestoreAll()
    {
        Deep.Clear(); _hiddenRenderers = 0;
        foreach (var c in _cells)
        {
            if (c == null) continue;
            for (int i = 0; i < c.Hidden.Length; i++) { if (!c.Hidden[i]) continue; c.Hidden[i] = false; try { WriteFlag(c, i); } catch (Exception) { } }
        }
        try { BatchFogCulling.ShowAll(); } catch (Exception) { }
    }

    static void Fail(string where, Exception e)
    {
        _errors++;
        if (_errors <= 3) Plugin.I.Log.LogWarning($"fog culling released ({where}): {e.Message}");
        try { RestoreAll(); } catch (Exception) { }
        _cellLayer = false;
    }

    public static void Report()
    {
        if (!_installed || (_cells.Length == 0 && _grids.Count == 0)) return;
        int inspected = 0, eligible = 0, revealed = 0;
        foreach (var c in _cells) { if (c.Inspected) inspected++; if (c.Eligible) eligible++; }
        foreach (bool t in _revealed) if (t) revealed++;
        double ms = 1000.0 / Stopwatch.Frequency;
        Plugin.I.Log.LogInfo(FormattableString.Invariant(
            $"fog: cells={_cells.Length} inspected={inspected} eligible={eligible} deepFogCells={Deep.Count} cellLayer={_cellLayer} hiddenRenderers={_hiddenRenderers} revealedTiles={revealed}/{_revealed.Length} fogChanges={_fogChanges} forceWrites={_writes} swaps={_swapCalls} pollMaxMs={_pollMax * ms:F3} pollTotalMs={_pollTicks * ms:F1} inspectMs={_inspectTicks * ms:F1} errors={_errors}"));
        // Orientation check: the revealed area in world units should sit around the camera's view.
        int rx0 = int.MaxValue, rx1 = -1, rz0 = int.MaxValue, rz1 = -1;
        for (int t = 0; t < _revealed.Length; t++)
            if (_revealed[t]) { int x = t % _w, z = t / _w; rx0 = Math.Min(rx0, x); rx1 = Math.Max(rx1, x); rz0 = Math.Min(rz0, z); rz1 = Math.Max(rz1, z); }
        var cam = Camera.main;
        if (rx1 >= 0 && cam != null)
        {
            var cp = cam.transform.position; var cf = cam.transform.forward;
            float k = cf.y < -0.01f ? -cp.y / cf.y : 0; // where the view centre meets the ground plane
            Plugin.I.Log.LogInfo(FormattableString.Invariant(
                $"fog: revealed world x=[{rx0 - _w / 2f - 0.5f:F1},{rx1 - _w / 2f + 0.5f:F1}] z=[{rz0 - _h / 2f - 0.5f:F1},{rz1 - _h / 2f + 0.5f:F1}] viewCentre=({cp.x + cf.x * k:F1},{cp.z + cf.z * k:F1})"));
        }
        if (!_rejectsLogged && inspected == _cells.Length && _cells.Length > 0)
        {
            _rejectsLogged = true;
            foreach (var kv in Rejects) Plugin.I.Log.LogInfo($"fog: ineligible[{kv.Key}]={kv.Value} cells, e.g. {RejectExample[kv.Key]}");
        }
        BatchFogCulling.Report();
        _pollTicks = _pollMax = _inspectTicks = 0; _swapCalls = 0; _fogChanges = 0;
    }
}
