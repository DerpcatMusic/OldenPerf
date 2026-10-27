using System;
using HarmonyLib;
using Hex.Render;

namespace OldenPerf;

// bew.lox renders SSAO. It reads SSAOQuality from bfu.lvd()._bxra: None skips it, Low renders the AO and its blur at
// pixelWidth/2 x pixelHeight/2 (the shader gets the 1/2 scale and its blur offsets compensate), High renders at full size.
// AO is a low-frequency term that is blurred anyway, so at 4K the half-size path is the game's own designed fast path.
// Inside lox only, High reads as Low; the stored setting is restored right after, so menus and saved settings never change.
static class SsaoHalfRes
{
    static bfq _settings;
    static SSAOQuality _saved;
    static bool _logged;

    public static void Init(Harmony harmony)
    {
        try
        {
            var lox = AccessTools.Method(typeof(bew), "lox") ?? throw new MissingMethodException("bew.lox");
            harmony.Patch(lox, prefix: new HarmonyMethod(typeof(SsaoHalfRes), nameof(Enter)), finalizer: new HarmonyMethod(typeof(SsaoHalfRes), nameof(Leave)));
            Plugin.I.Log.LogInfo($"ssao: half-resolution path {(Plugin.I.SsaoHalfResolution.Value ? "on" : "off")} (Ssao.HalfResolution)");
        }
        catch (Exception e) { Plugin.I.Log.LogWarning("ssao half-res unavailable: " + e.Message); }
    }

    static void Enter()
    {
        _settings = null;
        if (!Plugin.I.SsaoHalfResolution.Value) return;
        var s = bfu.lvd()?._bxra_k__BackingField;
        if (s != null && !_logged) { _logged = true; Plugin.I.Log.LogInfo($"ssao: game setting {s._bxpe_k__BackingField}"); }
        if (s == null || s._bxpe_k__BackingField != SSAOQuality.High) return;
        _settings = s;
        _saved = s._bxpe_k__BackingField;
        s._bxpe_k__BackingField = SSAOQuality.Low;
    }

    static Exception Leave(Exception __exception)
    {
        if (_settings != null) { _settings._bxpe_k__BackingField = _saved; _settings = null; }
        return __exception;
    }
}
