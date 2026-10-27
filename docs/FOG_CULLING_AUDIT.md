# Fog-of-war culling audit

Reviewed 2026-10-25 against the OldenPerf source and Steam build 26134266 (Unity 6000.0.66f1).

## Finding

Unity's built-in **Occlusion Culling** is not a fog-of-war system. It uses baked scene data to decide whether a camera can see renderers behind opaque geometry. The Unity manual describes baking that visibility data in the Editor and says the feature is a poor fit for runtime-generated scene geometry. It does not consume the game's per-player tile visibility map. This repository is a BepInEx mod with generated interop assemblies; it has no Unity scene-authoring project to rebake Olden Era's scenes.

Two Unity APIs are relevant to a mod:

- `CullingGroup` tracks camera visibility and distance bands for bounding spheres. It can notify the mod when a section's camera state changes, but it does not know whether map tiles are under fog.
- `Renderer.forceRenderingOff` is Unity's documented switch for custom visibility culling. It suppresses a renderer without changing the renderer's enabled state or deactivating its GameObject. A mod can combine this with the game's CPU-side fog data, provided it can prove which renderers belong to fully covered tiles and update them correctly.

The game already has camera culling: its custom render path calls `ScriptableRenderContext.Cull`, and `bxm.ppf(Camera)` applies a further section/frustum test. The missing layer is a conservative link between a section's tile footprint and the local player's fog state.

## What the measurements establish

- The full CPU fog-mask rebuild and reset ran at map load in the sampled session, not once per frame. The per-frame `bek.lmx` submission averaged about 0.005–0.007 ms; the inclusive fog process averaged about 0.020–0.024 ms. The reachability compute is time-dependent while converging, so camera stillness alone does not make it safe to skip.
- A camera-pan activation trace recorded 115 `Section Root` activations over 1 ms, totaling about 353 ms; mean was 3.07 ms and maximum 7.49 ms. A culling invocation in the trace reached 41.79 ms. These measurements make repeated section activation a real hitch candidate during pans; they do not prove that all of the culling invocation's cost came from `SetActive`.
- In a separate stationary audit, the manager made 720 unchanged `SetActive` calls over five invocations, but the whole manager cost only about 0.034–0.038 ms per invocation. Avoiding those unchanged calls is a small cleanup, not a likely dramatic win.
- With the terrain viewport fully fog-covered, whole-frame counters still showed 1,222 draws and 6.90 million submitted triangles. These include all cameras, shadows and repeated geometry. They identify a useful fog-culling experiment, not a count of geometry that can safely be removed.
- The animated home screen showed roughly 5,850–5,990 draws and 4.2 million triangles per frame. There is no CPU/GPU attribution for those submissions yet; map-section culling will not address the menu scene.
- The menu camera uses four shadow cascades at a 400-unit distance, while the adventure-map camera uses one cascade at 200 units. This gives the menu investigation a concrete shadow-pass target; its cost is still unmeasured, so the settings should not be changed on this evidence alone.

Evidence is in `/data/dev/workspace/RunScratch/olden-ui-pan-20261024/timing-only/cull-activation-slow.csv`, `/data/dev/workspace/RunScratch/olden-ui-pan-20261024/timing-only/cull-activation.csv`, and `/data/dev/workspace/RunScratch/olden-visibility-20261023/`. Do not compare captures across display resolutions or treat draw/triangle counters as GPU time.

## A tile-based design that fits the game

The useful precomputation is the mapping from each static section root to a conservative tile rectangle. Keep that mapping for the loaded map. When the authoritative local-player visibility data changes, recheck only roots overlapping the changed tiles; hide a section only when every tile in its expanded rectangle is still unexplored. Keep the fog surface itself visible, preserve a safety margin at edges, and restore renderers before a tile becomes visible. If fog state or mapping is unavailable, show the section.

Use the game's section registration data if its coordinates can be proven to share the fog grid. Otherwise derive bounds once from the section's renderers and map those bounds into tiles. A full tile-to-section lookup lets a changed tile find only affected sections. The fog test belongs on visibility changes, not in the frame loop.

## Gaps before an implementation is safe

1. **Fog bit meaning and layout:** the interop exposes `DataFogOfWar.visibilityMap`, but the native helper `bkfn` only establishes that its result negates the stored bit. Its game-level meaning, row stride, axis orientation and update writers still need to be confirmed against a live known-visible/known-unexplored tile.
2. **Section-to-tile correspondence:** section roots are named with coordinates and `bxm.ppb` registers integer coordinates, but the relationship to the fog grid has not been validated. Renderer bounds, neighboring tiles and the game's view rectangle also need edge checks.
3. **Section contents and lifecycle:** `SetActive(false)` disables every child component and invokes Unity lifecycle callbacks. A renderer-only replacement keeps non-renderer components and scripts active. The existing `SectionCulling.Promote` guard excludes only a few component types; there is no completed root-component inventory showing that promotion preserves behavior or CPU cost.
4. **All render consumers:** `forceRenderingOff` is per renderer, not per camera. The same objects may be needed by shadow, reflection, overlay or other camera passes. The currently available whole-frame counters cannot attribute those costs.
5. **Freshness and invalidation:** the draft polls the whole section set every 0.20 seconds. It has no verified dirty-tile event path, so it repeats work and could apply stale visibility after a reveal. The correct writer and notification point for fog changes are unknown.
6. **Image and performance proof:** there is no stable pixel-equivalent fog-edge capture for this change, and GPU frame/pass timings were unavailable in the sampled retail configuration. A candidate needs fixed-camera captures for visible, fully fogged and reveal-edge states, plus per-camera/pass counts and CPU timing.

### Implementation status (2026-10-25)

`OldenPerf/SectionCulling.cs` is now wired behind `Experimental.SectionFogCulling` (default off, restart required). It replaces the earlier camera-path draft: it never intercepts the game's frustum `SetActive` calls, so only fog-covered roots change, and only their renderers' `forceRenderingOff` is written. The draft's lookups did not match the installed build, so they were replaced after checking the interop assemblies in `BepInEx/interop` by reflection:

- `bxm.ppb(GameObject, int, int)` registers section roots. The old `ppb` overload in `dump-cs` and `decomp` is stale.
- `bek.lmj(Map, int, fqw, float[])` binds the map and side. `bek.lml` no longer exists.
- `Map.pmy(float, float, out int, out int)` converts world XZ to tile cells. `Map.pnb` no longer exists.
- `Side.fogData` is a property, and `DataFogOfWar.visibilityMap` is `bool[]` with `sizeX`/`sizeZ`. The old `fmu` grid fields are not used.
- `bkfn(i)` returns `visibilityMap[i] == 0`, and a sound caller indexes the grid row-major. This supports the polarity `true = visible/explored` and the layout `z * sizeX + x`, but only a live map can confirm it.

Roots are skipped (left visible) when they contain Terrain, Canvas or ReflectionProbe, have no renderers, have no bounds in the map, or touch the map edge after the margin. Any exception releases that root. A missing fog source shows every section.

Still unverified in play: fog polarity and layout on a live map, the section-to-cell mapping (bounds are derived from renderers, not `ppb`'s coordinates), and the cost of hidden shadow casters. Shadows are the largest visual risk: a hidden renderer no longer casts into visible tiles. Validate with fixed-camera captures at fog interiors and reveal edges before changing the default.

## Recommended order

1. Profile the main menu separately with passive CPU frame samples and per-camera/pass GPU timings. It is currently the user's worst-feeling scene, and its map-independent cost is unmeasured.
2. Collect the section-root component inventory and confirm the fog-grid coordinate/bit mapping in a live map.
3. Prototype fog-only `forceRenderingOff` updates on renderer-only sections, driven by fog dirtiness. Keep camera-frustum `SetActive` behavior unchanged in the first experiment.
4. Compare the same settled camera and settings before/after at fog interiors and reveal edges. Count renderer writes, draws, triangles and frame-time percentiles; retain the experiment only if it preserves the image and improves the target scene.

## Unity documentation

- [Unity 6 Occlusion Culling](https://docs.unity3d.com/6000.0/Documentation/Manual/OcclusionCulling.html)
- [Unity 6 CullingGroup](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/CullingGroup.html)
- [Unity 6 Renderer.forceRenderingOff](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Renderer-forceRenderingOff.html)
- [Unity Manual: occlusion culling with dynamic GameObjects](https://docs.unity3d.com/2020.2/Documentation/Manual/occlusion-culling-dynamic-gameobjects.html)
