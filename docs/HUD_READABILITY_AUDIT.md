# HUD readable-texture audit

## Parent runtime validation harness

`Profile.ProbeHudAlpha8` defaults off. It observes HUD sprite assignments, queues at most four readable RGBA32/no-mip textures, and extracts their complete alpha bytes into a same-size Alpha8 texture. It does not call Apply, bind that texture for rendering, change the original, redirect a hit test, or alter readability. It compares 2,064 native `GetPixelBilinear` alpha results per texture (32 pairs per frame), including fractional texels, boundaries and out-of-range UVs. It records differing float bits, differing decisions at threshold 0.2, range, object-memory estimates and diagnostic CPU time in `hud-alpha-oracle.txt`. A uniform sample range is inconclusive. This is a prerequisite test, not a memory optimization or a complete hit-test equivalence proof.

The assignment hook removes itself after four textures or four minutes. No global object inventory is used. Sampling frames are excluded from general timing; unit-open rows overlapping other diagnostics are now explicitly marked `diagnostics-overlap`.

Unity 6000.0 documents [GetPixelBilinear as a CPU-memory read](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Texture2D.GetPixelBilinear.html), with texel coordinates x/width and y/height, and [LoadRawTextureData as a CPU-memory write](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Texture2D.LoadRawTextureData.html), requiring Apply for a GPU upload. This does not establish whether texture construction itself allocates GPU memory. GPU residency must be measured separately. The [current upstream Image source](https://github.com/Unity-Technologies/uGUI/blob/main/com.unity.ugui/Runtime/UGUI/UI/Core/Image.cs) is supporting context, not a pinned native implementation for this game. The harness leaves the installed threshold/readability predicate untouched.

Status: the harness builds, but its Unity sampling path has not yet been run. No readable flag or click behavior is changed.

Date: 2026-10-24. Scope: CPU-readable copies of `HUD_RightPanel_Color_*`, `HUD_ColoRPanel_Back_*`,
`HUD_RightPanel_UnderColor` and `HUD_ColoRPanel_Top`. Method: static analysis only. The game was not
launched, stopped, deployed, or driven, and no game file was written.

## Verdict

**No readable flag can be removed safely today. No patch is proposed.**

- 20 HUD textures are `m_IsReadable = true` (`HUD_ColoRPanel_Back_neutral` is already false).
  Their CPU-side copies total **36.08 MiB** (RGBA32, mip count 1). This is an upper bound on what is held
  in system memory, and only for objects that are loaded.
- **4.83 MiB is statically required.** Four textures back UI cutouts that set a 0.2 alpha hit-test
  threshold: `RightPanel_Color_3`, `ColoRPanel_Back_3`, `RightPanel_UnderColor` and `ColoRPanel_Top`.
  Making them non-readable disables the cutout hit test, and the sprite swap resets the threshold.
- **31.25 MiB is runtime-undetermined.** The 16 faction variants `RightPanel_Color_{0,1,2,4,5,6,7,8}` and
  `ColoRPanel_Back_{0,1,2,4,5,6,7,8}` can be assigned at runtime to the three cutout-target `pic` Images
  (`Init(colorId)`). Whether they ever are depends on `colorId` values that are not in the offline data.
- **0 MiB is provably flippable now.**
- **VRAM is unaffected.** `m_IsReadable` only controls the retained CPU copy. The GPU texture is identical.

The premise "the faction variants are recolored from a readable template" is wrong. Nothing writes
pixels to these textures (see below). The readable flag is load-bearing for a different reason: UI hit testing.

## Native findings (GameAssembly.dll, sha256 `eb57aeec…`, matches `resources.assets.manifest.json`)

ISIL reference: `RunScratch/olden-engine-audit-20261023/current-isil/IsilDump`. Step numbers are the ISIL
`NNN` column. `symbols.txt` is dated 2026-10-22, does not match the current addresses, and was not used.

1. **No CPU writer.** `BhHudSessionColor.Init` (`Hex/Hex/UI/Hud/BhHudSessionColor.txt`, steps 003–063)
   builds `"HUD_RightPanel_Color_" + colorId` (`String.Concat`, step 037), looks it up in the `con` registry
   (`Dictionary.TryGetValue`, step 055), and calls `Image.set_sprite` (step 063). `BhHeroHudColor.Init` does
   the same with `"HUD_ColoRPanel_Back_"`. Neither calls `SetPixel`, `SetPixels`, `Apply`, or
   `LoadRawTextureData`. A callmap over all 169,932 dumped methods shows no pixel write whose receiver is
   one of these textures.

2. **The live CPU reader is alpha hit testing.** `UnityEngine.UI.Image.IsRaycastLocationValid`
   (`UnityEngine.UI/UnityEngine/UI/Image.txt`, ~line 13826) returns true immediately when the threshold
   at `[rbx+264]` is ≤ 0 (steps 023–027). Otherwise it reads the sprite texture through
   `Texture2D.GetPixelBilinear`. The threshold is never serialized (`Image.cs` declares
   `m_AlphaHitTestMinimumThreshold` without `[SerializeField]`), so it is non-zero only if code sets it.

3. **Who sets it.** `BhUiCutoutTreshhold.Start` (`Hex/Hex/UI/BhUiCutoutTreshhold.txt`, steps 030–051)
   loads the float `0x18469B930` into `xmm1` and calls `Image.set_alphaHitTestMinimumThreshold`. The
   installed binary holds `0.2f` at that address. The only other caller is `OnScreenStick`, and no
   `OnScreenStick` MonoBehaviour exists in the level or shared assets.

4. **The setter is gated on the texture.** `set_alphaHitTestMinimumThreshold` stores the value (step 060)
   only if the sprite texture passes a format and crunch check and then a virtual call on the texture
   (steps 049–059). That virtual is most likely `Texture.isReadable`. It is not named in the ISIL, so this
   is inference. The `else` path (step 067) takes an error branch. `BhUiCutoutTreshhold.Start` runs the
   same predicate before the store (steps 039–045). A non-readable sprite therefore cannot have its
   threshold set.

5. **Sprite swaps reset it.** `Image.set_sprite` calls `<set_sprite>g__ResetAlphaHitThresholdIfNeeded`
   (step 090), which resets the threshold to 0 when the new sprite does not support hit testing. So
   making a cutout's texture non-readable silently disables that cutout's hit test, and the next sprite
   assignment does too.

6. **Other CPU readers ruled out** (full list from the callmap):
   - `SoMinimapConfig.bmwe` calls `Texture2D.GetPixels` on its own serialized fields. No MonoBehaviour in the
     data holds a PPtr to any HUD texture.
   - `Minimap.bmgq` calls `EncodeToPNG` on a RenderTexture readback, not on a HUD texture.
   - `IconData..ctor(Sprite)` calls `EncodeToPNG` on `Sprite.texture`, but no allocation site uses it. All
     ten `typeof(Hex.Storage.IconData)` allocations call the byte-array constructor `0x180CB7BF0`.
   - `ey.hqp` and `fa_NestedType_ez.hio` read Steam avatar images, and `BhBugReportScreen` reads a RenderTexture.
   - `InputSystem CreateCircleSprite` reads its own texture.

## Serialized findings (installed `HeroesOldenEra_Data`, read-only)

Raw record decoding was checked against all 77,110 serialized `UnityEngine.UI.Image` records. Each one has
the expected length, and the final float (`pixelsPerUnitMultiplier`) falls in the expected range.

| Texture | Size | Readable | Referrers (serialized Images) | Cutout target | Classification |
|---|---|---|---|---|---|
| `HUD_RightPanel_Color_3` | 455×2048 | yes | `level12/5970`, `level53/32245` (session `pic`) | yes | static |
| `HUD_ColoRPanel_Back_3` | 1487×62 | yes | `level12/7524` (hero `pic`, cutout); `level11` ×6, `level51`, `level52`, `level55` ×2 | yes (7524) | static |
| `HUD_RightPanel_UnderColor` (sharedassets12) | 354×422 | yes | `level12/6222`, `level53/36658` | yes | static |
| `HUD_ColoRPanel_Top` (sharedassets12) | 1487×62 | yes | `level12/7175` | yes | static |
| `HUD_RightPanel_Color_{0,1,2,4,5,6,7,8}` | 455×2048 | yes | none serialized; runtime `pic` target | via session cutout `pic`s | runtime-undetermined |
| `HUD_ColoRPanel_Back_{0,1,2,4,5,6,7,8}` | 1487×62 | yes | none serialized; runtime `pic` target | via hero cutout `pic` 7524 | runtime-undetermined |
| `HUD_ColoRPanel_Back_7` | 1487×62 | yes | `level53/35965` (hero `pic`, not cutout) | no | runtime-undetermined |
| `HUD_ColoRPanel_Back_neutral` | 1487×62 | no | none | — | already non-readable |

The 14 HUD-sprite Images are exactly the 12 hero `pic` and 2 session `pic` Images. Three of those 14 are
cutout targets, and three more HUD Images are cutout targets through `UnderColor` and `Top`. Among the 931
`BhUiCutoutTreshhold` instances, 930 distinct Images are targets.

## Bounds

| Quantity | Value |
|---|---|
| Readable HUD textures | 20 |
| CPU-side readable copies (upper bound, if loaded) | 36.08 MiB |
| Statically required (cutout threshold 0.2) | 4.83 MiB, 4 textures |
| Runtime-undetermined (any variant on a cutout `pic`) | 31.25 MiB, 16 textures |
| Provably flippable now | 0 MiB |
| VRAM effect of any readable-flag change | none (GPU copy is identical) |

The parent's 185.28 MiB figure is a listed-payload total over 420 readable objects, not resident memory. This
audit does not change it.

## Remaining proof, before any flip

1. **Runtime `colorId` set for the three cutout `pic` Images** (`level12/5970`, `level12/7524`,
   `level53/32245`). This decides the 31.25 MiB. A bounded one-shot diagnostic would log the sprite name at
   each `set_sprite` on those three Image instances. It is not enabled, and it must not run during a live
   session without the user's approval.
2. **Name the predicate** (vtable slot `[rdx+488]` on `Texture`). It is consistent with `Texture.isReadable`
   but not confirmed.
3. **Native Unity readers** that are not visible in the managed ISIL. Assume none exist until a native trace
   shows otherwise.
4. **GPU pixel proof and in-game hit-test behavior.** Neither was run. Both need the user's go-ahead for a
   live launch.

Even if a runtime check passes for the 16 variant textures, the 4 static textures stay readable. The
realistic saving is therefore at most 31.25 MiB, and only after items 1–4.

## Reproduce

```sh
/data/dev/workspace/homm-olden-perf/.venv/bin/python tools/hud_readability_audit.py \
  "/data/dev/workspace/SteamLibrary/steamapps/common/Heroes of Might and Magic Olden Era/HeroesOldenEra_Data" \
  --json hud_readability_report.json
.venv/bin/python -m unittest tests/test_hud_readability_audit.py
```

The tool is read-only. It fails closed: threshold constants are read only when `GameAssembly.dll` matches the
pinned SHA-256.

## Alpha-only surrogate: status (2026-10-24)

**Built:** `OldenPerf/HudAlphaMask.cs`, a pure RGBA32 to alpha-plane extraction. It fails closed unless the byte count
is exactly `width*height*4`, keeps row order, and reports `BytesSaved` (3 of every 4 bytes). Unit checks are in
`tests/Program.cs`. Nothing is wired into the plugin: there is no config key, no hook and no readable-flag change, so
runtime behaviour is unchanged.

**Not built, and why:**

1. **Hit-test redirect.** A surrogate needs a managed detour on `Image.IsRaycastLocationValid` that samples the
   Alpha8 copy instead of the RGBA original. The threshold setter also gates on the readability predicate
   (`[rdx+488]`, still unnamed). A scoped override for that predicate would be a guess, not a proof.
2. **Sampling equivalence.** `Texture2D.GetPixelBilinear` on an Alpha8 texture must return the same alpha bits as on
   the RGBA32 original, for pixel centres, edges, UV outside [0,1], the wrap mode and 9-slice inputs. Unity's native
   float order is not documented, so this needs a live A/B oracle. None was run.
3. **GPU cost.** A new `Texture2D` probably allocates a GPU resource on construction, even without `Apply`. An Alpha8
   surrogate would add VRAM of 1 byte per texel: 1.21 MiB for the four static textures and 9.02 MiB for all 20. That is
   not measured. It does not make the net gain free.

**Ceiling, logical CPU copy only:** 36.08 MiB of RGBA32 becomes 9.02 MiB of alpha, a saving of 27.06 MiB, or 3.62 MiB
for the four static textures and 23.44 MiB for the sixteen runtime-undetermined variants. This is an upper bound on
retained system copies for loaded objects. It is not a residency or VRAM figure.

**Live checks still needed (parent-owned, not run here):** A/B `GetPixelBilinear` alpha bits for each sample class
above on the four static cutouts; the sprite-name log at `set_sprite` for the three cutout Images; VRAM before and
after with the surrogate; and hit tests on each cutout. No flip of any readable flag is proposed until those pass.
