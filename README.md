# OldenPerf

Performance mod for **Heroes of Might and Magic: Olden Era** (BepInEx 6, IL2CPP). Native 4K, no upscaling, no
lowered settings.

Tested on Steam build 26134266 (Unity 6000.0.66f1), RX 6600 8 GB, Linux + Proton/DXVK, 3840×2160.
Unofficial fan project. No game files included.

## Optimizations

Each entry: **what** — how — why.

### Rendering

- **Fogged map sections are switched off** — sections (16×16 tiles) with nothing revealed in or around them are
  fed an empty bounds into the game's own section culler, so the game keeps them inactive. — Their meshes, shadows,
  lights and scripts all stop. 43 of 100 sections on the test map.
- **Fogged objects at the fog edge are hidden** — `forceRenderingOff` on renderers whose tiles (plus a 1-tile margin)
  are all unrevealed. — Sections next to explored land would otherwise draw their whole contents.
- **Fogged props are cut out of merged meshes** — the game merges props into one mesh per section with
  whole-section bounds, so nothing in it is ever culled. The mod records each prop's real bounds (including tree
  and flag sway) and removes hidden props' indices from the mesh when fog changes. — 6.9 of 10.2 million prop
  triangles stop reaching the GPU and the shadow passes.
- **Allied vision counts** — the fog check uses the union of every alliance member's fog grid. — Without it, allied
  towns and roads would vanish.
- **Fog work only on change** — each frame is a single 25,600-byte compare; culling recomputes only when it differs.
  — No per-frame cost.
- **SSAO at half resolution** — during the SSAO pass only, the "High" setting reads as "Low", which is the game's
  own half-size AO path with blur compensation. Your saved setting is untouched. — Full-4K AO cost about 2 ms per
  frame; AO is soft and blurred, so half size looks the same.
- **Background frame cap** — 15 fps when the window isn't focused. — Saves power and heat while alt-tabbed.

### Loading and hitches

- **Audio decoded on demand** — asset patch: 3,523 AudioClips changed from *Decompress On Load* to *Compressed In
  Memory* (4 bytes per clip). — The whole sound library was being decoded while loading: a 5 s frame, now 0.5 s.
- **File hashes cached** — the safety checks hash `GameAssembly.dll` and `resources.assets`; results are cached by
  path, size and modified time. — Hashing cost 3.5 s on every launch; now it's one file stat.
- **GPU safety checks remembered** — a passed full-4K equality check is stored per game build, driver and
  resolution. — The check stalled about 1 s after every map load; now it runs once.
- **Cheaper fog setup** — mesh bounds are read once per source mesh and section inspection is spread out after
  load. — Removed most of the 1 s stalls right after a map loads (7 down to 1, and that last one was the check above).
- **Alt icons without outline tracing** — stored icons are created with `SpriteMeshType.FullRect` instead of Tight.
  — Tight traces an outline mesh for every icon on the first Alt press. Pixels are identical. Stall: 990 → ~700 ms.

Overall loading screen: **13–23 s → 7–10 s**.

### VRAM (an 8 GB card shared with the desktop)

On Linux the desktop holds a lot of video memory (here 4.5 of 8 GB: browser 1.7, compositor 1.2). Whatever the game
can't fit goes to system memory (GTT) and stays there for the session, and every frame then reads it over PCIe.
Frame time follows that spill: the same map ran at 33 ms with 364 MiB in GTT and 72 ms with 659 MiB.

- **Idle UI art is unloaded** — every sprite page (on-disk, non-readable, no mips) that no visible `Graphic` or
  `SpriteRenderer` has used for 30 s is unloaded with `Resources.UnloadAsset`; Unity reloads it through the
  sprite's reference when a window needs it, and a page reloaded once stays resident. — The map scene loads
  ~730 MiB of UI art for closed windows; ~690 MiB of it goes. Map frame 55 → 32 ms in the run that measured it.
- **Load peak cut** — entering a loading screen unloads every page it doesn't show before the next scene
  allocates, and mip streaming starts at its floor budget and rises within half a second. — A generous start
  budget streamed full mip chains during the load and dropped them seconds later; that peak is what spilled.
- **Other-language fonts at half size** — static Chinese/Japanese/Korean TextMeshPro atlases that no text uses are
  swapped for half-size copies (signed distance fields scale cleanly); the original reloads on the next loading
  screen, or at once when a text switches to that font. — 144 MiB in an English game.
- **No HDR on cameras without post-processing** — the UI, spellbook and menu cameras rendered into their own 4K
  RGBA16F buffer that was only clamped when copied to the screen. — One 63 MiB buffer and one 4K copy less per
  camera per frame. Pixels on screen are the same.

- **Depth copy without stencil** — the sampled depth copy becomes D32 without the 8-bit stencil it never uses; the
  main depth/stencil buffer is untouched. Enabled only after a pixel-by-pixel equality check. — 63.3 → 31.6 MiB at 4K.
- **Texture streaming that follows the view** — the streaming budget tracks Unity's measure of the mips the view
  needs, plus headroom; it rises at once and falls slowly. — Visible detail is never dropped; unused high mips give
  memory back. (A fixed budget had caused blurry city textures.)
- **Texture compression** — asset patch: 1,084 textures recompressed to BC1 (opaque) or BC7 (alpha). Every mip
  level must pass a quality gate (BC1 ≥ 32 dB, BC7 ≥ 30 dB PSNR); data textures, readable textures and anything that
  wouldn't get smaller are skipped. — Mostly uncompressed UI art (see table).

### Safety

- **Build guard** — native hooks check the game binary's SHA-256 and turn themselves off on an unknown build.
- **Fail open** — anything the mod can't prove hidden stays drawn.

## Before and after

Measured on the test machine (RX 6600 8 GB, 4K). Asset numbers are exact; frame numbers depend on how much VRAM
the desktop leaves free.

| | Vanilla | OldenPerf |
|---|---|---|
| Texture data on disk (all mips) | 4,246 MiB | 3,793 MiB |
| Always-resident textures (no mips) | 2,106 MiB | 1,687 MiB, of which ~690 MiB UI art unloaded while idle |
| RGBA32 → BC7 (842 textures) | 253 MiB | 64 MiB |
| RGB24 → BC1 (174 textures) | 191 MiB | 24 MiB |
| RGBA32 → BC1 (17), RGB24 → BC7 (41) | 113 MiB | 18 MiB |
| Audio in RAM (3,523 clips) | 736 MiB PCM | 124 MiB compressed |
| Other-language font atlases in VRAM | 340 MiB | 196 MiB |
| Longest loading frame (audio decode) | 5 s | 0.5 s |
| Loading screen | 13–23 s | 7–10 s |
| Town entry after the UI unload, worst frame | — | 1,555 → 246 ms (pin after first reload) |
| Game memory spilled to GTT, map | 659 MiB | 364 MiB |

## Tried and dropped

- **Lower render scale / smaller shadow atlas** — fewer pixels, not a faster engine.
- **Packed HDR TAA history** — lost precision.
- **On-demand icon textures** — unsafe reloads.
- **Frame queue of 1** (lower input lag) — left the GPU up to 60% idle and caused 50 ms spikes. The game's 2 is
  better.
- **Bloom fast mode / no NaN pass** — gains too small to measure reliably against the risk. The NaN pass is a
  full 4K copy, but the game turned it on deliberately and one frame can't prove NaNs never happen.
- **Lighter depth of field** — the 4K bokeh DoF chain is the most expensive post effect, but it is part of the look.

## For the developers

Native fixes that would make most of this mod unnecessary:
1. Cull by fog of war (including allies), and give merged prop meshes per-prop or per-cell bounds.
2. Ship AudioClips as Compressed In Memory (or Streaming for music).
3. Use `SpriteMeshType.FullRect` in `IconData`.
4. Use the half-size SSAO path at high resolutions.
5. Make the sampled depth copy D32 without stencil.
6. Compress the remaining uncompressed UI art, and don't hard-reference every icon atlas from the map scene: load
   window art with the window (Addressables / `Resources.Load`) and release it on close.
7. Load only the current language's fonts.
8. Turn HDR off on cameras that have no post-processing (UI, spellbook, menu).
9. Keep the load's VRAM peak low: release the previous scene's assets before loading the next, and don't start mip
   streaming with an unlimited budget.
10. Warm up TextMeshPro and particles during loading; the first Alt press still stalls ~0.7 s, and the first map
    frame spends ~2 s in `ParticleSystemRenderer::UpdateCachedMesh`.

## Install

1. Install [BepInEx 6 IL2CPP](https://docs.bepinex.dev/) and run the game once.
2. Set `GameDir` in `OldenPerf/OldenPerf.csproj`, then with the game closed run
   `dotnet build OldenPerf/OldenPerf.csproj -c Release -p:DeployToGame=true`.
3. Optional asset patches (game closed, back up first, re-apply after Steam updates):
   - audio: `python -I tools/audio/patch_loadtype.py <resources.assets> report.json` (needs UnityPy)
   - textures: `tools/asset_manifest.py install MANIFEST GAME_DATA --backup-dir DIR`

Config: `BepInEx/config/derpcat.oldenperf.cfg`. Log: `BepInEx/LogOutput.log`. Cached safety-check results:
`BepInEx/oldenperf/`.

## License

MIT
