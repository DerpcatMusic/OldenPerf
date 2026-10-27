# Unit preview: measurements and experimental model-pool return fix

Scope: the first live unit-open capture (`ui-open-20261024-005734.csv`, one open, `show-hud-unit`) and the static ISIL of the preview path. Source: `RunScratch/olden-engine-audit-20261023/current-isil/IsilDump` (pinned GameAssembly eb57aeec..., the `BuildIdentity` value). No game was launched, driven or deployed by this change. Build-only verification.

## Capture (one cold open, not a sample)

| phase | ms |
| --- | --- |
| total `show-hud-unit` | 60.72 |
| `stats` | 1.84 |
| `main-bind` | 0.35 |
| `preview-show` | 29.59 |
| `skills` (+ second `skills` entry) | 5.30 + 0.76 |

Frames N+1..N+3: 193.12, 65.46, 32.12 ms (`frame_status=complete`). One open, cold JIT/interop and profiler hooks included. Do not read these as steady-state.

## Native chain (ISIL, resolved by name where the dump names the callee)

1. `BhUnitWindow.Show(UnitViewConfig)` → `BhUnitWindowPreview.Show` (`Battle/BhUnitWindowPreview.txt` ISIL 682–1141).
2. Preview `Show`, per open:
   - `UnitPreviewRotation.lza`, `UnitPreviewCameraControl.lyt`, `ResManager.brym` (accessor), `BhUnitPreview.uar`.
   - `bjgv(string, Transform)` (ISIL 1332–): `BhUnitPreviews.itp` (pool get), `GameObject.SetActive`, `cpv.ubj`, `Transform.set_localPosition/localScale/rotation`, `UnitPreviewRotation.lzd`.
   - `new bgl(GameObject, bool, string)` (per open, object allocation) then `bge.lym(bgl, int)` (`bge.txt` ISIL 2138–2299), which calls `bgl.mbo` and `bgl.mbr`.
3. `BhUnitPreviews.itp` (`Hex/BhUnitPreviews.txt` ISIL 715–1052): `Dictionary.TryGetValue` (cache), `Queue.Dequeue` (pool). On a miss it calls `BhUnitPreviews.itt(UnitViewConfig)` (ISIL 1379–1890), the cold creation path. `itt` sets materials (`Material.SetTexture`), transforms and `SetActive`. It is the only per-unit creation path found.
4. RT: `bge.lyo()` (ISIL 2300–2557): the RT is recreated only when the stored RT is null, or when `RenderTexture.width` / `height` differs from the stored values (`[rsi+88]`, `[rsi+92]`). The recreate path is `RenderTexture.Release`, `RenderTextureDescriptor` (colorFormat, depth bits, mips off, sRGB), `new RenderTexture`, `set_filterMode`, `Camera.set_targetTexture`. A same-size open takes only the compare and returns.

## RT sizing (partly unresolved)

- `bge.lyp(RectTransform, Canvas, CanvasScaler)` (ISIL 703–850) returns a `Vector2` from `RectTransform.sizeDelta` and `Canvas.get_pixelRect`, multiplied by a float at offset `+0xC` of a struct and divided by a float at offset `+0x30` of the second argument. The field identities are **not** proven statically.
- Observed (audit log line, the `preview target` event from a live run): `rt=3099x3322 R16G16B16A16_SFloat depth=32 viewport=(0,0,1,1) screen=3840x2160`. The RT is not screen-sized. It is 10.295 Mpx, above 4K (8.294 Mpx).
- Formats: colorFormat 9 (R16G16B16A16_SFloat). Depth is 32 bits, not proven D32S8. Colour alone is 78.54 MiB. Depth memory is not claimed.
- **Not decided:** the exact mapping from canvas pixel rect and `sizeDelta` to the RT size, and the `RawImage.uvRect` display mapping. The new `UnitPreviewTargetAudit` logs these at runtime. It does not infer them.

## Update: repeat open creates a new model (implemented, default off)

Supersedes the verdict below for repeat opens. Evidence: `unit-reopen-4k.csv` (4K, same unit, Escape between opens). Open 2 has `preview-pool-create` 312.82 ms and a new camera and RT id. Open 1 had no create.

The return path is missing. `BhUnitWindowPreview.Hide` (`Battle/BhUnitWindowPreview.txt` ISIL 1142–1331) deactivates the model's children, returns the frame with `BhUnitPreviews.its`, and clears the model field (`cpt` at +0x48) and the frame field (+0x60). It never calls `BhUnitPreviews.itq`. The only `itq` callers are `BhPortalSummoningFull` and `BhHireUnitItemView`.

The pool is keyed by `cpt.Sid` (+0x38), and `itq` enqueues by that key into `Dictionary<string, Queue<cpt>>` (manager +0x30). A model that is never returned makes `itp` miss, so `itt` instantiates a new `cpt`. **Ownership correction from parent review:** the camera/RT renderer belongs to the separately pooled `BhUnitPreview` frame (`uao` returns its `bge` at +0x20), not to the `cpt` model. The new camera/RT IDs in the same run therefore do not establish that model creation caused RT recreation. This patch addresses the measured model creation cost; RT reuse must be checked separately.

Implementation: `OldenPerf/UnitPreviewPool.cs`, gated by `Experimental.ReturnUnitPreviewToPool` (default false). The Hide prefix captures the model pointer when the guard byte (+0x69) is set. The finalizer returns it through the game's own `BhUnitPreviews.itq`, only if Hide completed without an exception and the field is now null. Counters log from `Tick`: `gets`, `creates`, `hides`, `returned`, `hideNoop`, `noModel`, `notCleared`, `exceptions`, `noManager`, `destroyed`. `gets` and `creates` count every `BhUnitPreviews` user, not only the unit window.

Not covered: `BhUnitWindowPreview.Show` overwrites the model field without a `Hide` first. A second `Show` without `Hide` still orphans the first model, as before. The change does not touch that path.

Retention: returned models keep their existing model resources alive in the native keyed pool, as the hire and portal paths already do. The separately pooled frame owns the camera/RT. This change does not establish a VRAM reduction or bound all retained objects. The observed RT is `3099x3322 RGBA16F` (78.54 MiB colour), with live `D32_SFloat_S8_UInt`; its physical depth allocation is unmeasured.

Parent integration: class initialization is forced before checking native field offsets; active models are refused, exceptions disable future returns, and failed native returns are not retried. Release build and existing profiler/resource checks pass. **Live fix test remains pending:** the desktop session ended and loginctl reports the greeter; no visible game session is available. `Experimental.ReturnUnitPreviewToPool` remains false until that test.

## Verdict on the 29.59 ms preview phase (superseded for repeat opens)

- The RT is reused across opens when its size matches. Repeat-open RT allocation is **not** supported by the static chain.
- Per-open work is the pool get, a `SetActive`/transform bind, a `bgl` allocation and `bge.lym`. None of these is shown to be expensive on its own.
- The likely dominant cost of this cold open is the pool-miss path `itt` (model/material creation), not a repeated cost. One sample cannot separate that from JIT. Do not claim it.
- No safe fix is proposed. Caching the RT or skipping `set_rotation` would change lifetime or animation state, and neither is proven safe. The user's constraint (no downsizing, no precision cut, no effect removal) also rules out shrinking the RT.

## Runtime plan (parent runs it)

1. Set `UiOpen=true`, `UiOpenInventory=false`, `UnitPreviewTargetEvents=true`. Restart. Open the same unit window five times from a settled scene. Close each time.
2. `LogOutput.log`: count `preview-target camera` lines. One line total means the RT is reused. A line per open means recreation, and the rows of `rt=` show whether the size changed.
3. `ui-open-*.csv`: compare the `preview-show` value on open 1 with opens 2–5, using `complete` rows only. A large drop after open 1 means the cold `itt` path dominates.
4. If open 2+ still spends time in `preview-show`, next add a `BhUnitPreviews.itt` phase to `UiOpenProfile.Targets` (type `Hex.BhUnitPreviews`, method `itt`, verified on the build) to split pool miss from pool hit. Not done here: it touches an existing profiler file.
5. Only if step 2 shows per-open RT recreation with an identical size is an RT-retention candidate worth designing. Its lifetime must cover `Hide`, `OnDisable` and window close (`ReleaseTex` / pool clear). It must not change pixels, format or the animated preview.

## Changes in this branch

- `OldenPerf/UnitPreviewTargetAudit.cs`: default off, setter-only hooks on `Camera.targetTexture` (camera `LOOK UNIT CAMERA`) and `RawImage.texture` (RT bound to that camera). Capped at 64 events, not de-duplicated, so per-open recreates stay visible. Fails closed.
- `OldenPerf/Plugin.cs`: `Profile.UnitPreviewTargetEvents` (default false), read at startup.

## Not verified

- Hook path in IL2CPP: only the build is verified. No game run.
- Pure tests: none. The new file has no pure logic worth a test; the hook path needs a live run.
