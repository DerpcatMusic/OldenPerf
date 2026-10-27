using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

namespace OldenPerf;

// Counts are engine-reported rendering work, never labelled as GPU milliseconds.
static class RenderCounters
{
    sealed class Counter
    {
        public string Name;
        public ProfilerRecorder Recorder;
        public readonly SampleWindow Values = new();
    }
    static readonly List<Counter> Counters = new();
    public static unsafe void Init()
    {
        try
        {
            // GetAvailable was stripped from this retail player. The native UTF-8
            // lookup remains present; Render is Unity's built-in category 0.
            foreach (string name in new[] { "Draw Calls Count", "SetPass Calls Count", "Triangles Count", "Vertices Count", "Standard Instanced Draw Calls Count", "Standard Indirect Draw Calls Count" })
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(name);
                ProfilerRecorderHandle h;
                fixed (byte* ptr = bytes) h = ProfilerRecorderHandle.GetByName__Unmanaged(new ProfilerCategory(0), ptr, bytes.Length);
                if (!h.Valid) continue;
                var r = new ProfilerRecorder(h, 1, ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
                if (!r.Valid) { r.Dispose(); continue; }
                Counters.Add(new Counter { Name = name, Recorder = r });
                Plugin.I.Log.LogInfo("render counter: " + name);
            }
            Plugin.I.Log.LogInfo($"render counters: {Counters.Count} available; zero samples remain unavailable");
        }
        catch (Exception e) { Dispose(); Plugin.I.Log.LogWarning("render counters unavailable: " + e.Message); }
    }
    public static void Sample()
    {
        foreach (var c in Counters)
            if (c.Recorder.Valid && c.Recorder.Count > 0) c.Values.Add(c.Recorder.LastValue);
    }
    // Most recent value of a counter, -1 when unavailable (hitch tracing).
    public static long Last(string name)
    {
        foreach (var c in Counters) if (c.Name == name && c.Recorder.Valid && c.Recorder.Count > 0) return c.Recorder.LastValue;
        return -1;
    }
    public static void Clear() { foreach (var c in Counters) c.Values.Clear(); }
    public static void Report(string scene, int width, int height)
    {
        string path = Path.Combine(Plugin.I.OutDir, "render-counters.csv");
        using var w = new StreamWriter(path, true);
        if (w.BaseStream.Length == 0) w.WriteLine("utc,scene,width,height,metric,n,mean,p50,p95,max");
        foreach (var c in Counters)
        {
            var s = c.Values.Summary();
            if (s.count == 0) continue;
            Plugin.I.Log.LogInfo(FormattableString.Invariant($"render work [{scene}]: {c.Name} n={s.count} mean={s.mean:F1} p50={s.p50:F0} p95={s.p95:F0} max={s.max:F0}"));
            w.WriteLine(FormattableString.Invariant($"{DateTime.UtcNow:O},{scene},{width},{height},{c.Name},{s.count},{s.mean:F3},{s.p50:F0},{s.p95:F0},{s.max:F0}"));
        }
    }
    public static void Dispose()
    {
        foreach (var c in Counters) if (c.Recorder.Valid) c.Recorder.Dispose();
        Counters.Clear();
    }
}
