# Olden Era rendering and CPU optimization report

Scope: Steam build 26134266 (0.82.04 BETA), Unity 6000.0.66f1, IL2CPP, custom HexSRP pipeline. Test hardware: AMD RX 6600, 8 GB, Linux/Proton/DXVK (D3D11 to Vulkan/RADV). Original baseline below used `perf-mod` at 1312903 and a 2560×1440 display unless stated; the current integration and later native-4K tests use `perf-live-stutter`.

This report separates measurements and implementation from candidates. No confirmed FPS gain is claimed. The baseline report was read-only; the subsequent patch and validation status is recorded below.

## Texture follow-up, 2026-10-27

The 25 October asset pass covers 26 exact-hash files and reduced the full-chain payload estimate by 49.60 MiB. On 27 October, a further 208 textures across 25 files were compressed after per-level decode checks: 182 BC7 and 26 BC1, including 21 retained mip chains. The total serialized payload estimate is now 84.13 MiB lower than before both passes; this is not physical VRAM residency. After reports of soft city textures, the current config sets the global mip limit to 0 and disables runtime mip streaming. The game remained closed, so the new visual and residency behavior has not been checked live. Full quality gates and exclusions are in [TEXTURE_COMPRESSION_FOLLOWUP.md](TEXTURE_COMPRESSION_FOLLOWUP.md).

## Current validation status, 2026-10-24

- **Patch-induced scene stalls fixed:** automatic inventories ran on the main thread after scene changes, with logged costs of 142.8–467.6 ms and one 6,262 ms scan. They are now opt-in (`InventoryOnSceneChange=false`, `InventorySeconds=0`). These stalls came from OldenPerf diagnostics, not the stock engine. Excluding them from benchmark statistics never prevented the visible freeze.
- **Map-culling tail requires attribution:** recorded medians were 0.036–0.048 ms while some windows reached p99 71 ms and a maximum of 115 ms. Camera motion was not recorded in those windows. Synchronous object activation is a candidate, not a proven cause. A bounded activation trace now records slow object paths and camera steps, then removes its hooks; no culling skip is enabled.
- **Live activation trace now identifies expensive section transitions:** in one instrumented 421-call window, culling averaged 0.851 ms and peaked at 41.79 ms; 149 state transitions included 84 activations. Individual `Section Root` activations took up to 7.493 ms, with repeated slow activations of the same sections. Camera step reached 19.4377 world units. This attributes measured time to `SetActive`, but does not distinguish callbacks, renderer registration, physics or other Unity work inside it. The trace adds overhead, so this run is not an FPS comparison. Evidence: `/data/dev/workspace/RunScratch/olden-ui-pan-20261024/timing-only/`. The game exited normally before trace expiry; runtime hook removal at expiry was not observed. The trace is disabled for the next launch to isolate unit-window measurement.
- **Fog skip disabled:** an original-GPU R8 fixed-point check passed over 36,864 bytes on the tested map, but invalidation coverage and command ordering are not fully safe. A later write can invalidate an already-recorded skip before command execution. Detection does not repair the omitted dispatch. Neither a general correctness result nor a net speedup is established.
- **SSAO actual lifetime adapter validated at both 2560×1440 and 3840×2160:** after the independent replay proof passed, the actual delayed C acquisition / early A release ran. At 4K, frames 11816–11818 each had zero differing R8 bytes against the unmodified independent replay; wrong-input controls differed at 1,620,232, 1,621,011 and 1,620,850 pixels. The three-frame gate passed and diagnostic command-buffer hooks removed themselves. Logical peak-live reduction: **31.64 MiB at 4K** (14.06 MiB at 1440p). Physical pool residency and FPS remain unmeasured. No format, precision or effect-resolution changes. Evidence: `/data/dev/workspace/RunScratch/olden-ui-pan-20261024/actual-lifetime/actual-passed-4k-unit-reopen.log`.
- **HUD alpha experiment:** two observed HUD textures passed 2,064 native bilinear samples each with zero alpha-bit differences and zero hit differences at threshold 0.2 between original RGBA32 and a CPU Alpha8 copy. Originals remained readable and unchanged. No production hit-test redirection or CPU-copy removal is enabled. Only two textures and sampled coordinates were covered; GPU constructor allocation is unmeasured.
- **UI memory scope:** the existing city inventory contains 46.79 MiB of uncompressed UI-pattern texture objects, not a captured unit-window footprint and not physical VRAM residency. Nine distinct HUD-panel instances account for 31.95 MiB; identical pixels and redundant ownership have not been demonstrated. Vectorizing painted art is not a proven quality-preserving saving. A first timing-only unit open took 60.72 ms CPU inclusive, including 29.59 ms in preview Show; the next three frames were 193.12, 65.46 and 32.12 ms. One cold sample does not establish repeat-open cost or GPU attribution. The preview target was 3099×3322 RGBA16F (78.54 MiB color payload alone), depth setting 32; the actual depth format was not logged in that run. Native code reuses same-sized RTs within an instance. **New repeat-open evidence:** in a later 4K run, the same unit's second opening took 367.94 ms inclusive, including 312.82 ms in `BhUnitPreviews.itt` creation; the next frame was 461.69 ms. It bound a different camera and RT instance at the same 3099×3322 size, now confirmed D32_SFloat_S8_UInt. This demonstrates pool-miss creation on that reopening, not a general repeat-open distribution. `Hide` returns the preview frame through `its`, clears the model reference, and appears to omit the model pool's `itq` return; a default-off correction now captures the model before Hide and returns it through native `itq` after successful Hide, with offset/ownership/inactive-state guards. Build checks pass; live correction testing remains pending because the desktop session ended at the greeter. The camera/RT belong to the separate frame pool, so model reuse is not itself proof of RT savings. The desktop display connection and game process disappeared before the third sample; cause not established. See `UNIT_PREVIEW_RT_AUDIT.md`.
- **Depth-copy result reproduced:** another full-4K probe found zero differing float32 bits over 8,294,400 samples, with non-flat depth. This validates the sampled depth format change on that run, not whole-frame performance.

The original developer-facing findings below retain their stated evidence and limits. Current probe logs and integration notes are in `docs/worknotes-render-resource-events.md`, `docs/work-notes-compute-events.md`, and `docs/UI_OPEN_AUDIT.md`.

## SENDABLE: findings for developers

### Confirmed and measured

- **In the tested map path, the sampled depth copy originally used D32+S8 at full 4K.** `_CameraDepthTexture` uses D32_SFloat_S8_UInt (63.28 MiB). The stencil is not read through this sampled copy; main-buffer stencil is still needed by lit, outline, TAA and UI shaders. A D32_SFloat copy keeps identical float32 depth and uses 31.64 MiB. Verified bit-for-bit over 8,294,400 samples (0 differing, 0 invalid). Render-target inventory 1,118 → 1,086 MiB. This is a logical allocation saving, not a measured VRAM residency or FPS result. Opt-in `Memory.TrimDepthCopy` with a runtime equality gate.
  - Why it matters: a full depth/stencil copy is wider than the consumer needs.
  - Remaining: one-time GPU readback during validation can stall; the copy-shader cost is not GPU-timed.
- **Historical first-pass texture result (23 October):** 363 eligible textures compressed to BC7 (491 → 123 MiB payload, 368 MiB saved, 2,110 streaming flags restored). Minimum decoded PSNR was 31.87 dB. On that same save Unity's texture counter moved 3,151 → 1,749 MiB; that combines compression and residency and is not physical VRAM. The installed 25 October multi-format pass is recorded above.
- **Redundant SetActive writes in the map culling pass** (`bxm.ppf`): 720 calls in five stationary invocations, all setting the already-current `activeSelf`. Passive cost of the whole manager is about 0.034–0.038 ms per invocation. Correct but small.
- **Culling-state repetition:** 4,545 of 4,547 `ppf` calls had unchanged frustum planes; 654,617 of 654,768 `bxm` SetActive writes repeated the state it had already written. Zero external writes to the 144 managed objects were observed. This does not prove absence of native or animation writers, and the saving is tiny (above).
- **Whole-frame submission on the adventure map is high even when fog covers the view:** 1,186–1,222 draws and 5.7–6.9 M triangles per frame across all cameras and passes, including shadow and repeated geometry (`render-counters`, Draw Calls and Triangles recorders, 2560×1440). Fog-covered viewport: 1,222 draws / 6.90 M triangles. Main menu: about 5,850–5,990 draws per frame. These are submission counters, not GPU costs, and not proof of removable draws.
- **Fog compute CPU cost is small per frame:** `bek.lmx` CPU submit is about 0.005–0.007 ms; the full CPU fog mask rebuild and reset ran once at load, not per frame.
- **Unity's retail profiling gap:** `FrameTimingManager` returned zero records after 300 polls. Unity documents a release-player Frame Timing Stats requirement, but the cause of zero records here is unresolved: the feature query reported enabled. Result: we obtained no GPU frame or pass timings in the tested configuration; this does not prove that profiling the build is impossible. Recorders for Draw Calls, SetPass, Triangles and Vertices do work.
- **The mod's lazy icon unload experiment was unsafe; this is not a finding against the stock engine.** Unity's `Resources.UnloadAsset` documentation states that the object "will become invalid". This matches a disabled-by-default option (`Textures.LazyIcons = false`). Keep it disabled.

### Candidates that need profiling or proof before any change

- **Full-resolution bandwidth and buffer lifetimes.** Keep output and effect resolution unchanged. Trace attachment consumers, alpha/sign/precision requirements, load/store operations and overlapping lifetimes; eliminate dead attachments, redundant stores or duplicate snapshots only where dependency analysis proves equivalence. Do not treat every RGBA16F allocation as unnecessary.
- **Shadow atlas 4096² D32 (64 MiB).** Map camera uses one cascade (distance 200); menu camera uses four (distance 400). Caching requires invalidation for camera motion, animated casters, lights and scene changes. Static caster separation is a candidate.
- **AO and SSPR intermediate data and passes.** Preserve effect resolution and quality. SSAO uses three ARGB32 intermediates and a final R8 target; the intermediate shaders use multiple components, so dropping them is not a valid optimization. Investigate redundant reads/writes, equivalent packing and correctly fused filtering. SSPR uses a full-resolution R32_UInt random-write target: retain its integer/hash semantics, and investigate dispatch coverage, redundant work and dependencies before changing it.
- **Fog visibility and reachability.** `UpdateReachability` relaxes toward a target with factor `k = deltaTime`, so the result is time-dependent while converging. Skipping is not safe during convergence. A proven fixed point plus dependency invalidation, or a GPU-side convergence flag, are candidates to investigate. Other equivalent formulations may exist. Detector overhead and net GPU savings are unmeasured.
- **Culling of fully concealed geometry.** Shadow and main-pass submissions for fog-covered chunks. Must preserve fog edges, reveal behavior, shadow contributions and silhouettes. Requires a CPU-side section visibility source that does not come from GPU readback.
- **Lifetime and pooling of render targets.** `GetTemporary`/`ReleaseTemporary` pooling is the intended pattern; the pool may reuse textures whose contents are not guaranteed. Peak live resource analysis is the right question; API call count is not.
- **Full-screen copies.** Some are conditional. Fusing the final post-process copy into the output was tested and rejected: refraction, foreground and overlay consumers need a separate snapshot, and aliasing it creates read/write hazards. Fusion needs an explicit MRT or proven dead-output check.
- **CPU jobs and SIMD.** Map generation already uses `IJobParallelFor` (cellular automata, k-means) and `lib_burst_generated.dll`. Those library and job presences do not establish how efficiently runtime hot loops are vectorized or scheduled. Investigate hot loops, data layout and critical path before SIMD work.

### Rejected or retired (do not repeat)

- Packed R11G11B10 TAA histories: reduces precision and removes alpha/sign. Disabled by user decision.
- Blanket D24 depth: no measured precision or stencil case for it; format support was not established in this build.
- Reduced render scale and 2048² shadow cap as "optimizations": quality reductions. Retired in 0.4.0. Their 51 → 94 → 25 ms windows are confounded by GPU contention and must not be quoted as gains.
- Final color-copy rerouting: rejected, see above.

## Coverage of other optimization categories

These categories were discussed or are relevant to a broad review, but have not been established as stock-engine defects. They belong in the profiling backlog, not a list of accusations:

- **CPU allocations and GC:** capture allocation stacks and per-frame byte counts, including managed/native marshalling. Temporary render-target pool calls alone are not proof of heap-allocation waste.
- **Transforms and bounds:** measure repeated updates for unchanged static objects, then use dirty events that also cover hierarchy, bounds and object membership changes.
- **Animation, skinning and particles:** measure hidden/offscreen presentation updates; preserve visible shadows, reflections, events and correct reveal state. Gameplay simulation must continue under fog.
- **UI:** profile layout/canvas rebuilds and repeated material/property updates. No UI rebuild-cost evidence has been collected yet.
- **Batching and instancing:** SRP Batcher is enabled. Capture material/pass switches and instancing opportunities; high draw count alone does not identify redundant draws.
- **Geometry and occlusion:** inspect chunk granularity, conservative bounds and occlusion work. Preserve the existing visible detail; lower LOD detail is not a same-quality improvement.
- **GPU overdraw and transparency:** capture fragment counts, early-depth behavior and transparent layers. No measured overdraw attribution exists yet.
- **GPU compute/shader execution:** inspect occupancy, divergence, cache locality, workgroup dimensions and synchronization using an actual GPU capture. Scalar CPU XMM instructions say nothing about these GPU properties.
- **CPU/GPU synchronization and uploads:** trace waits, readbacks, immutable-data uploads and resource barriers. Diagnostic readbacks are not representative game-frame work.
- **Static shadows and derived map data:** trace current dirty flags first, then separate static dependencies from animated/light-dependent work. Do not duplicate existing event-driven behavior.
- **Residency and streaming:** correlate allocation/residency changes with transfer stalls and evictions under controlled competing GPU load. Shared-system-memory allocation is not sufficient evidence of VRAM spill.
- **Whole-frame/event-driven rendering:** cache static inputs or layers with explicit dependency invalidation. Continuous animation, fog, water, UI and temporal AA can still require a presented frame every refresh; a stationary camera is not an adequate global invalidation rule.

## SENDABLE: measurement guidance

- Compare only identical build, resolution, settings and scene. The earlier 3840×2160 and current 2560×1440 numbers are not comparable.
- Use A/B/A ordering and fixed camera, with settled windows. Exclude diagnostic scans (49–235 ms) and first cold loads from frame statistics.
- CPU submission timings are inclusive and nested; they cannot be summed and are not GPU time.
- Residency gauges (requested vs resident VRAM and GTT) do not measure transfer stalls. GTT allocation alone does not prove spill.
- Pixel comparison: a region is only usable if reference captures are bit-identical. Noisy references are inconclusive, not passes.

## Internal inventory and evidence matrix

Evidence root: `/data/dev/workspace/RunScratch/` (paths below are relative to it). Repo docs are in `docs/` of `perf-mod`.

| Topic | Status | Evidence | Notes / source |
|---|---|---|---|
| Depth copy D32 (TrimDepthCopy) | Deployed opt-in, validated (4K bit-identical) | `olden-native-quality-20261023/depth-copy-equality.txt`, `native-depth-inventory.csv` | Source default `false` (Plugin.cs:47); installed config enables it. Stencil retained on main buffer. |
| Multi-format asset compression | Two forward passes deployed and asset-hash verified; live VRAM pending | `/data/dev/workspace/PluginArtifacts/OldenPerf/{multi-format-20261025,mip-compression-20261027}` | 84.13 MiB cumulative serialized full-chain estimate saved, not physical residency; mip streaming disabled in current config. |
| Packed TAA history | Rejected (user) | `docs/NATIVE_QUALITY_AUDIT.md` | Source default `false` (Plugin.cs:45). ENGINE_AUDIT.md still describes it enabled (historical, superseded). |
| Final color-copy reroute | Rejected | `olden-native-quality-20261023/copy-reroute-rejected.log` | Consumer snapshot dependency. |
| Retired render-scale + shadow cap | Retired (0.3.0) | `docs/AGGRESSIVE_PRESET.md`, `olden-aggressive-20261023/` | Quality reduction; removed in 0.4.0. |
| SetActive redundancy (bxm.ppf) | Investigated, small | `olden-visibility-20261023/passive-stationary.log`, `detailed-setactive.log` | 720 redundant calls/5 invocations; 0.034–0.038 ms. |
| Cull invalidation / repetition | Investigated, candidate | `olden-round2-20261023/cull-invalidation.csv` | 0 external writes observed; native writers not excluded. |
| Prototype map cull cache | Experimental, unmerged, default off | branch `perf-cull-cache` @ 3850b7e (`OldenPerf/CullCache.cs`, `MapCullingCache.cs`) | Hooks deduplicate by native entry; method names use the exported IL2CPP accessor. In-place bounds/results/rect writes remain incomplete. No game test or measured speedup; 144 state reads per hit may cost more than the original loop. Keep skip disabled; not merged. |
| Draw/triangle counters | Measured (submission, not GPU) | `olden-visibility-20261023/render-counters.csv` | Map 1,186–1,222 draws; main menu ~5,850–5,990. |
| Fog CPU mask/reset | Measured once at load | `olden-visibility-20261023/passive-*`, `detailed-setactive.log` | Not per frame. |
| Fog UpdateFogOfWar shader | Analyzed | `olden-visibility-20261023/UpdateFogOfWar.asm`, `fog_shader.json` | Interpolates by dt, decrements timers; cannot skip while camera stationary without proof. |
| UpdateReachability | Analyzed, not safe to skip | `olden-round3-20261024/UpdateReachability0.asm`, `reachability-audit-map-session.txt` | Reads cb0.x and cb0.y only; 9,063 dispatches vs 9,062 frames. Writers of ReachabilityData not identified. |
| Shadow atlas 4096² D32 | Investigated, candidate | `olden-engine-audit-20261023/current-isil/IsilDump/Hex/bfs.txt` | 64 MiB; map cascades 1, menu cascades 4. |
| SSAO intermediates | Runtime prototype, unwired and undeployed | branch `perf-render-resource-events`, `OldenPerf/SsaoLifetime.cs`; `olden-ssao-reuse-20261024/shader-audit.json` | Defer C acquisition and release dead A before pass 2 on High only. Identical formats/passes/dimensions. Potential 31.64 MiB lower simultaneous logical allocation at 4K; physical reuse and output equality untested. |
| SSPR R32_UInt compute | Investigated, candidate | `…/Hex/bey.txt` | Full-res random-write; dispatch change needed. |
| Fog/culling pass classification | Not done | — | Requires CPU-side section visibility. |
| Lazy icon unload | Rejected (unsafe) | `OldenPerf/Plugin.cs:39` | Unity doc: object becomes invalid. |
| Burst/Jobs | Present; hot loops unprofiled | map-gen `Elevation` / k-means jobs; installed `lib_burst_generated.dll` | No runtime hot-loop profile. |
| GPU pass timings | Unavailable | `olden-native-quality-20261023/native-quality.log` ("no GPU timings") | Zero records despite an enabled feature query; cause unresolved. |

### Primary sources checked (2026-10-24)

- Unity `Resources.UnloadAsset`: https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Resources.UnloadAsset.html : "The object will become invalid and can't be loaded back from disk."
- Unity `FrameTimingManager`: https://docs.unity3d.com/6000.0/Documentation/ScriptReference/FrameTimingManager.html : Frame Timing Stats needed in release Player; GPU time zero when missing; Linux OpenGL reports no GPU time (this build uses D3D11/DXVK, so this exception is not the cause).
- Unity `RenderTexture.GetTemporary`: https://docs.unity3d.com/6000.0/Documentation/ScriptReference/RenderTexture.GetTemporary.html : pooled; contents not guaranteed; release after use.
- Microsoft D3D11 depth-stencil: https://learn.microsoft.com/en-us/windows/win32/direct3d11/d3d10-graphics-programming-guide-depth-stencil : sampling a depth buffer requires a typeless resource with typed SRV (e.g. R32_TYPELESS / R32_FLOAT) and matching DSV. The D32_FLOAT copy in this report is a separate shader-written target, not a depth-stencil reinterpretation.

Not checked: Unity `GameObject.SetActive`, `Graphics.CopyTexture` and `GraphicsFormatUsage` pages (cited in older docs but not re-fetched this session).

## Limitations

- No GPU pass timings were obtained in the tested configuration. GPU cost remains unattributed.
- Battles, other factions, lighting and mixed-camera transitions were not tested for image equivalence.
- Desktop was unavailable; the prototype cull cache has no in-game test.
- Obfuscated native names can change per build; hooks fail closed on an unverified hash.
- This list is a review of documented and sampled work, not an exhaustive search of all possible optimizations.
- Triangle totals are whole-frame, all-camera counters. They are not unique scene triangles and must not be read as removable.

## Parallel implementation review, 2026-10-24

All three delegated tracks have returned. None establishes a new measured runtime gain. CPU hooks and failure handling were corrected, but skip is default-off with incomplete writer coverage. The compute skip prototype is explicitly quarantined (`RuntimeSkipValidated=false`); its scalar checks are not GPU equivalence evidence. The resource planner checks pass; the parent added an unwired SSAO lifetime adapter and verified shader texture slots and overwrite blend state from installed metadata. The desktop is at the login greeter, blocking the requested integrated game test. Installed plugin SHA remains a72ad19ca6063c2844d278363e5749373b058fc3a5f228a685640200f7a26936. This is the plugin hash, distinct from GameAssembly SHA eb57…e940.
