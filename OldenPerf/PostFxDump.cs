using System;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace OldenPerf;

// One-shot diagnostic: the post-processing setup the map camera actually renders with (layer settings, every volume,
// every effect and its overridden parameter values). Written once per scene to the log.
static class PostFxDump
{
    static string _done = "";

    public static void Tick(string scene, float sinceScene)
    {
        if (scene == _done || sinceScene < 5) return;
        _done = scene;
        try
        {
            var log = Plugin.I.Log;
            foreach (var layer in UnityEngine.Object.FindObjectsOfType<PostProcessLayer>())
                log.LogInfo($"postfx [{scene}] layer '{layer.name}' camera={layer.GetComponent<Camera>()?.name} enabled={layer.enabled} aa={layer.antialiasingMode} volumeLayer={layer.volumeLayer.value} stopNaN={layer.stopNaNPropagation} directToCamera={layer.finalBlitToCameraTarget}");
            foreach (var v in UnityEngine.Object.FindObjectsOfType<PostProcessVolume>())
            {
                var profile = v.sharedProfile;
                log.LogInfo($"postfx [{scene}] volume '{v.name}' enabled={v.enabled} global={v.isGlobal} weight={v.weight} priority={v.priority} layer={v.gameObject.layer} profile={profile?.name}");
                if (profile == null) continue;
                foreach (var s in profile.settings)
                {
                    if (s == null) continue;
                    var sb = new StringBuilder();
                    var t = s.GetIl2CppType();
                    foreach (var f in t.GetFields())
                    {
                        object raw = null;
                        try
                        {
                            var p = f.GetValue(s);
                            if (p == null) continue;
                            var pt = p.GetIl2CppType();
                            var over = pt.GetField("overrideState"); var val = pt.GetField("value");
                            if (over == null || val == null) continue;
                            var o = val.GetValue(p);
                            // Boxed primitives must be unboxed by type; ToString on the wrapper reads the box pointer.
                            string text = val.FieldType.FullName switch
                            {
                                "System.Single" => o.Unbox<float>().ToString("G4", System.Globalization.CultureInfo.InvariantCulture),
                                "System.Int32" => o.Unbox<int>().ToString(),
                                "System.Boolean" => o.Unbox<bool>().ToString(),
                                "UnityEngine.Color" => o.Unbox<UnityEngine.Color>().ToString(),
                                "UnityEngine.Vector2" => o.Unbox<UnityEngine.Vector2>().ToString(),
                                "UnityEngine.Vector4" => o.Unbox<UnityEngine.Vector4>().ToString(),
                                _ => o?.ToString(),
                            };
                            raw = $"{f.Name}={text}{(over.GetValue(p).Unbox<bool>() ? "" : "(default)")}";
                        }
                        catch (Exception) { }
                        if (raw != null) sb.Append(' ').Append(raw);
                    }
                    log.LogInfo($"postfx [{scene}]   {t.Name} active={s.active}{sb}");
                }
            }
        }
        catch (Exception e) { Plugin.I.Log.LogWarning("postfx dump: " + e.Message); }
    }
}
