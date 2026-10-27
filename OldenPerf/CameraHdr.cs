using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace OldenPerf;

// The UI camera and the 3D spellbook camera render with HDR on but without post-processing. In the built-in pipeline an
// HDR camera renders into its own full-screen RGBA16F buffer that is copied to the backbuffer and clamped there, so with
// no tonemapping in between HDR changes nothing on screen: it adds a 63 MiB 4K buffer and a 4K copy per camera per frame.
// Cameras that draw to the screen without a PostProcessLayer get HDR off. The map camera (bloom, grading) keeps it.
static class CameraHdr
{
    static float _next;

    public static void Tick(float now)
    {
        if (now < _next) return;
        _next = now + 2f;
        foreach (var c in Camera.allCameras)
        {
            if (c == null || !c.allowHDR || c.targetTexture != null) continue;
            var layer = c.GetComponent<PostProcessLayer>();
            if (layer != null && layer.enabled) continue;
            c.allowHDR = false;
            Plugin.I.Log.LogInfo($"camera {c.name}: HDR off (no post-processing on it)");
        }
    }
}
