using System;
using UnityEngine;

namespace OldenPerf;

// Passive statistics: per-frame wall time from unscaledDeltaTime plus Unity's render counters. No method hooks, so the
// measurement costs nothing measurable. Only focused frames after a settling window are counted.
static class Stats
{
    const float SettleSeconds = 3f;
    static readonly SampleWindow Wall = new();
    static string _scene = "";
    static int _width, _height, _ignoreFrames;
    static float _settleUntil, _nextReport;

    public static void ExcludeDiagnostics() => _ignoreFrames = 8;
    public static float SceneSeconds => Time.realtimeSinceStartup - _sceneStart;

    // Mod work this frame (Stopwatch ticks), added by every per-frame mod path, read and cleared by the hitch tracer.
    public static long ModTicks;
    const double HitchMs = 50;
    static float _sceneStart;
    static int _gcCount = -1, _hitches, _hitchLogs;
    static bool _altBroken;

    public static void Tick(string scene, float now, int interval)
    {
        if (interval <= 0) return;
        TraceHitch(scene, now);
        if (scene != _scene || Screen.width != _width || Screen.height != _height)
        {
            if (Wall.Count > 0) Report(now, interval);
            if (scene != _scene)
            {
                Plugin.I.Log.LogInfo(FormattableString.Invariant($"scene {_scene} -> {scene} after {now - _sceneStart:F1}s in {_scene}"));
                _sceneStart = now;
            }
            _scene = scene; _width = Screen.width; _height = Screen.height;
            _settleUntil = now + SettleSeconds; Wall.Clear(); RenderCounters.Clear();
        }
        if (_ignoreFrames > 0) { _ignoreFrames--; return; }
        bool counted = Application.isFocused && now >= _settleUntil && !Ablation.Active;
        UiProfile.Tick(scene, counted);
        if (!counted) return;
        Wall.Add(Time.unscaledDeltaTime * 1000.0);
        RenderCounters.Sample();
        if (now >= _nextReport) Report(now, interval);
    }

    // Any frame over HitchMs is logged with what ran in it, including loading scenes and unfocused frames.
    static void TraceHitch(string scene, float now)
    {
        double ms = Time.unscaledDeltaTime * 1000.0;
        double mod = Stats.ModTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Stats.ModTicks = 0;
        int gc = Il2CppSystem.GC.CollectionCount(0);
        int gcDelta = _gcCount < 0 ? 0 : gc - _gcCount;
        _gcCount = gc;
        if (ms < HitchMs || !Application.isFocused) return; // unfocused frames are capped by BackgroundFps
        _hitches++;
        if (_hitchLogs++ >= 40) return; // per report interval
        bool alt = false;
        if (!_altBroken)
        {
            try { var k = UnityEngine.InputSystem.Keyboard.current; alt = k != null && k.altKey.isPressed; }
            catch (Exception) { _altBroken = true; }
        }
        Plugin.I.Log.LogInfo(FormattableString.Invariant(
            $"hitch [{scene}] t={now - _sceneStart:F1}s mono={System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency:F2}s frame={ms:F1}ms mod={mod:F1}ms gc={gcDelta} alt={alt} focused={Application.isFocused} draws={RenderCounters.Last("Draw Calls Count")} setpass={RenderCounters.Last("SetPass Calls Count")} {TextureBudget.Brief()}"));
    }

    static void Report(float now, int interval)
    {
        if (_hitches > 0) Plugin.I.Log.LogInfo($"hitches over {HitchMs}ms since last report: {_hitches}");
        _hitches = 0; _hitchLogs = 0;
        _nextReport = now + interval;
        var s = Wall.Summary();
        var log = Plugin.I.Log;
        if (s.count > 0)
        {
            log.LogInfo(FormattableString.Invariant($"profile [{_scene}] {_width}x{_height} wall: n={s.count} mean={s.mean:F3} p50={s.p50:F3} p95={s.p95:F3} p99={s.p99:F3} max={s.max:F3}ms"));
            RenderCounters.Report(_scene, _width, _height);
        }
        UiProfile.Report(_scene);
        log.LogInfo(TextureBudget.Describe());
        FogCulling.Report();
        Wall.Clear(); RenderCounters.Clear();
    }
}
