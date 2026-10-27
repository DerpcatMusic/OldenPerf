> Historical audit: runtime settings and experiments here are superseded by NATIVE_QUALITY_AUDIT.md and VISIBILITY_AUDIT.md. Packed TAA history is currently disabled. See DEVELOPER_OPTIMIZATION_REPORT.md for the current evidence inventory.

# Olden Era rendering audit — 23 October 2026

> Current policy: [native-quality audit](NATIVE_QUALITY_AUDIT.md). The later reduced-resolution/shadow experiment was rejected by the user and its code removed. Packed TAA is also disabled to preserve original precision. Historical measurements below are labeled by stage; they do not describe the current configuration.

## Verdict

The renderer has a poor memory footprint for an 8 GiB card at 4K: large full-resolution HDR/depth surfaces coexist with a texture residency policy that leaves substantial reducible pressure. It also lacks an exposed world render-scale control. There are credible optimization targets, especially effect resolution and resource lifetimes. The available evidence does **not** establish novice developers, a redundant whole-scene prepass, or a safe 2× performance patch. Calling every copy or pooled temporary allocation waste confuses dependencies with redundancy.

This is a custom Unity scriptable render pipeline, not an unmodified URP renderer. Built-in `QualitySettings` are not sufficient to establish its effective settings.

## Version and evidence

Steam updated the public-test branch during launch to build **26134266**, displayed as **0.82.04 BETA**. The old decompilation predates that update. A fresh native disassembly was generated from the installed binary, and the final profiler's native method bindings are restricted to its SHA-256:

`eb57aeecaa27ff5764ddff76c5a3459857ed73f3abdefb14cfa370facb85e940`

Evidence lives in `/data/dev/workspace/RunScratch/olden-engine-audit-20261023/`. The native dump is `current-isil/IsilDump/Hex/`. The first error-free map run is `passive-map-first.log` and its CSV. Earlier runs containing `camera-data-hook-failure.log` are failed instrumentation experiments and must not be used for engine performance conclusions.

The game runs through Proton/Wine and D3D11/DXVK on an AMD Radeon RX 6600. Several other browser GPU processes were running. During the map run, device utilization was 99% and occupied VRAM was approximately 7.2 GiB out of 8 GiB. The game's DRM allocation report included about 4 GiB VRAM and 2.2 GiB GTT. GTT allocation alone does not prove an eviction or transfer bottleneck.

The observed roughly 100 ms frames are **not a valid comparison** with the older 23.6 ms result: the binary, texture streaming flags, concurrent GPU workload, and instrumentation differ. The final build also stops unsuccessful Unity timing captures rather than requesting them forever. A clean A/B benchmark and observer-overhead check are still required before claiming a speedup.

## Changes installed and measured

The initial 23 October pass compressed **363 `resources.assets` textures from 491 to 123 MiB** and restored **2,110 streaming flags**. Its measured minimum PSNR was 31.87 dB. This is historical, not the current asset patch. The 25 October multi-format pass covered 26 hashed files; the 27 October pass added 208 texture conversions across 25 files, with BC1 for eligible opaque art and BC7 for alpha art. Runtime mip streaming is now disabled and the global mip limit is 0. See [the current texture-compression audit](TEXTURE_COMPRESSION_FOLLOWUP.md). Both passes preserve exact-build backups and hash guards.

On the same loaded adventure save, Unity's texture memory went from **3,151 to 1,749 MiB**, a **1,402 MiB reduction**. This historical sample combines compression and changed streaming residency; it is not solely the 368 MiB payload reduction. Streamable texture count went from 216 to 1,662. Total logical texture memory was 3,366 versus 3,063 MiB. These Unity counters are not exact physical VRAM measurements and predate the 27 October mip-streaming disable.

The additional `Memory.CompactTaaHistory` option is enabled in this machine's installed configuration. Fresh native disassembly identifies two no-depth DefaultHDR history allocations inside `bfj.lrz`. Both compiled TAA pixel variants write RGB to both MRT outputs, with no alpha output. A build-guarded, scoped hook changes those histories to R11G11B10 unsigned floating point. The live map inventory confirms **two 3840×2160 B10G11R11_UFloatPack32 targets at 31.64 MiB each**, replacing two 63.28 MiB RGBA16F targets: **63.28 MiB saved**, with native resolution and TAA retained. Render-target inventory drops from about 1,118 to 1,054 MiB. Four other full-4K RGBA16F targets remain unchanged.

Menu and map smoke tests showed no obvious image corruption or plugin exceptions. This is limited validation, not a proof of equal image quality across battles, factions and lighting. Packed HDR reduces precision, removes signed values/alpha, and should be disabled if artifacts appear. It defaults off in source, fails closed on an unknown game binary, and requires restart. The main color, depth/stencil and compute formats are untouched.

The final AMD sample reports approximately 4.00 GiB resident VRAM and 420 MiB resident GTT for the game. Requested VRAM exceeds resident VRAM by **256 MiB**, matching resident GTT above requested GTT. This supports continued placement pressure, despite an eviction gauge of zero. A prior compression-only sample showed a 768 MiB gap, but changing concurrent GPU allocations prevents attributing that entire difference to a 63 MiB format patch. Device utilization remains 99%. Neither allocation gauges nor Unity counters measure transfer bandwidth or stall time; spill has not been proven eliminated.

Evidence: `compressed-map-before-history.log`, `compressed-map-residency.csv`, `compact-history-final.log`, `compact-map-residency.csv`, and `compact-history-map.png` in the evidence directory. Reusable artifacts and the exact-build backup are under `/data/dev/workspace/PluginArtifacts/OldenPerf/26134266/`.

## Confirmed live configuration

- SRP batcher enabled; pipeline anti-aliasing TAA; history feedback 0.93 and sharpening 0.7.
- Runtime quality: SSAO High, SSPR reflections, shadows High.
- Adventure-map camera: cascades disabled, shadow distance 200, reflections and selection outlines enabled, dynamic resolution disabled.
- Main-menu camera: cascades enabled with ratios 0.25, 0.456, 0.661 and shadow distance 400. This directly demonstrates why one camera's settings cannot describe every scene.
- Shadow atlas: 4096×4096, D32_SFloat, 64 MiB.
- The video settings UI exposes separate SSAO, reflections, shadow, bloom, and anti-aliasing controls. The claim that there is no way to disable such effects is false for this build. There is no separate world render-scale control in this screen.
- Before restoring the current-build asset patch, only 216 textures were streamable. After the guarded refresh, 1,662 were streamable during the historical map sample; the 27 October config now disables mip streaming and has no new live residency sample.

## Corrections to the earlier analysis

### Render targets are a resident inventory, not a frame resource graph

The live map inventory again reports 48 render textures totaling about 1,118 MiB. It includes six 4K RGBA16F surfaces at 63.28 MiB each and two 4K D32_SFloat_S8_UInt surfaces at the same reported size. The latter are depth/stencil, not HDR color surfaces eligible for R11G11B10 conversion.

Two seemingly oversized 73 MiB textures have an explanation: one is a 1024² array with 16 slices and the other a 512² array with 64 slices. The previous dump omitted dimensions and slice counts. The new inventory records these, along with depth format, instance ID, random-write capability, dynamic scaling, and creation status.

This inventory includes pooled allocations; it does not prove all resources are simultaneously required by one camera or that each is written every frame.

### The whole-scene prepass claim mixed up different passes

The older `bfn.lsp` was tagged `DrawSilhouettes` and called selection-outline code. In the current build its equivalent is `bfn.lsn`. The actual G-buffer stage is separately represented by `bfn.lso`; the current base-camera driver is `bfn.lsi`.

A real auxiliary geometry pass exists. Its redundancy is unproven: removing it requires checking the depth/normal consumers, SSAO, selection, and other render stages. The same obfuscated symbol can acquire a different meaning after an update.

### SSAO is not four full-resolution R8 targets

In the current `bew.lox` disassembly, Low quality divides the initial target's width and height by two. The first three allocations use ARGB32, while the final allocation uses R8_UNorm. Subsequent intermediates use full camera dimensions. Shader pass names identify AO, horizontal blur, vertical blur, and final blur.

This is a credible scaling target. Compiled AO pixel variants actually write all four output components. Blindly replacing these ARGB32 intermediates with R8 would discard shader data; packing and filtering must be changed together.

### The shadow atlas supports one or four cascades

`bfs.luw` switches between one and four splits and changes atlas subdivision accordingly. It computes directional-shadow matrices/culling primitives per split and sets viewports. A single 4096 texture cannot establish the cascade count. The live adventure-map camera happens to select one cascade.

### Copies and temporary allocation calls do not prove wasted work

The main and transparent stages contain conditional copy paths, including TAA/history handling. A call graph cannot establish that every copy runs, or that the destination has no consumer. Unity's temporary render texture pool is intended for per-pass acquire/release use. The useful optimization question is peak live resources and unnecessary transfers, not the number of API call sites.

## Further patch priorities

Texture compression, the 27 October mip-streaming disable and the scoped TAA history change above are installed. The priorities below describe further work, not delivered features.

1. **World render scale with native-resolution UI.** Strongest broad candidate for pixel-limited workloads. A 0.75 scale uses 56.25% of native pixels; 1440p uses 44.44% of 4K pixels. These are pixel counts, not FPS predictions. The implementation must coordinate camera sizes, depth, reconstruction, outlines, temporal history, viewport changes, and upscaling. Global `ScalableBufferManager` settings alone are insufficient when this custom pipeline does not opt its targets into scaling.
2. **Reduce AO blur and reflection resolution.** Follow the existing Low AO path, then evaluate lower-resolution blur intermediates with correct reconstruction. SSPR uses a full-resolution R32_UInt random-write target; it is an integer compute resource, not an HDR color buffer. Dispatch sizes, bounds, indexing, and consumers must change together.
3. **Audit depth/stencil needs and precision.** The two 4K D32+S8 surfaces occupy about 126.6 MiB together. Where genuinely valid, D24+S8 could reduce that by about 63.3 MiB. This requires depth precision and stencil-use checks; no global replacement is justified yet.
4. **Audit HDR alpha, signed range, and lifetimes.** Two of the original six full-resolution RGBA16F surfaces are now packed TAA histories. Converting the remaining four could save up to another 126.6 MiB, but that is only an arithmetic ceiling. Consumer alpha, negative values, sampling and precision still need inspection; no global conversion is justified. Bandwidth savings are not automatically frame-time savings.
5. **Shadow distance/resolution before caching.** 4096² to 2048² D32 saves 48 MiB but trades quality. Proper caching must invalidate for camera motion, animated casters, foliage, light changes, and scene changes. A still turn-based map is not necessarily a static shadow scene.
6. **Expand the guarded texture audit.** The current resources patch is installed. Check remaining scene assets, data-texture exclusions and mip availability, then test a residency budget across camera travel and battles. Streaming flags alone cannot make mipless textures stream. Preserve exact-build backups and never overwrite updated assets with old copies.

A quiet fixed-view benchmark should first compare existing effect controls individually and resolution changes, with A/B/A ordering. A GPU frame capture is needed to distinguish expensive passes, actual draws/copies, and unused allocations. No measured per-pass GPU savings have been obtained in this session.

## Profiler changes and practical limitations

OldenPerf 0.2.0 now provides bounded, focused, scene-separated wall-time windows; CSV export; settling after scene/focus/resolution/cap changes; passive CPU submission measurements; optional engine recorder/whole-frame timing data; and explicit reporting of unavailable GPU metrics. Diagnostic scans are separated from statistics, skip loading scenes, and exclude subsequent measurement intervals. Diagnostic scans themselves took 49–235 ms in this run, so blindly including them in frame-tail statistics would distort the engine assessment.

Unity reports FrameTimingManager enabled but returns zero timing records. Engine profiling samplers are invalid in this retail player. The final build stops requesting captures after 300 unsuccessful polls. CPU submission durations are inclusive per-call timings and cannot substitute for GPU execution time, pass totals, or an independent observer-overhead test. Calls can include early returns and need not imply a dispatched GPU effect.

Native CPU hooks are restricted to the verified binary. Hooks involving `CameraData` were removed after an actual IL2CPP marshalling failure; the supported subset was exercised with the map visibly rendering and without trampoline errors. New game binaries disable those obfuscated hooks automatically.

The old pass-skipping ablation remains explicitly opt-in and is relabeled as a destructive whole-frame experiment. It is not a pass profiler. Icon eviction defaults off: modern `Resources.UnloadAsset` invalidates existing object references, contrary to the legacy implementation's assumption.

Validation: Release build with zero warnings/errors; numerical/bounded-buffer checks; six Python manifest/residency checks; independent decode of all 363 BC7 outputs; live menu and map smoke tests; packed-format inventory confirmation; unchanged user graphics settings. Battle image quality and controlled FPS gains remain untested. Build/deploy are now separate: use `dotnet build OldenPerf/OldenPerf.csproj -c Release -p:DeployToGame=true` to deploy explicitly. The previous plugin/config/log were backed up under the evidence directory's `before/` folder.

## Primary references

- [Unity temporary texture pooling](https://docs.unity3d.com/6000.0/ScriptReference/RenderTexture.GetTemporary.html)
- [CopyTexture compatibility](https://docs.unity3d.com/6000.0/ScriptReference/Graphics.CopyTexture.html)
- [UnloadAsset invalidation](https://docs.unity3d.com/6000.0/ScriptReference/Resources.UnloadAsset.html)
- [FrameTimingManager availability](https://docs.unity3d.com/6000.0/ScriptReference/FrameTimingManager.html)
- [FrameTiming metrics](https://docs.unity3d.com/6000.0/ScriptReference/FrameTiming.html)
- [GPU recorder delay and platform support](https://docs.unity3d.com/6000.0/ScriptReference/Profiling.Recorder-gpuElapsedNanoseconds.html)
