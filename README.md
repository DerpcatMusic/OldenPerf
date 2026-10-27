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

### Memory and assets

- **Depth copy without stencil** — the sampled depth copy becomes D32 without the 8-bit stencil it never uses; the
  main depth/stencil buffer is untouched. Enabled only after a pixel-by-pixel equality check. — 63.3 → 31.6 MiB at 4K.
- **Texture streaming that follows the view** — the streaming budget tracks Unity's measure of the mips the view
  needs, plus headroom; it rises at once and falls slowly. — Visible detail is never dropped; unused high mips give
  memory back. (A fixed budget had caused blurry city textures.)
- **Texture compression** — asset patch: 875 textures recompressed to BC1 (opaque) or BC7 (alpha). Every mip level
  must pass a quality gate (BC1 ≥ 32 dB, BC7 ≥ 30 dB PSNR); data textures, readable textures and anything that
  wouldn't get smaller are skipped. — 84 MiB less texture data, mostly uncompressed UI art.

### Safety

- **Build guard** — native hooks check the game binary's SHA-256 and turn themselves off on an unknown build.
- **Fail open** — anything the mod can't prove hidden stays drawn.

## Tried and dropped

- **Lower render scale / smaller shadow atlas** — fewer pixels, not a faster engine.
- **Packed HDR TAA history** — lost precision.
- **On-demand icon textures** — unsafe reloads.
- **Frame queue of 1** (lower input lag) — left the GPU up to 60% idle and caused 50 ms spikes. The game's 2 is
  better.
- **Bloom fast mode / no NaN pass** — gains too small to measure reliably against the risk.

## For the developers

Native fixes that would make most of this mod unnecessary:
1. Cull by fog of war (including allies), and give merged prop meshes per-prop or per-cell bounds.
2. Ship AudioClips as Compressed In Memory (or Streaming for music).
3. Use `SpriteMeshType.FullRect` in `IconData`.
4. Use the half-size SSAO path at high resolutions.
5. Make the sampled depth copy D32 without stencil.
6. Compress the remaining uncompressed UI art.
7. Warm up TextMeshPro and particles during loading; the first Alt press still stalls ~0.7 s.

## Install

1. Install [BepInEx 6 IL2CPP](https://docs.bepinex.dev/) and run the game once.
2. Set `GameDir` in `OldenPerf/OldenPerf.csproj`, then with the game closed run
   `dotnet build OldenPerf/OldenPerf.csproj -c Release -p:DeployToGame=true`.
3. Optional asset patches (game closed, back up first, re-apply after Steam updates):
   - audio: `python -I tools/audio/patch_loadtype.py <resources.assets> report.json` (needs UnityPy)
   - textures: `tools/asset_manifest.py install MANIFEST GAME_DATA --backup-dir DIR`

Config: `BepInEx/config/derpcat.oldenperf.cfg`. Logs: `BepInEx/LogOutput.log`, `BepInEx/oldenperf/`.
`docs/` holds the raw measurement notes.

## License

MIT
