using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace OldenPerf;

// Verified native bzc.pyt/ib/mw/lt/lty build one mesh per section/material. bzc.pyu/hpb appends
// a source model's indices and stores its instance ID in the vertex stream. The actual position is
// pulled by the shader from separate GraphicsBuffers, so generated Mesh.bounds is deliberately huge.
// Capture each source model's true transformed bounds at that builder, then compact the generated
// mesh's index stream on fog changes. This removes primitives before all camera and shadow draws.
// No GPU readback, shader discard, per-frame mesh rebuild, or vertex/instance ID remapping.
static class BatchFogCulling
{
    const int MaxBatchIndices = 1048576;
    const long MaxRetainedIndices = 32L * 1024 * 1024; // 192 MiB max: original, scratch, interop upload (ushort each)
    sealed class Pending
    {
        public readonly Dictionary<ushort, FogBatchIndices.Footprint> Models = new();
    }
    sealed class Batch
    {
        public Mesh Mesh;
        public ushort[] Original, Scratch;
        public Il2CppStructArray<ushort> Upload;
        public FogBatchIndices.Part[] Parts;
        public bool[] Hidden, NextHidden;
        public int Kept;
        public bool Disabled;
    }
    static readonly Dictionary<IntPtr, Pending> Building = new();
    // Source meshes repeat across thousands of placed models; Mesh.bounds is an interop call, so read it once per mesh.
    static readonly Dictionary<IntPtr, Bounds> MeshBounds = new();
    static readonly List<Batch> Batches = new();
    static bool _installed, _dirty;
    static int _revision = -1, _errors, _sourceReadFailures;
    static long _retainedIndices;
    static int _indexOffset, _vertexIdOffset, _instanceOffset;
    static readonly BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    public static void Init()
    {
        var harmony = new Harmony("derpcat.oldenperf.fog-batches");
        try
        {
            if (!BuildIdentity.IsVerified || Marshal.SizeOf<bzc.InstanceData>() != 76)
                throw new InvalidOperationException("unverified batch layout");
            _indexOffset = ArrayOffset("cbsv", 0xB0);
            _vertexIdOffset = ArrayOffset("cbsw", 0xC0);
            _instanceOffset = ArrayOffset("cbsx", 0xD0);
            var methods = typeof(bzc).GetMethods(Declared).Where(m =>
                m.Name is "pyu" or "hpb" or "kdv" or "pyv" or "iye").ToArray();
            if (methods.Length != 5) throw new MissingMethodException("bzc builder aliases");
            var entries = methods.Select(m => NativeIdentity.EntryOf(typeof(bzc), m.Name)).ToArray();
            var kinds = HookPlan.Classify(entries, -1) ?? throw new MissingMethodException("bzc native entries");
            var selected = Enumerable.Range(0, methods.Length).Where(i => kinds[i] != HookPlan.Kind.Alias).ToArray();
            if (!HookPlan.Claim(selected.Select(i => entries[i]))) throw new InvalidOperationException("batch hook ownership");
            HarmonyMethod Hook(string n) => new(typeof(BatchFogCulling).GetMethod(n, BindingFlags.NonPublic | BindingFlags.Static));
            foreach (int i in selected)
            {
                bool part = methods[i].Name is "pyu" or "hpb";
                harmony.Patch(methods[i], prefix: part ? Hook(nameof(BeforePart)) : null,
                    postfix: part ? null : Hook(nameof(BatchCreated)));
            }
            _installed = true;
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf();
            Plugin.I.Log.LogWarning("batch fog unavailable: " + e.Message);
        }
    }

    static int ArrayOffset(string name, int expected)
    {
        var field = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<bzc>.NativeClassPtr, name);
        if (field == IntPtr.Zero) throw new MissingFieldException("bzc." + name);
        int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
        if (offset != expected) throw new InvalidOperationException($"bzc.{name} offset {offset:X}, expected {expected:X}");
        return offset;
    }

    // The interop NativeArray<T> property boxes a value type and failed with NullReferenceException
    // in the live build. These arrays are embedded fields in bzc. Read their verified native headers
    // directly, without creating a boxed generic NativeArray wrapper for every source model.
    static (IntPtr Buffer, int Length) ReadArray(bzc owner, int offset, int capacity)
    {
        if (!NativeBatchArray.TryRead(owner.Pointer, offset, capacity, out var buffer, out int length))
            throw new InvalidOperationException($"native array +{offset:X}: null={buffer == IntPtr.Zero}, length={length}, cap={capacity}");
        return (buffer, length);
    }

    static unsafe void BeforePart(bzc __instance, Mesh __0, ushort __3)
    {
        if (!_installed || !Plugin.I.FogCullingOn.Value) return;
        Pending p = null;
        try
        {
            if (__3 == 0 || !Building.TryGetValue(__instance.Pointer, out p))
                Building[__instance.Pointer] = p = new Pending();
            p.Models[__3] = default;
            var data = ReadArray(__instance, _instanceOffset, 4096);
            if (__3 >= data.Length) return;
            var instance = ((bzc.InstanceData*)data.Buffer)[__3];
            var m = instance.objectToWorld;
            if (!MeshBounds.TryGetValue(__0.Pointer, out var b)) MeshBounds[__0.Pointer] = b = __0.bounds;
            var c = b.center; var e = b.extents;
            float x = m.m00 * c.x + m.m01 * c.y + m.m02 * c.z + m.m03;
            float z = m.m20 * c.x + m.m21 * c.y + m.m22 * c.z + m.m23;
            // Transform the source AABB, then include the exact shader's flag/tree motion envelope.
            // Default nonzero power values are ignored when the corresponding flags are off.
            float ex = Math.Abs(m.m00) * e.x + Math.Abs(m.m01) * e.y + Math.Abs(m.m02) * e.z + 1f;
            float ez = Math.Abs(m.m20) * e.x + Math.Abs(m.m21) * e.y + Math.Abs(m.m22) * e.z + 1f;
            float relativeHeight = Math.Abs(m.m10 * c.x + m.m11 * c.y + m.m12 * c.z) +
                Math.Abs(m.m10) * e.x + Math.Abs(m.m11) * e.y + Math.Abs(m.m12) * e.z;
            bool cullable = FogBatchIndices.TryDeformationPadding(instance.props.flags,
                (float)BitConverter.UInt16BitsToHalf(instance.flagPowX),
                (float)BitConverter.UInt16BitsToHalf(instance.flagPowY),
                (float)BitConverter.UInt16BitsToHalf(instance.flagPowZ), relativeHeight, out float padX, out float padZ) &&
                float.IsFinite(x) && float.IsFinite(z) && float.IsFinite(ex) && float.IsFinite(ez) && ex > 0 && ez > 0;
            ex += padX; ez += padZ;
            p.Models[__3] = new(x - ex, x + ex, z - ez, z + ez, cullable);
        }
        catch (Exception e)
        {
            _sourceReadFailures++;
            if (_sourceReadFailures <= 2) Plugin.I.Log.LogWarning("batch fog source bounds unavailable: " + e);
        }
    }


    // A batch that fails any check below is left untouched (drawn in full).
    static unsafe void BatchCreated(bzc __instance, Mesh __1)
    {
        if (!_installed || !Plugin.I.FogCullingOn.Value) return;
        if (!Building.Remove(__instance.Pointer, out var p)) return;
        try
        {
            int count = checked((int)__1.GetIndexCount(0));
            // cbsv (+0xB0) is the index stream; cbsw (+0xC0) holds per-vertex instance IDs.
            var native = ReadArray(__instance, _indexOffset, MaxBatchIndices);
            if (__1.subMeshCount != 1 || __1.indexFormat != IndexFormat.UInt16 || __1.GetTopology(0) != MeshTopology.Triangles) return;
            if (count <= 0 || count > MaxBatchIndices || native.Length < count) return;
            if (_retainedIndices + count > MaxRetainedIndices) return;
            var ids = ReadArray(__instance, _vertexIdOffset, 65536);
            int vertices = __1.vertexCount;
            if (ids.Length < vertices || vertices <= 0 || vertices > 65536) return;
            var original = new ushort[count];
            new ReadOnlySpan<ushort>(native.Buffer.ToPointer(), count).CopyTo(original);
            var vertexInstances = new ushort[vertices];
            new ReadOnlySpan<ushort>(ids.Buffer.ToPointer(), vertices).CopyTo(vertexInstances);
            if (!FogBatchIndices.TryMap(original, vertexInstances, p.Models, out var parts) || !FogBatchIndices.Valid(original, parts, vertices)) return;
            if (Batches.Any(b => b.Mesh != null && b.Mesh.Pointer == __1.Pointer)) return;
            Batches.Add(new Batch { Mesh = __1, Original = original, Scratch = new ushort[count],
                Upload = new Il2CppStructArray<ushort>(count), Parts = parts, Hidden = new bool[parts.Length],
                NextHidden = new bool[parts.Length], Kept = count });
            _retainedIndices += count; _dirty = true;
        }
        catch (Exception e) { _errors++; if (_errors <= 2) Plugin.I.Log.LogWarning("batch fog capture refused: " + e); }
    }

    public static void Update(int[] sat, int width, int height, int revision)
    {
        if (!_installed || (!_dirty && revision == _revision)) return;
        _dirty = false; _revision = revision;
        for (int i = Batches.Count - 1; i >= 0; i--)
        {
            var b = Batches[i];
            if (b.Mesh == null) { _retainedIndices -= b.Original.Length; Batches.RemoveAt(i); continue; }
            if (b.Disabled) continue;
            try
            {
                FogBatchIndices.Classify(b.Parts, sat, width, height, b.NextHidden);
                bool changed = false;
                for (int j = 0; j < b.Hidden.Length; j++) if (b.Hidden[j] != b.NextHidden[j]) { changed = true; break; }
                if (changed)
                {
                    int kept = FogBatchIndices.CopyVisible(b.Original, b.Parts, b.Scratch, b.NextHidden);
                    Submit(b, b.Scratch, kept);
                    Array.Copy(b.NextHidden, b.Hidden, b.Hidden.Length);
                    b.Kept = kept;
                }
            }
            catch (Exception e)
            {
                _errors++; b.Disabled = true;
                try { Submit(b, b.Original, b.Original.Length); b.Kept = b.Original.Length; }
                catch (Exception restore) { Plugin.I.Log.LogError("batch fog restore failed: " + restore.Message); }
                Plugin.I.Log.LogWarning("batch fog released: " + e.Message);
            }
        }
    }

    static void Submit(Batch b, ushort[] indices, int count)
    {
        indices.AsSpan(0, count).CopyTo(b.Upload.AsSpan());
        // calculateBounds=false is essential: shader positions live in external buffers, not this mesh's vertex data.
        b.Mesh.SetIndices(b.Upload, 0, count, MeshTopology.Triangles, 0, false, 0);
    }

    // A new map: source-mesh pointers from the previous map may be reused by other meshes.
    public static void ForgetMeshBounds() => MeshBounds.Clear();

    public static void ShowAll()
    {
        foreach (var b in Batches)
        {
            if (b.Mesh == null || b.Kept == b.Original.Length) continue;
            try { Submit(b, b.Original, b.Original.Length); b.Kept = b.Original.Length; Array.Clear(b.Hidden, 0, b.Hidden.Length); }
            catch (Exception e) { _errors++; Plugin.I.Log.LogError("batch fog restore failed: " + e.Message); }
        }
        _revision = -1; _dirty = true;
    }

    // The true world extent of a captured batch: the union of its source-model footprints. The generated mesh's own bounds are
    // deliberately huge, so FogCulling asks here before deciding whether the batch's cell can be switched off as a whole.
    // Unknown batches, unbounded (deforming) models and mixed-instance ranges report false and keep the cell on.
    public static bool TryExtent(IntPtr mesh, out float minX, out float maxX, out float minZ, out float maxZ)
    {
        minX = minZ = float.MaxValue; maxX = maxZ = float.MinValue;
        foreach (var b in Batches)
        {
            if (b.Mesh == null || b.Mesh.Pointer != mesh) continue;
            if (b.Disabled || b.Parts.Length == 0) return false;
            foreach (var part in b.Parts)
            {
                if (!part.Cullable) return false;
                minX = Math.Min(minX, part.MinX); maxX = Math.Max(maxX, part.MaxX);
                minZ = Math.Min(minZ, part.MinZ); maxZ = Math.Max(maxZ, part.MaxZ);
            }
            return true;
        }
        return false;
    }
}
