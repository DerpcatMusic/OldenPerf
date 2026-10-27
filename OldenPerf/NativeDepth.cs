using System;
using System.Linq;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Hex.Render;

namespace OldenPerf;

// Keep full float32 depth. Main depth/stencil is untouched. The sampled copy
// drops only unused stencil/storage padding. A full-resolution GPU/readback test
// runs through the game's existing copy shader before any live target changes.
static class NativeDepth
{
    static int _copyId;
    static RenderTargetIdentifier Depth, DepthCopy;
    [ThreadStatic] static bool _scope;
    [ThreadStatic] static IntPtr _trimmedCommand;
    static int _width, _height, _verifiedWidth, _verifiedHeight, _queuedFrame;
    static bool _ready, _tested, _verified, _queued, _installed, _driverChecked;
    static string _scene = "";
    static float _sceneSince;
    static RenderTexture _testDepth, _reference, _actual;
    public static long TrimmedCopies { get; private set; }

    public static void Init(Harmony harmony)
    {
        if (!BuildIdentity.IsVerified)
        { Plugin.I.Log.LogWarning("native depth: unverified configuration; original depth copy retained"); return; }
        _copyId = Shader.PropertyToID("_CameraDepthTexture");
        Depth = new RenderTargetIdentifier(Shader.PropertyToID("_DepthBuffer"));
        DepthCopy = new RenderTargetIdentifier(_copyId);
        HarmonyMethod Hook(string n) => new(typeof(NativeDepth).GetMethod(n, BindingFlags.Static | BindingFlags.NonPublic));
        try
        {
            if (!ShaderAssetGuard.Verified) throw new InvalidOperationException("unverified copy-shader asset");
            var driver = typeof(bfp).GetMethod("Render", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(ScriptableRenderContext), typeof(Il2CppSystem.Collections.Generic.List<Camera>) }, null);
            var allocator = typeof(CommandBuffer).GetMethod("GetTemporaryRT", new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(FilterMode), typeof(RenderTextureFormat), typeof(RenderTextureReadWrite) });
            var allocatorShort = typeof(CommandBuffer).GetMethod("GetTemporaryRT", new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(FilterMode), typeof(RenderTextureFormat) });
            var copy = typeof(CommandBuffer).GetMethod("CopyTexture", new[] { typeof(RenderTargetIdentifier), typeof(RenderTargetIdentifier) });
            if (driver == null || allocator == null || allocatorShort == null || copy == null) throw new MissingMethodException("verified depth-copy signatures");
            harmony.Patch(driver, prefix: Hook(nameof(Enter)), finalizer: Hook(nameof(Leave)));
            harmony.Patch(allocator, prefix: Hook(nameof(Allocate)));
            harmony.Patch(allocatorShort, prefix: Hook(nameof(Allocate)));
            harmony.Patch(copy, prefix: Hook(nameof(Copy)), postfix: Hook(nameof(AfterCopy)));
            _installed = true;
            Plugin.I.Log.LogInfo("native depth: armed full-resolution equality probe; live formats unchanged pending validation");
        }
        catch (Exception e) { harmony.UnpatchSelf(); Plugin.I.Log.LogWarning("native depth disabled: " + e.Message); }
    }
    static void Enter(Il2CppSystem.Collections.Generic.List<Camera> __1, out bool __state)
    {
        __state = _scope; _scope = false; _trimmedCommand = IntPtr.Zero;
        if (__1 == null) return;
        // Reject mixed base-camera invocations (including scene transitions and
        // off-screen previews). Overlay cameras keep the original main stencil.
        for (int i = 0; i < __1.Count; i++)
        {
            var c = __1[i];
            if (c == null || c.name == "Map Camera") continue;
            var context = c.GetComponent<BhCameraContext>();
            if (context == null || context.renderType != CameraRenderType.Overlay) return;
        }
        for (int i = 0; i < __1.Count; i++)
        {
            var c = __1[i];
            if (c == null || c.name != "Map Camera" || c.targetTexture != null || c.stereoEnabled ||
                c.pixelRect != new Rect(0, 0, Screen.width, Screen.height)) continue;
            _width = c.pixelWidth; _height = c.pixelHeight;
            if (_verifiedWidth != _width || _verifiedHeight != _height)
            {
                _verifiedWidth = _width; _verifiedHeight = _height;
                // A proof from an earlier launch with the same build, driver and size skips the 4K readback.
                _verified = _tested = ProvenGates.Has(ProvenGates.Key("native-depth", _width, _height));
                if (_verified) Plugin.I.Log.LogInfo($"native depth: equality proven earlier at {_width}x{_height} on this build and driver; enabled without readback");
            }
            _scope = true; break;
        }
    }
    static void Leave(bool __state) { _scope = __state; _trimmedCommand = IntPtr.Zero; }
    static RenderTextureDescriptor DepthDescriptor(int w, int h) => new(w, h, RenderTextureFormat.Depth, 32)
    {
        graphicsFormat = GraphicsFormat.None, depthStencilFormat = GraphicsFormat.D32_SFloat,
        msaaSamples = 1, useMipMap = false, autoGenerateMips = false, mipCount = 1, sRGB = false
    };
    static bool Allocate(CommandBuffer __instance, int __0, int __1, int __2, int __3, FilterMode __4, RenderTextureFormat __5)
    {
        if (__0 == _copyId) _trimmedCommand = IntPtr.Zero;
        if (!_scope || !_verified || __0 != _copyId || __1 != _width || __2 != _height || __3 != 32 || __5 != RenderTextureFormat.Depth) return true;
        __instance.GetTemporaryRT(__0, DepthDescriptor(__1, __2), __4);
        _trimmedCommand = __instance.Pointer;
        return false;
    }
    static bool Copy(CommandBuffer __instance, RenderTargetIdentifier __0, RenderTargetIdentifier __1, out bool __state)
    {
        __state = false;
        if (!_scope || !__0.Equals(Depth) || !__1.Equals(DepthCopy)) return true;
        if (_verified && _trimmedCommand == __instance.Pointer)
        {
            bgm.mcb(__instance, new Rect(0, 0, _width, _height), Depth, DepthCopy, true);
            TrimmedCopies++;
            return false;
        }
        __state = _ready && !_tested && !_queued;
        return true;
    }
    static void AfterCopy(CommandBuffer __instance, bool __state)
    {
        if (!__state) return;
        try
        {
            _testDepth = new RenderTexture(DepthDescriptor(_width, _height)) { name = "OldenPerf depth probe D32" };
            _reference = new RenderTexture(_width, _height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { name = "OldenPerf depth reference" };
            _actual = new RenderTexture(_width, _height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { name = "OldenPerf depth actual" };
            if (!_testDepth.Create() || !_reference.Create() || !_actual.Create()) throw new InvalidOperationException("probe allocation failed");
            if (_testDepth.depthStencilFormat != GraphicsFormat.D32_SFloat ||
                _reference.graphicsFormat != GraphicsFormat.R32_SFloat || _actual.graphicsFormat != GraphicsFormat.R32_SFloat)
                throw new InvalidOperationException("probe format substituted by the driver");
            var rect = new Rect(0, 0, _width, _height);
            bgm.mcb(__instance, rect, Depth, new RenderTargetIdentifier(_testDepth), true);
            bgm.mbz(__instance, rect, DepthCopy, new RenderTargetIdentifier(_reference));
            bgm.mbz(__instance, rect, new RenderTargetIdentifier(_testDepth), new RenderTargetIdentifier(_actual));
            _queued = true; _queuedFrame = Time.frameCount;
            Stats.ExcludeDiagnostics();
        }
        catch (Exception e) { _tested = true; Cleanup(); Plugin.I.Log.LogWarning("native depth probe failed; original formats retained: " + e.Message); }
    }
    public static bool Tick(string scene)
    {
        if (!_installed) return false;
        if (scene != _scene) { _scene = scene; _sceneSince = Time.realtimeSinceStartup; _ready = false; }
        _ready = scene.StartsWith("world") && Time.realtimeSinceStartup - _sceneSince > 5;
        if (_ready && !_driverChecked)
        {
            _driverChecked = true;
            Plugin.I.Log.LogInfo($"native depth driver: api={SystemInfo.graphicsDeviceType} D32 render={SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat, GraphicsFormatUsage.Render)} sample={SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat, GraphicsFormatUsage.Sample)}; allocation/readback probe decides actual compatibility");
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) { _installed = false; _ready = false; return false; }
        }
        if (!_queued || Time.frameCount <= _queuedFrame) return false;
        var active = RenderTexture.active;
        Texture2D readback = null;
        try
        {
            Stats.ExcludeDiagnostics();
            readback = new Texture2D(_width, _height, TextureFormat.RGBAFloat, false, true);
            var rect = new Rect(0, 0, _width, _height);
            RenderTexture.active = _reference; readback.ReadPixels(rect, 0, 0, false);
            var reference = readback.GetPixels().ToArray();
            RenderTexture.active = _actual; readback.ReadPixels(rect, 0, 0, false);
            var actual = readback.GetPixels().ToArray();
            var comparison = new DepthEquality();
            for (int i = 0; i < reference.Length; i++)
                comparison.Add(reference[i].r, actual[i].r);
            _verified = comparison.Equivalent && comparison.Count == (long)_width * _height;
            _tested = true; _verifiedWidth = _width; _verifiedHeight = _height;
            if (_verified) ProvenGates.Add(ProvenGates.Key("native-depth", _width, _height));
            var result = FormattableString.Invariant($"width={_width} height={_height} pixels={comparison.Count} different_float32_bits={comparison.DifferentBits} invalid={comparison.Invalid} depth_min={comparison.Min:R} depth_max={comparison.Max:R} enabled={_verified}");
            File.WriteAllText(Path.Combine(Plugin.I.OutDir, "depth-copy-equality.txt"), result);
            Plugin.I.Log.LogInfo("native depth equality: " + result + "; main stencil and all render dimensions unchanged");
        }
        catch (Exception e) { _tested = true; _verified = false; Plugin.I.Log.LogWarning("native depth comparison failed; original formats retained: " + e.Message); }
        finally
        {
            RenderTexture.active = active;
            if (readback != null) UnityEngine.Object.Destroy(readback);
            Cleanup(); Stats.ExcludeDiagnostics();
        }
        return true;
    }
    static void Cleanup()
    {
        foreach (var rt in new[] { _testDepth, _reference, _actual })
            if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
        _testDepth = _reference = _actual = null; _queued = false;
    }
}
