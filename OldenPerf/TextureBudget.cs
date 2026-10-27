using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace OldenPerf;

// Mip streaming at full quality. Unity documents Texture.desiredTextureMemory as the total size of the textures it needs
// for the current view (streamed and non-streamed), and recommends a budget slightly above it. With spare budget Unity keeps
// unneeded high mips resident, and with too little it drops needed ones. The budget therefore tracks desired + headroom:
// every mip the view samples stays loaded, mips no camera needs are evicted. It rises at once and falls slowly.
// The earlier soft-texture regression was a fixed 1,024 MiB budget below the 1,444 MiB of non-streamed textures.
static class TextureBudget
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong U64();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetB(byte v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetF(float v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetI(int v);

    // Unity's stripped managed getters; the native icalls remain in UnityPlayer.
    static U64 _current, _desired, _nonStreaming, _total;
    static SetF _setBudget;
    static bool _on;
    static float _budgetMb, _nextPoll, _lowerSince = -1;

    public const float HeadroomMb = 384, StartMb = 6144, FloorMb = 1024, CeilingMb = 7168, PollSeconds = 0.5f, LowerAfterSeconds = 10f;

    public static ulong Current => _current?.Invoke() ?? 0;
    public static ulong Desired => _desired?.Invoke() ?? 0;
    public static ulong NonStreaming => _nonStreaming?.Invoke() ?? 0;
    public static float BudgetMb => _budgetMb;

    static T Icall<T>(string n) where T : Delegate
    {
        try { return IL2CPP.ResolveICall<T>(n); }
        catch (Exception e) { Plugin.I.Log.LogWarning($"icall {n}: {e.Message}"); return null; }
    }

    public static void Start(bool streaming, int maxReduction)
    {
        _current = Icall<U64>("UnityEngine.Texture::get_currentTextureMemory");
        _desired = Icall<U64>("UnityEngine.Texture::get_desiredTextureMemory");
        _nonStreaming = Icall<U64>("UnityEngine.Texture::get_nonStreamingTextureMemory");
        _total = Icall<U64>("UnityEngine.Texture::get_totalTextureMemory");
        _setBudget = Icall<SetF>("UnityEngine.QualitySettings::set_streamingMipmapsMemoryBudget");
        var active = Icall<SetB>("UnityEngine.QualitySettings::set_streamingMipmapsActive");
        if (!streaming || _setBudget == null || _desired == null || active == null)
        {
            active?.Invoke(0);
            Plugin.I.Log.LogInfo("mip streaming off (full mip chains resident)");
            return;
        }
        // Start generous so the first frames never starve; the controller lowers it once desired is known.
        _budgetMb = StartMb;
        _setBudget(_budgetMb);
        Icall<SetI>("UnityEngine.QualitySettings::set_streamingMipmapsMaxLevelReduction")?.Invoke(maxReduction);
        Icall<SetI>("UnityEngine.QualitySettings::set_streamingMipmapsRenderersPerFrame")?.Invoke(1024);
        Icall<SetB>("UnityEngine.QualitySettings::set_streamingMipmapsAddAllCameras")?.Invoke(1);
        active(1);
        _on = true;
        Plugin.I.Log.LogInfo($"mip streaming on: budget tracks desired + {HeadroomMb} MiB (start {StartMb} MiB, max reduction {maxReduction})");
    }

    public static void Tick(float now)
    {
        if (!_on || now < _nextPoll) return;
        _nextPoll = now + PollSeconds;
        float want = TextureBudgetMath.Target(Desired / 1048576f, HeadroomMb, FloorMb, CeilingMb);
        if (want > _budgetMb) { _budgetMb = want; _setBudget(_budgetMb); _lowerSince = -1; return; }
        if (want >= _budgetMb - 128) { _lowerSince = -1; return; }
        // Lower only after the lower demand has held for a while, so panning back does not trigger reloads.
        if (_lowerSince < 0) { _lowerSince = now; return; }
        if (now - _lowerSince < LowerAfterSeconds) return;
        _budgetMb = want; _setBudget(_budgetMb); _lowerSince = -1;
    }

    public static string Brief() => FormattableString.Invariant($"tex={Current / 1048576}/{Desired / 1048576}MiB budget={_budgetMb:F0}");

    public static string Describe() => _on
        ? FormattableString.Invariant($"textures: current={Current / 1048576}MiB desired={Desired / 1048576}MiB nonStreaming={NonStreaming / 1048576}MiB total={(_total?.Invoke() ?? 0) / 1048576}MiB budget={_budgetMb:F0}MiB")
        : FormattableString.Invariant($"textures: streaming off current={Current / 1048576}MiB nonStreaming={NonStreaming / 1048576}MiB total={(_total?.Invoke() ?? 0) / 1048576}MiB");
}
