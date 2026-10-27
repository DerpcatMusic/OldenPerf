# Visibility and repeated-work audit — OldenPerf 0.4.1

Tested Steam build 26134266 / Unity 6000.0.66f1 on 2026-10-23. This is a measurement update, not a new rendering optimization or an FPS improvement claim.

## Method

Three game launches: passive CPU timing; a bounded five-invocation SetActive audit; then rendering counters and camera pans. Loaded Chosen One/Profile 3 Autosave Day 2. Did not end a turn or save. Closed the game after testing. Tests used the current display's **2560×1440** output; the patch did not change resolution, shadows, effects or precision. Do not compare these frame times to earlier 3840×2160 sessions.

The previous native-quality depth optimization and texture patch remained installed. Detailed activeSelf queries add overhead, so their timing is not the passive cost measurement. Passive timings include Harmony/interoperability overhead and CPU command submission, not GPU execution. Nested timings must not be added together.

## Confirmed redundant CPU calls

Current native `bxm.ppf(Camera)` calculates camera planes, iterates bounds, checks an additional eligibility predicate, tests the eligible bounds, calls `Component.gameObject` and `GameObject.SetActive(result)`, then stores the visibility result. It does not compare the previous result before calling SetActive.

A scoped SetActive hook observed **720 calls in five stationary culling invocations; all 720 requested the already-current activeSelf value**. This is 144 redundant calls per invocation on this map. This is not 144 actual activation/deactivation transitions: Unity can reject unchanged state internally.

Separate passive runs measured the entire manager at roughly **0.034–0.038 ms mean per invocation**, once per observed frame. Removing this entire measured cost would still be a small gain in this scene. Replacing each call with an extra managed activeSelf query is not a demonstrated improvement and was not shipped. A correct cache must also account for object state changes originating outside this manager.

## Fog update frequency

The full CPU mask rebuild (`bek.lmo`) and reset/upload (`bek.lmp`) each ran **once during loading** and did not run repeatedly in the stationary/panning windows. The initial distance computation (`bek.lmq`) also ran once. The claim that the full CPU visibility grid is rebuilt every frame is not supported by these runs.

`bek.lmx` ran once per observed map frame. Current native code submits a texture copy, UpdateFogOfWar compute dispatch, and UpdateReachability dispatch. Its passive CPU submission mean was about **0.005–0.007 ms**. The outer fog process was roughly **0.020–0.024 ms** including its nested work.

Extracted the game's actual FogOfWar compute shader. UpdateFogOfWar samples input visibility, the prior fog map and distance data, interpolates values using delta time and decrements timer channels. A stationary camera is not sufficient proof that this work is redundant. A valid optimization needs dirty-input tracking plus a proven settled-state condition or an equivalent replacement. These small mask surfaces are not full-screen 4K targets: this session's inventory includes 202×202 FP16 RGBA and FP16 single-channel random-write targets and 192×192 R8 maps.

## Submitted rendering work with fog

Added working retail-player rendering counters. `GetAvailable` was stripped and failed to unstrip. The surviving native UTF-8 lookup (`ProfilerRecorderHandle.GetByName__Unmanaged`) successfully resolves Draw Calls Count, SetPass Calls Count, Triangles Count and Vertices Count; the plugin records their values separately from milliseconds.

Representative settled ten-second windows, same loaded session and unchanged quality settings:

| Camera view | Frames | Mean draws/frame | Mean triangles/frame | Mean SetPass/frame |
|---|---:|---:|---:|---:|
| Initial visible island | 819 | 1,186.0 | 6,010,659 | 510.0 |
| First pan, fog over most of view | 842 | 1,195.3 | 5,748,582 | 495.9 |
| Further pan, narrow visible strip | 779 | 1,073.5 | 6,477,414 | 513.0 |
| Further pan, terrain viewport entirely fog-covered | 889 | 1,222.1 | 6,895,062 | 517.4 |

These are **whole-frame engine counters across all cameras and passes**, including shadows and repeated geometry. They are not unique scene triangles, fragment counts, GPU times, or counts of proven removable hidden objects. Panning changes the camera frustum and shadow coverage. The evidence establishes that a fog-covered view still submits substantial geometry; it does not identify which submissions can be removed without affecting visible output. Static camera, same-frustum visibility-controlled captures and per-pass attribution are needed to quantify that subset.

The animated main menu also reached roughly 6,000 draws and 4.2 million triangles per frame. That is another submission-heavy scene to examine, not proof those draws are duplicates.

## What exists already

The native render path uses Camera.TryGetCullingParameters and ScriptableRenderContext.Cull. The custom manager above provides additional frustum/eligibility culling. It is incorrect to say there is no culling.

Map generation contains IJobParallelFor jobs, including cellular automata and k-means. The installed player contains lib_burst_generated.dll. These facts do not establish runtime CPU efficiency, but they rule out a blanket claim that the developers ignored parallel jobs. Scalar float instructions also use XMM registers; their presence alone does not establish multi-object SIMD vectorization.

## Priorities supported by the evidence

1. Attribute geometry and shadow submissions in the fully covered camera view. Then exclude fully concealed chunks/props from the passes where they cannot affect visible pixels. Preserve fog edges, silhouettes/magic vision, shadow contributions, reflections and reveal behavior.
2. Trace the reachability and fog inputs and their transition state. Test whether individual dispatches or regions can be omitted after convergence, rather than stopping all fog updates based on camera motion.
3. Examine shadow caster coverage and reuse of static data. Stationary camera does not imply static animated casters or lighting; no shadow freeze was implemented.
4. Audit batching/pass dependencies and buffer lifetimes. SRP Batcher is enabled, but that does not guarantee few draws or eliminate repeated geometry.
5. Treat the redundant SetActive calls as a small cleanup opportunity unless a larger map demonstrates a materially larger cost. Do not prioritize SIMD for this 0.04 ms loop ahead of proven larger work.

No new culling, shader skip, pass removal or quality reduction is enabled by 0.4.1. GPU frame/pass timings remain unavailable in this retail configuration; CPU submission times and triangle totals cannot substitute for them.

## Profiler changes and validation

- Added CPU submission timings and lifetime call counts for map culling, fog mask rebuild/reset, fog processing, initial compute, transition compute and fog draw. Lifetime counts distinguish loading-only calls from a path that was never observed.
- Added rendering-counter CSV (`BepInEx/oldenperf/render-counters.csv`) with count units, separate from timing CSV.
- Added optional `Profile.AuditVisibility` diagnostic, disabled after the test and off by default. It observes five settled map culling invocations and never suppresses a call.
- Unknown GameAssembly builds disable the obfuscated method hooks.
- Release build: zero warnings/errors. Existing sample-window/depth-equality checks and six Python manifest/residency checks passed. Live hooks and all four rendering counters were exercised in game.

Unity documents rendering counters and their release-player availability in the [rendering profiler reference](https://docs.unity.com/en-us/engine/6000.7/manual/analysis/graphics-performance-profiling/profile-rendering/profiler-rendering); actual availability here was established by the live player.

Evidence: `/data/dev/workspace/RunScratch/olden-visibility-20261023/`: passive-stationary.log, passive-with-fog-view.log, detailed-setactive.log, counters-visible.log, counters-fog-edge.log, counters-full-fog.log, counters-covered.log, render-counters.csv and matching screenshots. `counters-full-fog.png` still has a narrow visible strip; **counters-covered.png** is the entirely covered terrain viewport. Raw fog shader metadata and extracted compute bytecode are in the same directory.

## Round 2: culling predicate and invalidation diagnostic (2026-10-23)

Native trace (GameAssembly.dll, SHA256 eb57…e940, read-only radare2 at file offsets):

- `bxm.ppf(Camera)` computes frustum planes, then for each managed bounds entry `i` calls `0x182CE56E0`. That wrapper converts `i` into grid coordinates `(i % W, i / W)`, with `W` read from the map-view object at `+0x2c`, and calls `0x2CE5E80`.
- `0x2CE5E80` is a rectangle test. The cell is eligible only when it lies inside `rect[b]`, where `b` is the current view index at `+0x28` and `rect` comes from the view list. In the decomp typemap this view object is `bzf` (`pzs` returns a `RectInt` of a `MapView`), so eligibility is "section is inside the current map view".
- Ineligible entries are still passed to `SetActive(false)`. Eligible entries go through `TestPlanesAABB`, then `SetActive(visible)`, then a result store.
- Current native analysis identifies view-index writers `bzf.xs`, `bzf.iyc`, `bzf.pzl`, `bzf.cyt` and `bzf.jer` (`void(int)`, store to +0x28). `bzf.pzo(int,int)` is a Boolean grid query, not a setter. Hooks still do not cover all in-place array writes.

Runtime diagnostic (`Profile.AuditCullInvalidation`, default false, never changes culling): counts `ppf` calls whose camera planes are bit-identical to the previous call on that camera, `SetActive` writes made inside `bxm`, writes that repeat the value `bxm` already wrote, and `SetActive` writes from outside `bxm` to objects it manages, including those that diverge from the last state it wrote.

Stationary Autosave Day 2 window (cumulative counters from the game log, 2560×1440):

| counter | value |
|---|---:|
| `ppf` calls | 4,547 |
| camera planes unchanged vs previous call | 4,545 (99.96%) |
| `bxm` SetActive writes | 654,768 |
| of those, repeats of the state `bxm` already wrote | 654,617 (99.98%) |
| external SetActive writes (all, any object) | 79,399 |
| external writes to `bxm`-managed objects (144) | **0** |
| external writes that diverged from `bxm`'s last state | **0** |

Interpretation: repeated planes and state writes suggest a caching opportunity in this session, but do not establish cache correctness. The view index, view rectangles and all other dependencies must also remain unchanged or invalidate the cache. Unity's internal writers (animation clips toggling `m_IsActive`, native code calling `SetActive`) bypass the managed hook, so the hook cannot prove absence. The total CPU cost is the earlier ~0.035 ms per culling invocation, so even a perfectly safe skip is a negligible saving. **No skip cache was implemented or enabled.**

Pixel-equivalence harness: `tools/frame_compare.py` and `tests/test_frame_compare.py`. Stationary captures of the same map view (`map-c`, `stationary-1`, `stationary-2`) differ on about 70% of pixels with peak delta near 245. That is far too large for a valid noise envelope, so no candidate can be judged equivalent in this session. Its cause (camera drift, a whole-frame animation or a capture-timing artifact) is unresolved. Until a truly fixed frame is established, any envelope from these captures is not usable.

Remaining blockers:

1. Identify the `bzf` view-index writer and whether the view or rect can change while the camera is still.
2. Fix the capture procedure so repeated stationary frames match (locked camera, no edge pan, fixed animation time), then re-measure the noise floor.
3. Fog-covered geometry culling still needs a CPU-side source of section visibility that is not a GPU readback. Without it, no draw reduction is safe.

Evidence: `/data/dev/workspace/RunScratch/olden-round2-20261023/` (`cull-invalidation.csv`, `LogOutput-round2.log`, captures `map-c.png`, `stationary-1.png`, `stationary-2.png`).

## Parent review: comparison tool hardened (2026-10-24)

The original global noise-envelope acceptance rule was unsafe: a candidate could introduce an error in a previously stable location while remaining below the count and maximum delta of unrelated noisy pixels. The tool now reports reference noise as **inconclusive**, never as permission for a candidate difference. It requires bit-identical references and candidate RGB values throughout the identical, non-empty comparison mask before returning a pass. The pass describes only that captured region, not masked pixels, internal state or future frames.

Added regressions for a new error hidden by unrelated noise, an empty comparison region and a candidate mask that excludes its own error. All 16 Python tests pass. Re-running the supplied map captures returns `inconclusive_reference_noise`, as required. This fixes validation infrastructure; no rendering optimization or performance gain is claimed. The installed 0.4.1 plugin remains unchanged.

## Round 3: UpdateReachability dispatch (2026-10-24)

**No cache or stage-output validator was implemented.** The dispatch is time-dependent while converging; unchanged source data alone does not justify a skip. This does not rule out caching after a proven fixed point with correct invalidation.

Shader (`UpdateReachability0.dxbc`, disassembled with `dxbc_disasm.exe`; `cs_5_0`, 8×8 groups, `ReachabilityData` as `t0`, `ReachabilityMap` as `u0`):

- If `cb0.y != 0`: `map = data.x` (initialisation copy, replicated to all channels).
- Otherwise: `map = map + cb0.x * (data.y - map)`, computed on `map.y` and replicated to all channels. `cb0.x` is `k`, a relaxation factor.
- The saved kernel disassembly reads only `cb0.x` and `cb0.y`; `cb0.z` and `cb0.w` are unused by this kernel.

Live CPU-side binding (`Profile.AuditReachability`, diagnostic, default off, never changes a dispatch):

- `bek.lmx` runs once per map frame and dispatches `FogOfWar` kernel 0 (`UpdateFogOfWar`) and kernel 1 (`UpdateReachability`) repeatedly. The saved final trace reports 9,063 dispatches per kernel across 9,062 observed `Time.frameCount` values; this is approximately once per frame, with an additional invocation.
- Kernel 1 writes no parameters of its own. It reads the uniform block set before kernel 0. That block carries `F3014`, which takes values of the same magnitude as `Time.deltaTime` (≈0.014–0.021 in steady play, with spikes to 0.093 and 0.30). `F3014` is therefore `k`, and the update is `map += dt·(data − map)`, i.e. time-dependent.
- `k == 0` would make the update an exact identity, but it requires `dt == 0`. `Time.timeScale` stayed at 1 in play and while the in-game menu was open, so that case was not reached.
- While values are converging, skipping at positive `dt` generally changes the output. Fixed points, quantization and actual floating-point rounding require stage-output validation. Per-frame CPU readback is not the only possible convergence test: GPU reduction or a proven bound plus dependency invalidation could be investigated. No convergence detector, correctness proof or cost comparison was implemented, so neither impossibility nor a readback-versus-dispatch performance claim is established.

Evidence: `/data/dev/workspace/RunScratch/olden-round3-20261024/` (`reachability-audit-map-session.txt`, `LogOutput-round3.log`, screenshots). Earlier round-2 notes remain valid.

Not established: the writers of `ReachabilityData` (`t0`) and whether any other writer touches `ReachabilityMap`; the per-frame readback comparison; any GPU time for this kernel. Those need a separate stage validator.

## Round 4 prototype — not deployed or merged

Branch `perf-cull-cache` contains an experimental map-culling cache plus a validation mode. It observes camera planes, the view-index setter and managed activation writes. This is not yet a complete invalidation contract: bounds, view-rectangle/object membership changes, direct field writes and native activation writers still need tracing. A zero-mismatch run alone cannot establish that unobserved native activation changes are safe, because the original culling pass may simply restore an object to its predicted desired state.

Parent review corrected a shared-state bug: a pass for camera B overwrites scene activation state, so camera A cannot reuse its earlier entry. Only the most recent completed camera pass remains eligible. A regression covers A → B → A. Validation mode now takes precedence over skipping; exceptions, reentrant culling and observed prediction mismatches invalidate state and disable skipping.

The plugin builds without warnings/errors; the C# checks and 16 Python tests pass. These checks cover cache behavior, not Harmony interception or visual correctness in the game. Live testing is blocked by the missing desktop session (seat0 greeter, no X/Wayland compositor). The installed DLL hash remains `a72ad19ca6063c2844d278363e5749373b058fc3a5f228a685640200f7a26936`, stable 0.4.1. Both experimental switches remain off. No performance gain or safe activation of the cache is claimed.
