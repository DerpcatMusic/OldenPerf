using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace OldenPerf;

// OldenPerf core. Only features that change what the game renders or holds in memory, each verified against this build:
//   Fog       - nothing under fog is drawn, activated or simulated (FogCulling, BatchFogCulling)
//   Textures  - mip streaming whose budget tracks Unity's desired texture memory (TextureBudget)
//   Memory    - full-precision depth copy without the unused stencil (NativeDepth)
//   Frame     - background frame cap
// Diagnostics are passive (Stats); the destructive pass ablation runs only when AblateMethods is set.
[BepInPlugin("derpcat.oldenperf", "OldenPerf", "0.5.0")]
public class Plugin : BasePlugin
{
    internal static Plugin I;
    internal ConfigEntry<bool> FogCullingOn, WatchConfig;
    internal ConfigEntry<bool> SsaoHalfResolution;
    internal ConfigEntry<int> FpsCap, BackgroundFps, MipLimit, StatsSeconds, MaxQueuedFrames;
    internal string OutDir;
    internal bool Streaming;
    internal int StreamMaxReduction;

    public override void Load()
    {
        I = this;
        OutDir = Path.Combine(Paths.BepInExRootPath, "oldenperf");
        Directory.CreateDirectory(OutDir);

        FogCullingOn = Config.Bind("Fog", "Culling", true, "Draw nothing under fog of war: deep-fog map cells stay inactive through the game's own culler, frontier renderers and combined prop models under fog are hidden. Can be toggled live with Diagnostics.WatchConfig.");
        Streaming = Config.Bind("Textures", "Streaming", true, "Mip streaming with a budget that tracks Unity's desired texture memory plus headroom: every mip the view needs stays loaded, unused high mips are evicted. Requires restart.").Value;
        StreamMaxReduction = Config.Bind("Textures", "StreamingMaxReduction", 2, "Lowest mip level streaming may fall back to under memory pressure (2 = quarter resolution). With the tracking budget this is a safety floor, not a target. Requires restart.").Value;
        MipLimit = Config.Bind("Textures", "GlobalMipLimit", 0, "QualitySettings.globalTextureMipmapLimit (0 = full resolution).");
        bool trimDepth = Config.Bind("Memory", "TrimDepthCopy", true, "Sampled depth copy without the unused stencil, full D32 precision. Enables only after a full-frame float-depth equality check. Requires restart.").Value;
        FpsCap = Config.Bind("Frame", "FpsCap", 0, "Frame cap while focused. 0 = leave the game's setting alone.");
        BackgroundFps = Config.Bind("Frame", "BackgroundFps", 15, "Frame cap while unfocused. 0 = off.");
        MaxQueuedFrames = Config.Bind("Frame", "MaxQueuedFrames", 0, "Frames the CPU may run ahead of the GPU (QualitySettings.maxQueuedFrames). 0 = leave the game's setting (2). Measured at 4K: 1 left the GPU 17-60% idle (21-25 ms frames, 50 ms spikes) vs 96-99% busy at 2 (20 ms).");
        StatsSeconds = Config.Bind("Diagnostics", "StatsSeconds", 10, "Passive frame-time, render-counter, texture-memory and fog report interval. 0 = off.");
        WatchConfig = Config.Bind("Diagnostics", "WatchConfig", false, "Reload this file when it changes (live A/B of Fog.Culling). Other switches require restart.");
        string ablate = Config.Bind("Profile", "AblateMethods", "", "Destructive measurement: skip the listed render methods in alternating 4 s windows on the map and log whole-frame deltas. Empty = off. Requires restart.").Value;

        if (ablate.Length > 0) Ablation.Init(new HarmonyLib.Harmony("derpcat.oldenperf.ablate"), ablate, 4f, 2);
        RenderCounters.Init();
        if (FogCullingOn.Value) FogCulling.Init();
        if (trimDepth) NativeDepth.Init(new HarmonyLib.Harmony("derpcat.oldenperf.native-depth"));
        SsaoHalfResolution = Config.Bind("Ssao", "HalfResolution", true, "Render ambient occlusion through the game's own half-resolution path (its SSAO Low path) when SSAO is set to High. AO is blurred and low-frequency; at 4K this is visually equivalent and roughly halves its GPU cost. Toggle live with WatchConfig.");
        SsaoHalfRes.Init(new HarmonyLib.Harmony("derpcat.oldenperf.ssao"));
        IconSprites.Init(new HarmonyLib.Harmony("derpcat.oldenperf.icon-sprites"));
        if (StatsSeconds.Value > 0) UiProfile.Init(new HarmonyLib.Harmony("derpcat.oldenperf.ui-profile"));

        ClassInjector.RegisterTypeInIl2Cpp<Runner>();
        var go = new GameObject("OldenPerf");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<Runner>();
        Log.LogInfo("OldenPerf 0.5.0 loaded");
    }
}

public class Runner : MonoBehaviour
{
    public Runner(IntPtr p) : base(p) { }

    bool _bgCapped;
    int _queueLogs;
    int _savedTfr = -1, _savedVsync;
    float _nextConfigCheck;
    DateTime _configWrite;
    bool _configSeen, _configWatchFailed;

    void OnDestroy() => RenderCounters.Dispose();

    void Start()
    {
        TextureBudget.Start(Plugin.I.Streaming, Plugin.I.StreamMaxReduction);
        Plugin.I.Log.LogInfo($"frame pacing at start: maxQueuedFrames={QualitySettings.maxQueuedFrames} vSyncCount={QualitySettings.vSyncCount} targetFrameRate={Application.targetFrameRate}");
    }

    void Update()
    {
        var p = Plugin.I;
        float now = Time.realtimeSinceStartup;
        if (p.WatchConfig.Value && !_configWatchFailed && now >= _nextConfigCheck)
        {
            _nextConfigCheck = now + 1;
            try
            {
                var modified = File.GetLastWriteTimeUtc(p.Config.ConfigFilePath);
                if (_configSeen && modified != _configWrite) { p.Config.Reload(); Stats.ExcludeDiagnostics(); p.Log.LogInfo("config reloaded"); }
                _configWrite = modified; _configSeen = true;
            }
            catch (Exception e) { _configWatchFailed = true; p.Log.LogWarning("config watcher stopped: " + e.Message); }
        }

        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        FogCulling.Tick();
        TextureBudget.Tick(now);
        NativeDepth.Tick(scene);
        if (p.StatsSeconds.Value > 0) PostFxDump.Tick(scene, Stats.SceneSeconds);
        if (Ablation.Active)
        {
            if (Application.isFocused && scene.StartsWith("world", StringComparison.Ordinal)) Ablation.Tick(now, Time.unscaledDeltaTime);
            else Ablation.Pause();
        }
        Stats.Tick(scene, now, p.StatsSeconds.Value);

        bool bg = !Application.isFocused && p.BackgroundFps.Value > 0;
        if (bg && !_bgCapped) { _savedTfr = Application.targetFrameRate; _savedVsync = QualitySettings.vSyncCount; _bgCapped = true; }
        if (!bg && _bgCapped) { _bgCapped = false; Application.targetFrameRate = _savedTfr; QualitySettings.vSyncCount = _savedVsync; }
        int want = bg ? p.BackgroundFps.Value : p.FpsCap.Value;
        if (want > 0 && Application.targetFrameRate != want) { Application.targetFrameRate = want; QualitySettings.vSyncCount = 0; }
        if (p.MaxQueuedFrames.Value > 0 && QualitySettings.maxQueuedFrames != p.MaxQueuedFrames.Value)
        {
            if (_queueLogs++ < 3) p.Log.LogInfo($"maxQueuedFrames {QualitySettings.maxQueuedFrames} -> {p.MaxQueuedFrames.Value}");
            QualitySettings.maxQueuedFrames = p.MaxQueuedFrames.Value;
        }
        if (p.MipLimit.Value >= 0 && QualitySettings.globalTextureMipmapLimit != p.MipLimit.Value)
            QualitySettings.globalTextureMipmapLimit = p.MipLimit.Value;
    }
}
