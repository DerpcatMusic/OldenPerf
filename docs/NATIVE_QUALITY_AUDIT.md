# Native-quality optimization — OldenPerf 0.4.0

The user clarified the requirement: retain image quality and remove redundant data/work. Lower resolution, lower shadow detail, dropping effects and silently reducing arithmetic precision are not optimization results under this requirement.

## Current installed state

Render-scale and shadow-cap code was removed. Output and map buffers are again native 3840×2160, the shadow atlas is 4096², and the original six full-resolution RGBA16F color/history surfaces remain. Packed R11G11B10 TAA is disabled because it reduces mantissa precision. D24 is not used. SSAO, reflections, post-processing, TAA and original main-buffer stencil remain enabled. The current BC1/BC7 texture compression and disabled mip streaming are documented in [the compression follow-up](TEXTURE_COMPRESSION_FOLLOWUP.md); BC1/BC7 are lossy formats and are not being reclassified as numerically lossless.

## Verified saving: sampled depth copy

The main `_DepthBuffer` needs stencil. Shader-state inspection found active stencil operations/comparisons in lit materials, outline effects, TAA and UI shaders. Removing stencil globally would be incorrect.

The separate `_CameraDepthTexture` is the sampled depth snapshot. The verified pipeline supplies the original main depth/stencil attachment to fixed-function stencil consumers; the sampled copy's stencil is not required by this path. Its original D32_SFloat_S8_UInt allocation occupies 63.28 MiB at 4K. A D32_SFloat copy retains the **same float32 depth precision** and occupies 31.64 MiB.

The existing `Hidden/Hex/Copy` depth pass samples LOD 0 and moves the red value directly to `SV_Depth`; it does not linearize, quantize or otherwise transform depth. The source uses point filtering. Both the game binary and the exact compressed resources asset containing that shader are fingerprinted.

Before changing any live allocation, the mod keeps the original copy and creates a separate D32 test copy. It samples both into R32 float capture targets using the same color-copy shader, reads them back, and compares every float32 depth bit pattern. Empty/flat images, out-of-range/nonfinite values, one-ULP differences and signed-zero changes cannot authorize the optimization. Probe allocations must also retain their exact requested formats.

The final live check reported:

```
width=3840 height=2160 pixels=8294400
different_float32_bits=0 invalid=0
depth_min=0 depth_max=0.8640552 enabled=True
```

After validation, the actual native allocator and matching copy path were intercepted. The live inventory confirms the sampled copy is now D32_SFloat at 31.64 MiB, while the main D32+S8 attachment remains 63.28 MiB. Total render-target inventory changes from **1,118 to 1,086 MiB**, a **31.64 MiB logical allocation saving**. All target dimensions and original color precision remain unchanged. The CSV counter confirmed hundreds of live replacements, not merely a successful diagnostic probe.

The replacement also avoids copying the snapshot's unused stencil/padding. It replaces a full depth/stencil resource copy with a float-depth draw; no per-pass GPU timing or net FPS saving has been established. Do not call this a massive engine-wide improvement or a measured physical-residency reduction. Unity's earlier streamed texture-memory counter was 1,749 MiB. A temporary 1,024 MiB mip budget was reverted to 2,048 MiB after reports of soft city textures. On 27 October the config was changed to disable mip streaming and set the global texture mip limit to 0; this newer state has not been checked in-game.

This path is restricted to a full-screen, non-stereo map camera with no other base camera in the render invocation. Mixed-camera transitions and off-screen previews retain the original path. Size changes invalidate the equality result. A mismatching/inconclusive/failed probe leaves original allocations intact. Validation is opt-in via `Memory.TrimDepthCopy`; it is enabled in this machine's configuration. Its one-time GPU readback can stall, and diagnostic intervals are excluded from profiling.

The initialized device reports D32 render support but a false generic Sample capability flag, while actual exact-format allocation and full-frame depth sampling/readback succeed. The optimization relies on this concrete probe rather than treating that generic flag as proof that depth-aspect sampling cannot work. Earlier D24 support statements should not be read as a broad hardware conclusion; no D24 path is active or needed here. Unity documents [format usages and capability queries](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Experimental.Rendering.GraphicsFormatUsage.html).

## Rejected shortcut: final color-copy rerouting

A prototype wrote an eligible final post-process directly into `_ColorBuffer` and skipped the subsequent copy from `_CameraColorTexture`. It ran on the map, but that alone does not establish equivalence: subsequent stages can depend on the separate snapshot. The prototype was removed from the release and is disabled in the installed configuration.

Correct fusion must preserve both logical outputs and their lifetimes, for example by producing equivalent MRT outputs in one final shader pass, or by proving the second output has no later consumer. It cannot silently change the snapshot binding to the live color target and introduce a read/write hazard. No rendering-pass merge is claimed as delivered.

## Further work that meets the requirement

- Trace consumers/lifetimes of every HDR channel before changing formats. RGB-only usage does not make R11G11B10 numerically equivalent to FP16 RGB; there is no native D3D11 48-bit RGB render-target substitute established here.
- Fuse compatible shader stages while preserving intermediate rounding, edge handling, stencil, temporal history and every live output. Removing a named method or copy call without those checks is not sufficient.
- Alias physical resources only after their final consumer, including overlay cameras and later snapshot sampling.
- Obtain a GPU resource-history capture to substantiate larger bandwidth/pass changes. Current retail Unity timing recorders remain unavailable; CPU submission timings cannot identify GPU pass costs.

## Validation and evidence

Release builds succeed with zero warnings/errors. Numerical profiler tests, strict depth-equality tests and six Python manifest/residency tests pass. Live validation covers menu/map rendering, the full-resolution equality check, actual allocation formats/sizes and replacement counters without plugin exceptions. Battles and other GPU/API configurations are not covered by these map tests.

Evidence: `/data/dev/workspace/RunScratch/olden-native-quality-20261023/`, especially `native-quality.log`, `depth-copy-equality.txt`, `native-depth-inventory.csv` and `native-quality-map.png`. The original copy-reroute experiment is recorded separately as rejected; its visual smoke test is not evidence of whole-frame equivalence.
