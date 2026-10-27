using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OldenPerf;

// Hex.Storage.IconData.kab() decodes stored PNG bytes and calls Sprite.Create(texture, rect, pivot, pixelsPerUnit), whose
// default SpriteMeshType.Tight traces an outline mesh from the alpha channel (SpriteMeshGenerator). Profiled on the first
// Alt press after loading a map: about a third of a 1 s main-thread stall was that tracing. Inside kab only, the same call
// is made with FullRect: same texture, rect, pivot and scale; transparent texels stay transparent, so the image is unchanged.
static class IconSprites
{
    [ThreadStatic] static bool _inIcon;
    static int _created;

    public static void Init(Harmony harmony)
    {
        try
        {
            var kab = typeof(Hex.Storage.IconData).GetMethod("kab", Type.EmptyTypes) ?? throw new MissingMethodException("IconData.kab");
            var create = typeof(Sprite).GetMethod("Create", new[] { typeof(Texture2D), typeof(Rect), typeof(Vector2), typeof(float) })
                ?? throw new MissingMethodException("Sprite.Create(Texture2D, Rect, Vector2, float)");
            HarmonyMethod Hook(string n) => new(typeof(IconSprites).GetMethod(n, BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(kab, prefix: Hook(nameof(Enter)), finalizer: Hook(nameof(Leave)));
            harmony.Patch(create, prefix: Hook(nameof(Create)));
            Plugin.I.Log.LogInfo("icon sprites: stored icons are created with full-rect meshes (no outline tracing)");
        }
        catch (Exception e) { harmony.UnpatchSelf(); Plugin.I.Log.LogWarning("icon sprites unavailable: " + e.Message); }
    }

    static void Enter() => _inIcon = true;
    static void Leave() => _inIcon = false;

    static bool Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit, ref Sprite __result)
    {
        if (!_inIcon) return true;
        __result = Sprite.Create(texture, rect, pivot, pixelsPerUnit, 0, SpriteMeshType.FullRect);
        if (_created++ == 0) Plugin.I.Log.LogInfo($"icon sprites: first full-rect icon {rect.width}x{rect.height}");
        return false;
    }
}
