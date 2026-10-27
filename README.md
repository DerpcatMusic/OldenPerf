# OldenPerf

A free, open-source performance mod for **Heroes of Might and Magic: Olden Era** (BepInEx 6, IL2CPP).

The goal: make the game run faster at native 4K without visible quality loss. No upscaling, no lower render
resolution, no lower shadow or effect settings. Every change below is either lossless or uses a path the game
already ships with.

Tested on Steam build 26134266 (Unity 6000.0.66f1), an RX 6600 8 GB, Linux with Proton/DXVK, at 3840×2160.

> Unofficial fan project, not affiliated with the developers or publisher. No game files are included.

## What it does, and why

### Don't draw what's under the fog of war
The game draws the whole map every frame, including land nobody has explored. The mod reads the fog grid and skips
anything that is fully hidden:
- **Whole map sections** (16×16 tiles) with nothing revealed in or next to them stay switched off. The mod uses the
  game's own section culler for this, so their scripts, shadows and lights don't run either.
- **Single objects** on the fog edge are hidden when no tile they cover is revealed.
- **Combined prop meshes** (trees, rocks, decor): the game merges props into big meshes whose bounds cover the whole
  section, so they are never culled. The mod strips hidden props out of those meshes, so their triangles stop
  reaching the GPU and the shadow passes.
- **Allies count.** Anything your allies have revealed is drawn too.

On the test map this removes about **6.9 of 10.2 million** prop triangles and hides about 14,000 renderers. Work
only happens when the fog changes.

### Ambient occlusion at half resolution
At "High", SSAO (the soft contact shadows) renders at full 4K. The game's own "Low" setting renders the same
effect at half size and corrects the blur for it. AO is soft and blurred anyway, so at 4K the two look the same.
The mod uses the half-size path only while SSAO renders; your saved setting stays "High". This saves roughly
1 ms per frame. Switch: `Ssao.HalfResolution`.

### Faster loading
- **Audio:** 3,523 sound clips were set to "decompress on load", so the whole sound library was decoded into memory
  during loading (a 5-second frame). A one-time patch to `resources.assets` switches them to "compressed in
  memory": same audio, decoded when played. That frame now takes about 0.5 s.
- **Startup checks are cached:** file hashes and one-time GPU safety tests are remembered per game build and driver,
  so they don't run again on every launch.

Overall, the loading screen went from **13–23 s to about 7–10 s**.

### Fewer hitches
- **First Alt press:** the game builds outline meshes for every stored icon the first time you press Alt. Icons are
  now created as plain rectangles. Transparent pixels stay transparent, so they look identical. This took about
  0.3 s off the stall (990 ms to about 700 ms). The rest is still there (see below).
- **Frame queue left at 2:** we tested queuing only 1 frame for lower input lag. It left the GPU up to 60% idle
  and caused 50 ms spikes, so the mod keeps the game's default of 2 (steady ~20.5 ms per frame on the map).

### Less memory
- **Depth copy without stencil:** the game copies the 4K depth buffer with an 8-bit stencil it never reads. The
  mod drops that stencil, keeping full 32-bit depth. This saves 31.6 MiB at 4K. On first run the mod renders both
  versions and checks every pixel is identical; if any differs, it keeps the original.
- **Texture streaming that follows what's on screen:** the budget follows Unity's own measure of the mips the view
  needs, plus headroom. Needed detail is never dropped; textures nothing is looking at give back memory.
- **Texture compression (optional asset patch):** some textures shipped uncompressed. They are recompressed to
  BC1 (opaque) or BC7 (with alpha) after a per-mip quality check, saving about 84 MiB.

### Smaller things
- 15 fps cap while the game is in the background (`Frame.BackgroundFps`).
- Everything that patches native code checks the game's binary hash first and turns itself off on an unknown
  build. Anything it can't prove safe stays drawn (it fails open).

## Notes for the developers

These would be easy native wins:
1. **Cull by fog of war**, including allied vision, and give merged prop meshes real per-section bounds.
2. **Set AudioClips to Compressed In Memory** (or Streaming for music) instead of Decompress On Load.
3. **`IconData` sprites:** use `SpriteMeshType.FullRect`. Tight meshes cost time on the first Alt press.
4. **Use half-resolution SSAO at high output resolutions.** The "Low" path already looks the same at 4K.
5. **Make the sampled depth copy D32 without stencil.** This is bit-identical and halves its size.
6. **First Alt press still stalls ~0.7 s.** It's mostly TextMeshPro text generation and particle setup on first
   use. Warming these up during loading would remove it.

The `docs/` folder has the detailed measurements and audits from development. Some of it describes experiments
that were later removed.

## Install

1. Install [BepInEx 6 (IL2CPP)](https://docs.bepinex.dev/) into the game folder and start the game once.
2. Build: `dotnet build OldenPerf/OldenPerf.csproj -c Release`. Set `GameDir` in the project file to your game
   folder; add `-p:DeployToGame=true` to copy the DLL into `BepInEx/plugins`. Close the game first.
3. Optional asset patches (game closed, back up the files first):
   - audio: `python -I tools/audio/patch_loadtype.py <resources.assets> report.json` (needs `UnityPy`)
   - textures: `tools/asset_manifest.py install MANIFEST GAME_DATA --backup-dir DIR`

   Steam updates replace these files. Re-apply after an update; never restore old backups over a new build.

Settings live in `BepInEx/config/derpcat.oldenperf.cfg`. Logs go to `BepInEx/LogOutput.log` and `BepInEx/oldenperf/`.

## License

MIT. See [LICENSE](LICENSE).
