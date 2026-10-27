# Unit-window open audit (OldenPerf 0.4.1, build 26134266)

Scope: the unit-stat window (Undead Pet screenshot: painted panel, animated 3D preview, ability list). Sources: the authoritative ISIL dump (`RunScratch/olden-engine-audit-20261023/current-isil/IsilDump`) with callee names resolved through `symbols.txt`; generated C# signatures (`dump-cs/DiffableCs`); the live OldenPerf inventory copied to scratch. The subagent work was offline. Parent integration subsequently loaded all nine timing hooks in a visible game session with inventories off, but the session ended normally without a unit-open CSV row. Startup is verified; the opening bottleneck remains unmeasured.

## Status

| Item | Status |
| --- | --- |
| Static call-tree of the open path | Done (below) |
| Negative findings (loads, RT rebuild, battle code) | Done (below) |
| UI texture footprint from inventory | Measured on the city inventory, not on the window |
| Default-off UI-open profiler | **Implemented**: `Profile.UiOpen`, `Profile.UiOpenInventory` (hook path reads the clock only; file and scan work runs from `Tick`) |
| Preview `Update` static read | **Done** (below): unconditional `set_rotation` while visible; no safe cache proven |
| Event-based caching of window state | **Not implemented**: no proven complete invalidation |
| Vectorizing painted art | **Research only**: not equivalent, not implemented |

## Open path (ISIL, resolved)

- `BhUnitWindow.Show(Unit)`, `Show(BhUnitUI)`, `Show(string)` and `UpdateView(...)` share one fan-out: `BhUnitWindowStats.bjgz` (stat text, `String.Format`/`Concat`), `BhUnitWindowMain.Init` and `bjgg` (`BhGenericButton.Init`, `UiAnimatorPrime.Out`), `BhUnitWidnowBuffs.Clear` and `bjfb` (buff items), `BhUnitWindowPreview.Show`, `BhUnitWindowSkills.bjgw`/`bjgx` (ability items), and many `GameObject.SetActive` calls.
- `BhUnitWindowPreview.Show(UnitViewConfig)` dequeues a pooled preview (`BhUnitPreviews.itr`), binds the `RawImage` (`bge.lyn`), starts the rotation and camera control, and calls `bjgv` → `BhUnitPreviews.itp`. The pool is filled at `BhUnitPreviews.Start` through `ito`/`itt`, and `itu` is only a cache lookup.
- `BhUnitWindowPreview.Update` runs per frame while the preview is active (rotation, `Mathf.SmoothDamp`, `Screen.get_height`). This is steady-state cost, not open cost. `Hide` returns the preview to the pool and deactivates it.
- The window is persistent. Opening does not create it. It toggles `SetActive` and fills existing components.

## Negative findings

- No `Resources.Load`, `Resources.LoadAll`, or `AssetBundle.LoadFromFile` caller exists in the unit-window classes (reverse index over the full dump). Those loads appear only in `ResManager` helpers and battle code.
- `ResManager.brym` is a singleton accessor, not a load.
- `bge.lyo` (preview `RenderTexture` rebuild) releases and recreates the RT **only** when the RT is null or the stored width/height changed (ISIL 027–046). It is not a per-open allocation. Format per the call site: colorFormat 9, depth 32, no mips, not sRGB.
- `emk.bfqe/bis/fvk/jin` (Resources.Load + Instantiate + LineRenderer) belong to battle observers, not the window.
- `Canvas.ForceUpdateCanvases` is called only from `ScrollRect`, not from the window.

## Texture footprint (measured on the city inventory)

Source: `inventory_20261023-234403_007_CityUndead2.csv`, taken in the city scene. It is **not** an inventory of the unit window. The `mb` column is `Profiler.GetRuntimeMemorySizeLong`, which is object memory, not VRAM residency.

- All `Texture2D`: 132.8 MiB over 7,606 objects.
- UI-pattern subset (HUD, Window, PNG_, Frame, Panel, Button, Icon, cursor, Unit, Back, Bar, Stat, …): 55.5 MiB over 2,758 objects.
- Format split of that subset: uncompressed `R8G8B8A8_SRGB` 46.79 MiB (622 objects), `RGBA_DXT5` 4.84 MiB, `RGBA_BC7` 2.62 MiB, `RGBA_DXT1` ≈0.
- Largest uncompressed families: `HUD_RightPanel_Color` 9 × 455×2048 = 31.95 MiB; `HUD_ColoRPanel_Back` 9 × 1487×62 = 3.15 MiB.
- Sharing: the 9 `HUD_RightPanel_Color` objects are distinct instances. Whether their pixels are identical is **unknown**. No dedupe saving is claimed.
- Theoretical only: a 4:1 BC7-equivalent of the uncompressed UI set is 11.70 MiB, a 35.09 MiB upper bound. This is lossy and was **not** applied.
- Undead unit arrays: `undead_1024_texture_array` 21.33 MiB and `undead_512` 4.00 MiB, `undead_256` 0.33 MiB. These are probably 3D unit art, not window UI. Ownership is unverified.
- Render targets in that scene are map and world buffers. The preview RT's size is not in this inventory because the window was not open.

## Vectorization (research, not implemented)

The Unity Vector Graphics package 2.0 is documented as experimental and "not recommended to be used in production". Its SVG importer covers a subset of SVG 1.1 and excludes text, per-pixel masking, filter effects, and animation ([manual](https://docs.unity3d.com/Packages/com.unity.vectorgraphics@2.0/manual/index.html); see also the [Unity 6.4 vector-graphics UI page](https://docs.unity3d.com/Manual/ui-systems/work-with-vector-graphics.html)). Painted backpanels and the painted scene behind the preview are not expressible this way without visible change. Raster-to-vector conversion would have to pass a per-pixel comparison at window scale, and tessellated meshes and gradient textures would add memory. Verdict: not a significant RAM/VRAM lever for painted art. It might fit flat shapes after a pixel gate.

## Profiler implemented

- `Profile.UiOpen` (default `false`): inclusive CPU time of the outermost `Show` root (`show-unit`, `show-hud-unit`, `show-sid`) and its phases (`update-view`, `preview-show`, `main-bind`, `stats`, `buffs`, `skills`). A phase outside a root is not timed. A nested root is a phase of the outer open, not a second row. Writes `ui-open-<run>.csv`.
- Frames: the whole frame intervals after the open, at frames N+1 to N+3 where N is the open frame (`Time.frameCount`). Each sample is tagged `frame=ms`. A row's `frame_status` is `complete`, or the reason the window ended early: `frame-gap`, `scene-changed`, `unfocused`, `invalid-delta`, `superseded`. Incomplete rows are kept and marked.
- Hook path: reads the clock and appends to memory. No file, log or scan runs inside an open.
- `Profile.UiOpenInventory` (default `false`, needs `UiOpen`): one scan per outermost root open, run from `Tick` after that open's frame samples are done. Its `inventory_ms` is end to end: the scan, the texture rows, the file appends and the log line. The scan reads the window as it is at flush time, not at open time. Its `inventory_status` is `ok`, `inactive`, `window-gone` or `cap`. Writes `ui-open-textures-<run>.csv` (keyed by `open`).
- The inventory's frames are not in any open's sample window, but the scan still runs on a real frame, so it can cost the frame after it. For frame numbers, use a timing-only run (`UiOpen=true`, `UiOpenInventory=false`). Do not subtract `inventory_ms` from frame deltas: its cost is not a causal estimate of any frame.
- Bounds: 64 recorded opens per session, then the hooks are removed (`UnpatchSelf`, from `Tick`, outside any open). 16 inventories, 256 textures per inventory. Each cap logs once.
- Setup fails closed. Hooks count only after `Patch` succeeds. A setup or write error removes the hooks and disables the profiler. Output errors never escape `Tick`, `Begin` or `End`.
- Obfuscated sub-steps (`bjgg`, `bjgz`, `bjfb`, `bjgx`) are hooked only on the verified build, like `FrameProfile`. Types and methods were confirmed by name in `BepInEx/interop/Hex.dll`.
- Passive: no effect on window state, textures, canvases, or draws.
- CSV: RFC 4180 quoting for every free-text field (scene, root, phases, frames, names).
- Harness: `UiOpenStats.cs` (pure, compiled by `tests/ProfilerChecks.csproj`) covers root gating, nesting, re-entry, invalid durations, the row cap, the frame-window rules (open frame excluded, gap, scene, focus, bad delta, supersede), CSV quoting, and texture de-duplication. It does not prove the Unity hook path; that needs a live run.

## Preview update: static findings (research, not implemented)

Source: `decomp/Hex/Hex.Session.Battle/BhUnitWindowPreview.cs` (attribute call lists) and `dump-isil/.../BhUnitWindowPreview.txt` (the ISIL body of `Update`). Nothing was run.

- `Update` (ISIL 001–089) is gated by `cpac` (offset 0x69). When it is set, the body runs every frame. `cpab` (0x68) gates only the input read (`Input.GetMouseButton`, `cbn.qqt`, `MouseDeltaAxes.lza`). Then it calls `UnitPreviewRotation.lzd`, `UnitPreviewCameraControl.lyx`, `lze`, `lyy`, and always writes `Transform.set_rotation` from `UnitPreviewRotation.lzf`. Last, it calls a smoothing helper at `0x182A53E40` with the field at 0xA0 and the field at 0x94. These are native callbacks; their bodies are not in the dump.
- The write is unconditional. The ISIL shows no compare against the last value.
- This is animation state, not repeat work. Caching the quaternion or skipping `set_rotation` is safe only if (a) the rotation and camera state have converged, (b) no other code writes this transform, animator or camera between frames, and (c) Unity's `set_rotation` with an identical value has no observable effect (not verified). None of the three is proven, so no cache is proposed.
- `Show(UnitViewConfig)` makes six `il2cpp_codegen_object_new` allocations in its body, plus `ResManager.bryp`, `BhUnitPreview.uau`, `cpv.ubm`, `BhUnitPreviews.itr/itp`, `bge.lyo` and `bge.lyp`, and one `Debug.LogError` on a branch. Which allocations run on every open, and whether `bgl` (a new `GameObject, bool, string` per open) is per-open, is not known from the static dump. The live `ui-open` CSV with inventory is what settles it.
- `ResManager.bryp` is an accessor name in the dump. Like `brym`, it is not proven to be a load. The absence of a direct `Resources.Load` in the window classes does not rule out an indirect load in a callback. Preserve that as an unknown.
- `Hide()` returns the preview to the pool (`BhUnitPreviews.its`) and deactivates it. Nothing here changes that.

## Prioritized recommendations

1. **Live measurement first (parent).** Run a timing-only session: `Profile.UiOpen=true`, `Profile.UiOpenInventory=false`, restart, open the Undead Pet window five times from a settled city, then close. Read `ui-open-*.csv` and keep rows whose `frame_status` is `complete`. A second session with `UiOpenInventory=true` gives the texture and graphics counts; its frames are not free of the scan.
2. If the largest phase is `stats`, `skills`, or `buffs`: look at string building and per-open item creation before anything else. Each has measurable CPU and no art change.
3. If the frame delta dominates and inventory shows many graphics with few textures: canvas rebuild and layout is the suspect. Only event-based caching would be safe, and it needs proof.
4. The 3D preview costs every frame while open. Check GPU frame time with the preview on and off before touching it.
5. BC7 for UI art (35 MiB theoretical) and texture de-duplication are lossy or unproven. They need explicit approval and per-pixel evidence.
6. Vectorization: only as a pixel-gated experiment on flat elements.

## Runtime unknowns

- Real per-phase costs and which phase dominates.
- Whether `SetActive` triggers canvas rebuilds in this window.
- The preview RT's size and owner in the live inventory.
- Whether the Undead art arrays or the `HUD_RightPanel_Color` family are referenced by the window.
- Whether `Hex.dll` interop method signatures match on the live build (checked by name only).
