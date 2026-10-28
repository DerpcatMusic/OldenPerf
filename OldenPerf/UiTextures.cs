using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace OldenPerf;

// The game keeps almost every texture it ships loaded: on the map 7,600 of 8,900. UI art has no mips, so each
// sprite page sits in VRAM at full size even when no window shows it (portraits, town screens, artifact and spell
// icons, dialogs). Unity reloads an unloaded on-disk asset the next time something dereferences it, and UI reaches
// these pages only through Sprite objects (Image -> sprite -> texture). So a sprite page that no active UI element
// has used for IdleSeconds is unloaded; when a window that needs it opens, the Image rebuild reloads it from disk.
// Runtime-created textures, readable textures, mipped (streamed) textures and font atlases are never touched.
static class UiTextures
{
    const float ScanSeconds = 5f, IdleSeconds = 30f, LoadScanSeconds = 1f;
    const long MinBytes = 256 * 1024;
    sealed class Page { public Texture2D Tex; public float Used; public long Bytes; }
    static readonly HashSet<int> Pinned = new();
    static readonly HashSet<int> SeenSprites = new();
    static readonly Dictionary<int, Page> Loaded = new();   // instance ID -> resident sprite page
    static readonly Dictionary<int, long> Unloaded = new(); // instance ID -> bytes, until something uses it again
    static string _scene;
    static float _next;
    static double _scanMaxMs;
    static long _reloaded;

    public static void Tick(string scene)
    {
        if (!Plugin.I.UiTextureResidency.Value) return;
        try
        {
            float now = Time.realtimeSinceStartup;
            bool loading = scene == "loadingScene";
            if (scene != _scene)
            {
                _scene = scene;
                if (!loading) { CollectPages(); return; } // first frame of a scene: stall hidden by the load
                // During a load: drop every page the loading screen does not show, once a second, including pages the
                // incoming scene has just loaded (its window prefabs pull in ~730 MiB of icon atlases). Unity loads and
                // uploads them before the map allocates its own buffers; freeing them as they arrive keeps that peak from
                // spilling the map's buffers into system memory. The HUD reloads the few it shows on the first frame.
                _next = 0;
            }
            else if (now < _next) return;
            _next = now + (loading ? LoadScanSeconds : ScanSeconds);
            if (loading) CollectPages(log: false);
            float idleBefore = loading ? now : now - IdleSeconds;
            var sw = Stopwatch.StartNew();
            // Unsorted: FindObjectsOfType sorts every result by instance ID, a third of the scan on the map.
            foreach (var g in UnityEngine.Object.FindObjectsByType<Graphic>(FindObjectsSortMode.None)) Use(g != null ? g.mainTexture : null, now);
            foreach (var r in UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None)) Use(r != null && r.sprite != null ? r.sprite.texture : null, now);
            long freed = 0; int n = 0;
            List<int> idle = null;
            foreach (var kv in Loaded) if (kv.Value.Used < idleBefore) (idle ??= new()).Add(kv.Key);
            if (idle != null)
                foreach (int id in idle)
                {
                    var p = Loaded[id];
                    Loaded.Remove(id);
                    if (p.Tex == null) continue;
                    Resources.UnloadAsset(p.Tex);
                    Unloaded[id] = p.Bytes; freed += p.Bytes; n++;
                }
            _scanMaxMs = Math.Max(_scanMaxMs, sw.Elapsed.TotalMilliseconds);
            if (n > 0)
            {
                long out_ = 0; foreach (var b in Unloaded.Values) out_ += b;
                Plugin.I.Log.LogInfo($"ui textures: unloaded {n} idle sprite pages ({freed / 1048576} MiB); {out_ / 1048576} MiB not resident, {_reloaded / 1048576} MiB reloaded and kept; scan max {_scanMaxMs:F1} ms");
            }
        }
        catch (Exception e) { Plugin.I.UiTextureResidency.Value = false; Plugin.I.Log.LogWarning("ui textures: residency off: " + e.Message); }
    }

    static void Use(Texture t, float now)
    {
        if (t == null) return;
        int id = t.GetInstanceID();
        if (Loaded.TryGetValue(id, out var p)) { p.Used = now; return; }
        if (!Unloaded.Remove(id, out long b)) return;
        // Reloaded because a window needed it: the reload is a synchronous disk read (a town's pages took 1.5 s), so a page
        // this session has shown once stays resident from then on.
        Pinned.Add(id);
        _reloaded += b;
    }

    // Sprite pages that are on-disk assets (positive instance ID), not readable, single mip, not a font atlas.
    static void CollectPages(bool log = true)
    {
        var sw = Stopwatch.StartNew();
        float now = Time.realtimeSinceStartup;
        // TextMeshPro inline-sprite sheets are bound through materials, not through a Graphic: never unload them.
        foreach (var a in Resources.FindObjectsOfTypeAll<TMPro.TMP_SpriteAsset>())
            if (a != null && a.spriteSheet != null) Pinned.Add(a.spriteSheet.GetInstanceID());
        var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
        double findMs = sw.Elapsed.TotalMilliseconds;
        foreach (var s in sprites)
        {
            // Sprite.texture loads an unloaded page from disk, so each sprite is read once. Its page is then tracked,
            // pinned or ineligible; reading it again on the next scene would bring every unloaded page back.
            if (s == null || !SeenSprites.Add(s.GetInstanceID())) continue;
            var t = s.texture;
            if (t == null) continue;
            int id = t.GetInstanceID();
            if (id <= 0 || Pinned.Contains(id) || Loaded.ContainsKey(id) || t.isReadable || t.mipmapCount != 1 || t.format == TextureFormat.Alpha8) continue;
            long bytes = Bytes(t);
            if (bytes < MinBytes) continue;
            Unloaded.Remove(id);
            Loaded[id] = new Page { Tex = t, Used = now, Bytes = bytes };
        }
        if (!log) return;
        long total = 0; foreach (var p in Loaded.Values) total += p.Bytes;
        Plugin.I.Log.LogInfo($"ui textures: {Loaded.Count} sprite pages resident ({total / 1048576} MiB), collected in {sw.Elapsed.TotalMilliseconds:F0} ms ({sprites.Length} sprites, find {findMs:F0} ms)");
    }

    static long Bytes(Texture2D t)
    {
        long blocks = ((t.width + 3L) / 4) * ((t.height + 3L) / 4);
        return t.format switch
        {
            TextureFormat.DXT1 => blocks * 8,
            TextureFormat.DXT5 or TextureFormat.BC7 => blocks * 16,
            _ => (long)t.width * t.height * 4,
        };
    }
}
