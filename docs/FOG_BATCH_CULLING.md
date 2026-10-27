# Fog filtering inside combined prop meshes

Build 26134266, GameAssembly SHA256 `eb57aeecaa27ff5764ddff76c5a3459857ed73f3abdefb14cfa370facb85e940`.

The earlier per-renderer culler misses an engine rendering path. `bzc` combines source prop meshes into section/material batches, stores actual positions and transforms in separate GraphicsBuffers, and gives its generated meshes deliberately enormous bounds. `SectionCulling.RendererHidden` rejects bounds wider than 20 world units. It therefore cannot establish which individual props inside those batches are concealed.

## Native source trace

The authoritative current-build disassembly/ISIL is under `/data/dev/workspace/RunScratch/olden-engine-audit-20261023/current-isil/IsilDump/Hex/bzc.txt`.

- `pyt`, `ib`, `mw`, `lt`, `lty` build material batches with at most 65,536 vertices, 1,048,576 indices and 4,096 instances.
- `pyu` / `hpb` append a source mesh's triangle indices with winding correction and a vertex offset. Their final two arguments are the vertex and index cursors. The source model's `InstanceData` has already been written before this call.
- The current interop accessor `cbsv` (native offset `0xB0`) is the UInt16 triangle index stream. `cbsw` (`0xC0`) stores per-vertex instance IDs. `cbsx` (`0xD0`) contains 76-byte instance records, with the object-to-world matrix at offset 0 and vertex offset at 48.
- `kdv`, `pyv`, `iye` create a MeshRenderer for the generated mesh and parent it into a section. Its shader reads the separate vertex and instance buffers.
- The fog update uses `bxm.ppf(Camera)`, the simple map-culling entry already exercised by FrameProfile. The earlier `bfn.lsk` hook has been removed: its `ref CameraData` contains object references that this player's interop cannot marshal safely. FrameProfile already excludes that signature. Live toggling leaves native hooks installed, so the initial on/off result cannot rule out the removed hook as a regression source.

Generated interop signatures and record layouts were checked independently against the installed `Hex.dll`. Native entry addresses are deduplicated before patching aliases. The build hash and 76-byte layout are required at runtime.

## Implementation

`BatchFogCulling` captures each source model's immutable index range and transformed source-mesh bounds as the engine builds the batch. It preserves the engine's exact original UInt16 index stream, including vertex IDs and winding. On a fog change it removes the index ranges of models whose complete expanded footprint has no revealed cell. It updates only changed batches; it never renumbers vertices or modifies the shader's vertex/instance buffers, materials, roots or gameplay state.

The first live attempt rejected all 512 batches and performed no uploads. Revision 2 replaces patched cursor-range inference with the engine's per-vertex instance-ID stream (`cbsw`): every triangle is associated with its actual shader instance, then adjacent triangles for that instance form a range. Source-model bounds are captured by instance ID before the original append routine runs. Unknown or mixed-instance triangles stay visible. Rejections now have bounded reason logging instead of a single unexplained total.

The revision 2 live map exposed 29,821 source-read failures and 512 capture exceptions. Both capture paths used boxed generated `NativeArray<T>` accessors; their observed exception was NullReferenceException. Revision 3 reads the embedded array headers directly after resolving and checking the actual native field offsets against B0/C0/D0. It validates pointer/length/capacity before constructing a span, and bounds exception logging. This fix still needs live capture confirmation.

The summed-area table is rebuilt only when the fog bits or dimensions change. Unchanged frames perform a memory comparison and reuse the existing geometry. For changed fog, batches first compare model visibility states; unchanged batches do not copy or upload their index stream. Newly revealed models restore their original index ranges in their original order. Missing fog data restores original geometry.

The existing per-renderer culling remains for individual meshes. The new batch path is enabled by the same `Experimental.SectionFogCulling` switch, and needs a process restart and map load to capture the engine's batch generation.

After the hooks have been installed at startup, `SectionFogCulling` can be switched off/on live with `Diagnostics.WatchConfig=true`. Turning it off restores original indices and renderer flags. This supports a fixed-camera A/B/A without a process restart. `fog CPU:` reports pre-camera and periodic-refresh mean/max cost and mask rebuild counts, to distinguish culling overhead from whole-frame or GPU delays.

## Counters and interpretation

Unity's `Triangles Count` is sampled from `ProfilerRecorder.LastValue` for each measured frame. A reported mean around 9M is per-frame engine rendering work, not a cumulative number of discovered polygons. It can include repeated geometry in shadow and other camera passes. The active scene name attached to a sample does not make the counter camera-local.

The new `batch fog:` line reports original, removed and retained **mesh triangles** from the captured batches, hidden source models, preserved unbounded/deforming models, capture rejections, uploads, update time and errors. These counts describe stored mesh ranges, not all-frame GPU work: inactive sections and multiple passes prevent directly subtracting them from Unity's frame counter.

The earlier statement that hidden-renderer counts should rise as more tiles are revealed was incorrect. Revealing an area should restore its geometry. The previous log's hidden count rose while the number of initialized roots grew from 11 to 95; those two changing populations cannot establish culling efficiency.

## Regression investigation, same-view live toggle

At 3840x2160, on/off/on switching in the same stationary view produced about 74.2 / 74.4 / 74.2 ms mean frame time. Turning culling off restored geometry: draws increased from about 1,944 to 2,212, triangles from 9.79M to 10.12M. This rules out the enabled culler as the sole explanation of the persistent 75 ms frame in this view; it is not proof of equal GPU cost. The pre-camera fog check averaged 0.004–0.007 ms with no mask rebuilds. Periodic renderer refresh averaged 3.5–6.5 ms per refresh (about four refreshes per second), so that scan still deserves optimization.

The device was 99% busy. A contemporaneous residency sample showed the game's requested VRAM exceeding resident VRAM; that alone does not prove transfer stalls. BepInEx, current/previous Unity Player logs, EOS logs, frame CSVs, live-toggle timestamps and GPU samples are saved under `/data/dev/workspace/RunScratch/olden-fog-regression-20261025`. Existing Player.log configuration warnings also appeared in the prior session. No new device-loss or out-of-memory error was found. At that time, `resources.assets` matched the earlier BC7 patch hash (`f286cdd9ade61c0dc2fbdb95c0faaac73b6b1d016b0b000921885ef486e07b9f`). The 25 October forward patch now installs a different exact-hash asset set; see [the multi-format follow-up](TEXTURE_COMPRESSION_FOLLOWUP.md).

## Shader motion, revision 4

Revision 3 captured all 512 batches and 29,821 ranges without read errors, but retained every model because nonzero default wind-power parameters were treated as deformation. The actual installed `Hex/Environment` batched vertex program (28-byte vertices, 76-byte instances, raw UInt16 vertex-to-instance map) gates flag motion on `_Flags & 8` and tree motion on `_Flags & 16`. The source model color weight is Color32.r/255, bounded by one. Flag displacement is bounded by absolute power; tree displacement is bounded by `0.0256 * height^2` in X and `0.064 * height^2` in Z. Combined motion includes the flag's Y displacement before bounding the tree term. Revision 4 uses outward-rounded envelopes rather than excluding all models with default power values.

Evidence: extracted installed shader DXBC and `environment-vs/largest.asm` in the regression evidence directory, especially the structured-buffer loads and flag masks, plus native `bzc.pyz` Color32 normalization. Unit checks cover disabled flags with nonzero defaults, flag/tree bounds and sampled combined-motion extrema. This is source-level envelope verification; runtime rendering equivalence remains a separate check.

## Validation and remaining coverage

Release build: zero warnings/errors. Unit checks cover exact index order, winding/vertex ID preservation, full reveal restoration, same-count visibility swaps, immutable originals, invalid ranges/indices, map edges and unbounded deformation. An independent metadata check verifies all five builder hook signatures and the installed instance layout. Evidence code is under `/data/dev/workspace/RunScratch/olden-fog-batches-20261025/MetadataCheck`.

Live hook execution, removed triangle counts and visual correctness still require the new DLL to run and a map to load. The already-running game uses the previous DLL. No FPS or GPU saving is claimed from compilation or these unit checks.

This is conservative source-model culling. Models crossing a reveal boundary remain intact. Wind-deformed models remain intact until their shader displacement envelope is established, and footprints reaching beyond the fog grid remain visible. Capture memory is capped at 32M indices (192 MiB across original, scratch and upload buffers); excess batches remain original. Other procedural drawing paths are not automatically covered by this batch builder, and the fog surface must remain rendered. Therefore this build does not establish that every concealed primitive across the engine is removed.
