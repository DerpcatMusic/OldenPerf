using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace OldenPerf;

// Entry point. Each fix lives in its own file with a comment saying what it changes and why.
[BepInPlugin("derpcat.oldenperf", "OldenPerf", "0.5.0")]
public class Plugin : BasePlugin
{
    internal static Plugin I;
    internal ConfigEntry<bool> FogCullingOn, SsaoHalfResolution;
    internal ConfigEntry<int> BackgroundFps;
    internal string OutDir;

    public override void Load()
    {
        I = this;
        OutDir = System.IO.Path.Combine(Paths.BepInExRootPath, "oldenperf");
        System.IO.Directory.CreateDirectory(OutDir);

        FogCullingOn = Config.Bind("Fog", "Culling", true, "Don't draw, activate or simulate anything under fog of war.");
        SsaoHalfResolution = Config.Bind("Ssao", "HalfResolution", true, "Render SSAO through the game's own half-size path when it is set to High.");
        bool streaming = Config.Bind("Textures", "Streaming", true, "Mip streaming with a budget that follows what the view needs. Restart required.").Value;
        int maxReduction = Config.Bind("Textures", "StreamingMaxReduction", 2, "Lowest mip streaming may fall back to under memory pressure. Restart required.").Value;
        bool trimDepth = Config.Bind("Memory", "TrimDepthCopy", true, "Depth copy without the unused stencil (enabled only after an equality check). Restart required.").Value;
        BackgroundFps = Config.Bind("Frame", "BackgroundFps", 15, "Frame cap while the window is unfocused. 0 = off.");

        if (FogCullingOn.Value) FogCulling.Init();
        if (trimDepth) NativeDepth.Init(new HarmonyLib.Harmony("derpcat.oldenperf.native-depth"));
        SsaoHalfRes.Init(new HarmonyLib.Harmony("derpcat.oldenperf.ssao"));
        IconSprites.Init(new HarmonyLib.Harmony("derpcat.oldenperf.icon-sprites"));

        ClassInjector.RegisterTypeInIl2Cpp<Runner>();
        var go = new GameObject("OldenPerf");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<Runner>();
        Runner.Streaming = streaming; Runner.MaxReduction = maxReduction;
    }
}

public class Runner : MonoBehaviour
{
    public Runner(IntPtr p) : base(p) { }
    internal static bool Streaming;
    internal static int MaxReduction;
    bool _bgCapped;
    int _savedTfr = -1, _savedVsync;

    void Start() => TextureBudget.Start(Streaming, MaxReduction);

    void Update()
    {
        FogCulling.Tick();
        TextureBudget.Tick(Time.realtimeSinceStartup);
        NativeDepth.Tick(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);

        // Background cap: restore the game's own frame settings when focus returns.
        int fps = Plugin.I.BackgroundFps.Value;
        bool bg = !Application.isFocused && fps > 0;
        if (bg && !_bgCapped) { _savedTfr = Application.targetFrameRate; _savedVsync = QualitySettings.vSyncCount; _bgCapped = true; }
        if (!bg && _bgCapped) { _bgCapped = false; Application.targetFrameRate = _savedTfr; QualitySettings.vSyncCount = _savedVsync; }
        if (bg && Application.targetFrameRate != fps) { Application.targetFrameRate = fps; QualitySettings.vSyncCount = 0; }
    }
}
