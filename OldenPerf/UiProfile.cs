using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OldenPerf;

// Where UI frames go. Times the two per-frame UI entry points: EventSystem.Update (input, raycasts, every hover/click
// handler the game runs) and CanvasUpdateRegistry.PerformUpdate (layout + graphic rebuilds, TextMeshPro included).
// A slow UI frame logs the objects under the pointer and the rebuild queue sizes.
static class UiProfile
{
    const double SpikeMs = 8;
    static long _start, _events, _rebuild;
    static int _layoutQueue, _graphicQueue, _spikes;
    static readonly SampleWindow Events = new(), Rebuild = new();

    public static void Init(Harmony h)
    {
        try
        {
            HarmonyMethod Hook(string n) => new(typeof(UiProfile).GetMethod(n, BindingFlags.Static | BindingFlags.NonPublic));
            h.Patch(typeof(EventSystem).GetMethod("Update", Type.EmptyTypes), prefix: Hook(nameof(Begin)), postfix: Hook(nameof(EndEvents)));
            h.Patch(typeof(CanvasUpdateRegistry).GetMethod("PerformUpdate", Type.EmptyTypes), prefix: Hook(nameof(BeginRebuild)), postfix: Hook(nameof(EndRebuild)));
            Plugin.I.Log.LogInfo("ui profile: timing EventSystem.Update and CanvasUpdateRegistry.PerformUpdate");
        }
        catch (Exception e) { h.UnpatchSelf(); Plugin.I.Log.LogWarning("ui profile unavailable: " + e.Message); }
    }

    static void Begin() => _start = Stopwatch.GetTimestamp();
    static void EndEvents() => _events += Stopwatch.GetTimestamp() - _start;
    static void BeginRebuild()
    {
        try
        {
            var r = CanvasUpdateRegistry.instance;
            _layoutQueue = r.m_LayoutRebuildQueue.Count; _graphicQueue = r.m_GraphicRebuildQueue.Count;
        }
        catch (Exception) { }
        _start = Stopwatch.GetTimestamp();
    }
    static void EndRebuild() => _rebuild += Stopwatch.GetTimestamp() - _start;

    // Once per frame from Runner.Update: closes the previous frame's UI work.
    public static void Tick(string scene, bool counted)
    {
        double f = 1000.0 / Stopwatch.Frequency, ev = _events * f, rb = _rebuild * f;
        _events = _rebuild = 0;
        if (!counted) return;
        Events.Add(ev); Rebuild.Add(rb);
        if ((ev >= SpikeMs || rb >= SpikeMs) && _spikes++ < 25)
            Plugin.I.Log.LogInfo(FormattableString.Invariant(
                $"ui spike [{scene}] mono={Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency:F2}s events={ev:F1}ms rebuild={rb:F1}ms layoutQueue={_layoutQueue} graphicQueue={_graphicQueue} pointer={UnderPointer()}"));
    }

    static string UnderPointer()
    {
        try
        {
            var es = EventSystem.current; var mouse = UnityEngine.InputSystem.Mouse.current;
            if (es == null || mouse == null) return "?";
            var data = new PointerEventData(es) { position = mouse.position.ReadValue() };
            var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(data, hits);
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < hits.Count && i < 3; i++)
            {
                var t = hits[i].gameObject?.transform; string path = "";
                for (int d = 0; t != null && d < 4; d++, t = t.parent) path = t.name + (path.Length > 0 ? "/" + path : "");
                names.Add(path);
            }
            return names.Count == 0 ? "-" : string.Join(" | ", names);
        }
        catch (Exception e) { return "err " + e.GetType().Name; }
    }

    public static void Report(string scene)
    {
        var e = Events.Summary(); var r = Rebuild.Summary();
        if (e.count > 0)
            Plugin.I.Log.LogInfo(FormattableString.Invariant(
                $"ui [{scene}] events mean={e.mean:F2} p95={e.p95:F2} max={e.max:F1}ms rebuild mean={r.mean:F2} p95={r.p95:F2} max={r.max:F1}ms spikes={_spikes}"));
        Events.Clear(); Rebuild.Clear(); _spikes = 0;
    }
}
