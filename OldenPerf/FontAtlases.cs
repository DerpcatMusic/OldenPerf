using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace OldenPerf;

// The game loads every language's TextMeshPro font at startup and keeps all atlases in VRAM: in an English game the
// Chinese, Japanese and Korean atlases alone are 224 MiB (six 8192x4096 and two 4096x4096 Alpha8 textures). They can't
// be dropped, because other players' names may need those glyphs. The atlases hold signed distance fields, which the
// text shader reads with normalized UVs, so a half-size copy draws the same glyphs with slightly softer corners at a
// quarter of the memory. During the first loading screen (where a stall is hidden), every large static atlas that no
// text on screen uses as its main font is replaced by a half-size copy and the original is unloaded.
static class FontAtlases
{
    const int MinSide = 4096;
    static bool _done;

    public static void Tick(string scene)
    {
        if (_done || scene != "loadingScene") return;
        _done = true;
        try { Shrink(); }
        catch (Exception e) { Plugin.I.Log.LogWarning("font atlases unchanged: " + e.Message); }
    }

    static void Shrink()
    {
        // Fonts some text uses directly are the current language's fonts: leave those at full size.
        var primary = new HashSet<IntPtr>();
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            if (text != null && text.font != null) primary.Add(text.font.Pointer);

        var materials = Resources.FindObjectsOfTypeAll<Material>();
        long freed = 0;
        foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font == null || primary.Contains(font.Pointer) || font.atlasPopulationMode != AtlasPopulationMode.Static) continue;
            var atlases = font.atlasTextures;
            if (atlases == null || atlases.Length != 1) continue;
            var old = atlases[0];
            if (old == null || old.format != TextureFormat.Alpha8 || Math.Max(old.width, old.height) < MinSide) continue;

            var small = HalfSize(old);
            atlases[0] = small;
            font.m_AtlasTexture = small;
            foreach (var m in materials)
                if (m != null && m.HasProperty("_MainTex") && m.mainTexture != null && m.mainTexture.Pointer == old.Pointer) m.mainTexture = small;
            freed += (long)old.width * old.height * 3 / 4;
            Resources.UnloadAsset(old);
        }
        if (freed > 0) Plugin.I.Log.LogInfo($"font atlases: other-language atlases at half size, {freed / 1048576} MiB freed");
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
