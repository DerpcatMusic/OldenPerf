using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace OldenPerf;

// The game loads every language's TextMeshPro font and keeps all atlases in VRAM: in an English game the Chinese,
// Japanese and Korean atlases alone are 224 MiB (six 8192x4096 and two 4096x4096 Alpha8 textures). They can't be
// dropped, because other players' names may need those glyphs. The atlases hold signed distance fields read through
// normalized UVs, so a half-size copy draws the same glyphs with slightly softer corners at a quarter of the memory.
//
// On every loading screen (where a stall is hidden): a large static atlas whose font no text uses directly is swapped
// for a half-size copy and the original is unloaded; a shrunk font that is now in use gets its original back. Switching
// language mid-game restores at once: the font setter hook sees a text take a shrunk font and reloads the original.
static class FontAtlases
{
    const int MinSide = 4096;
    sealed class Shrunk { public TMP_FontAsset Font; public Texture2D Small; public int OriginalId; }
    static readonly Dictionary<IntPtr, Shrunk> ByFont = new();
    static string _scene;
    static bool _restoreFailed;

    public static void Init(Harmony harmony)
    {
        try
        {
            var setter = AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.font)) ?? throw new MissingMethodException("TMP_Text.font");
            harmony.Patch(setter, postfix: new HarmonyMethod(typeof(FontAtlases), nameof(FontSet)));
        }
        catch (Exception e) { Plugin.I.Log.LogWarning("font atlases: no language-switch hook, restores wait for the next loading screen: " + e.Message); }
    }

    static void FontSet(TMP_FontAsset value)
    {
        if (value != null && ByFont.Count > 0 && ByFont.TryGetValue(value.Pointer, out var s)) Restore(s);
    }

    public static void Tick(string scene)
    {
        if (scene == _scene) return;
        _scene = scene;
        if (scene != "loadingScene") return;
        try { Rebalance(); }
        catch (Exception e) { Plugin.I.Log.LogWarning("font atlases unchanged: " + e.Message); }
    }

    static void Rebalance()
    {
        // Fonts some text uses directly are the current language's fonts: those stay (or come back) at full size.
        var primary = new HashSet<IntPtr>();
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            if (text != null && text.font != null) primary.Add(text.font.Pointer);
        foreach (var s in new List<Shrunk>(ByFont.Values))
            if (primary.Contains(s.Font.Pointer)) Restore(s);

        Material[] materials = null;
        long freed = 0;
        foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font == null || primary.Contains(font.Pointer) || ByFont.ContainsKey(font.Pointer) || font.atlasPopulationMode != AtlasPopulationMode.Static) continue;
            var atlases = font.atlasTextures;
            if (atlases == null || atlases.Length != 1) continue;
            var old = atlases[0];
            if (old == null || old.format != TextureFormat.Alpha8 || Math.Max(old.width, old.height) < MinSide) continue;

            var small = HalfSize(old);
            materials ??= Resources.FindObjectsOfTypeAll<Material>();
            Swap(font, old, small, materials);
            ByFont[font.Pointer] = new Shrunk { Font = font, Small = small, OriginalId = old.GetInstanceID() };
            freed += (long)old.width * old.height * 3 / 4;
            Resources.UnloadAsset(old);
        }
        if (freed > 0) Plugin.I.Log.LogInfo($"font atlases: unused-language atlases at half size, {freed / 1048576} MiB freed");
    }

    // The original is an asset on disk: Unity reloads it from its instance ID.
    static void Restore(Shrunk s)
    {
        if (_restoreFailed) return;
        try
        {
            var original = Resources.InstanceIDToObject(s.OriginalId)?.TryCast<Texture2D>();
            if (original == null) { _restoreFailed = true; Plugin.I.Log.LogWarning("font atlases: original did not reload; fonts stay half size"); return; }
            Swap(s.Font, s.Small, original, Resources.FindObjectsOfTypeAll<Material>());
            ByFont.Remove(s.Font.Pointer);
            UnityEngine.Object.Destroy(s.Small);
            Plugin.I.Log.LogInfo($"font atlases: {s.Font.name} back at full size ({original.width}x{original.height})");
        }
        catch (Exception e) { _restoreFailed = true; Plugin.I.Log.LogWarning("font atlases: restore failed: " + e.Message); }
    }

    static void Swap(TMP_FontAsset font, Texture2D from, Texture2D to, Material[] materials)
    {
        font.atlasTextures[0] = to;
        font.m_AtlasTexture = to;
        foreach (var m in materials)
            if (m != null && m.HasProperty("_MainTex") && m.mainTexture != null && m.mainTexture.Pointer == from.Pointer) m.mainTexture = to;
    }

    // Bilinear blit to half size averages each 2x2 block of distances; the alpha channel is read back into Alpha8.
    static Texture2D HalfSize(Texture2D src)
    {
        int w = src.width / 2, h = src.height / 2;
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        var prev = RenderTexture.active;
        Graphics.Blit(src, rt);
        RenderTexture.active = rt;
        var rgba = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        rgba.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        var px = rgba.GetPixels32(); // Il2Cpp struct array: direct reads, no NativeArray boxing
        var alpha = new byte[w * h];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = px[i].a;
        UnityEngine.Object.Destroy(rgba);

        var dst = new Texture2D(w, h, TextureFormat.Alpha8, false, true) { name = src.name, filterMode = src.filterMode, wrapMode = src.wrapMode };
        dst.LoadRawTextureData(alpha);
        dst.Apply(false, true); // upload, then drop the CPU copy
        return dst;
    }
}
