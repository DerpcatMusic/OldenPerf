using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace OldenPerf;

// Destructive ablation experiment, NOT a GPU profiler. Skipping a method can invalidate
// outputs and change downstream work/history. Deltas cannot be attributed to a single pass.
static class Ablation
{
    static readonly List<MethodBase> Targets = new();
    static readonly List<string> Labels = new();
    static int _slot, _cycle, _cycles;
    static float _slotEnd, _settleEnd, _slotLen;
    static readonly List<float> Win = new(1024);
    static readonly Dictionary<string, List<(float baseMs, float skipMs)>> Res = new();
    static float _lastBase = -1;

    public static bool Active => Targets.Count > 0 && _cycle < _cycles;

    public static void Init(Harmony h, string spec, float slotSeconds, int cycles)
    {
        _slotLen = slotSeconds; _cycles = cycles;
        var prefix = new HarmonyMethod(typeof(Ablation).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dot = item.LastIndexOf('.');
            if (dot <= 0 || dot == item.Length - 1) { Plugin.I.Log.LogWarning($"ablation: invalid target '{item}'"); continue; }
            var type = AccessTools.TypeByName(item[..dot]);
            var ms = type == null ? new List<MethodInfo>() : AccessTools.GetDeclaredMethods(type).Where(m => m.Name == item[(dot + 1)..] && m.ReturnType == typeof(void)).ToList();
            if (ms.Count == 0) { Plugin.I.Log.LogWarning($"ablation: {item} not found"); continue; }
            foreach (var m in ms) h.Patch(m, prefix: prefix);
            foreach (var m in ms) { Targets.Add(m); Labels.Add(item); }
        }
        Plugin.I.Log.LogWarning($"ablation DESTRUCTIVE experiment: {Targets.Count} methods, {_slotLen}s windows, {_cycles} cycles; deltas are whole-frame changes, NOT GPU pass timings");
    }

    static bool Prefix(MethodBase __originalMethod) => _paused || _skipLabel == null || Labels[Targets.IndexOf(__originalMethod)] != _skipLabel;

    // Overloads/clones sharing a label are skipped together.
    static List<string> Distinct => Labels.Distinct().ToList();

    public static void Tick(float now, float dt)
    {
        if (!Active) { _skipLabel = null; return; }
        _paused = false;
        var groups = Distinct;
        if (_slotEnd == 0) { _slotEnd = now + _slotLen; _settleEnd = now + 1f; }
        if (now >= _settleEnd) Win.Add(dt * 1000f);
        if (now < _slotEnd) return;

        Win.Sort();
        float med = Win.Count > 0 ? Win[Win.Count / 2] : 0;
        Win.Clear();
        bool baseline = _slot % 2 == 0;
        int g = _slot / 2;
        if (baseline) _lastBase = med;
        else
        {
            if (!Res.TryGetValue(groups[g], out var l)) Res[groups[g]] = l = new();
            l.Add((_lastBase, med));
        }

        _slot++;
        if (_slot >= groups.Count * 2) { _slot = 0; _cycle++; Report(); }
        bool nextBaseline = _slot % 2 == 0;
        _skipLabel = nextBaseline || !Active ? null : groups[_slot / 2];
        _slotEnd = now + _slotLen; _settleEnd = now + 1f;
    }

    static string _skipLabel;

    // Unfocused or off-map: throw away the current window and restart it when we're back.
    static bool _paused;
    public static void Pause() { Win.Clear(); _slotEnd = 0; _paused = true; }

    static void Report()
    {
        var p = Plugin.I;
        p.Log.LogInfo($"ablation cycle {_cycle}/{_cycles}:");
        foreach (var (label, l) in Res.OrderByDescending(kv => kv.Value.Average(x => x.baseMs - x.skipMs)))
        {
            float b = l.Average(x => x.baseMs), s = l.Average(x => x.skipMs);
            p.Log.LogInfo(string.Format("  {0,-14} base {1,6:F2} ms  skipped {2,6:F2} ms  whole-frame delta {3,6:F2} ms  (n={4}; dependencies/history changed)", label, b, s, b - s, l.Count));
        }
    }
}
