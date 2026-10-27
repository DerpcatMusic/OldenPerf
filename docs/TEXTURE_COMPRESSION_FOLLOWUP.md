# Texture compression and mip residency — 27 October 2026

## Current installed state

The 27 October build disables runtime mip streaming and sets `QualitySettings.globalTextureMipmapLimit` to 0. Unity keeps the original mip chains and their top-resolution levels; the compression pass did not delete mip levels. This addresses the reported soft city textures by removing automatic budget-driven mip drops. The earlier 2,048 MiB streaming budget and max-reduction setting remain in config but are dormant while streaming is off.

The serialized inventory contains 8,899 `Texture2D` objects. None has `m_MipsStripped` set and none has a nonempty mip-limit group; 23 explicitly ignore the global limit and remain at full resolution.

The new exact-hash asset pass converted 208 additional textures across 25 asset files: 182 to BC7 and 26 to BC1/DXT1. This includes 21 full mip chains; every authored level was independently encoded and decoded. Opaque BC1 candidates had to meet 32 dB PSNR and BC7 candidates 30 dB. The lowest gated level was 30.03 dB. Very small tail mips were measured but did not gate a chain; the lowest tail score was 25.42 dB. Twenty-six candidates failed quality checks and were left untouched. The scan also found 805 alpha-bearing BC7/DXT5 mip chains and kept them unchanged. City, town, settlement, fortress and building-named textures are excluded from this follow-up; the earlier 25 October pass had already converted the small `dungeon_city` icon.

Using exact per-mip block sizes, full-chain serialized `Texture2D` payload is estimated to fall from 3,826.86 MiB after the 25 October pass to 3,792.33 MiB: **34.53 MiB less**. Across both passes, the estimate falls from 3,876.46 to 3,792.33 MiB, or **84.13 MiB**. These are format payload estimates, not live VRAM residency. Disabling streaming can keep higher-resolution levels resident, so this file-size delta does not predict runtime memory. The game was left closed; there is no post-change visual, VRAM or frame-time measurement.

The patcher skips named data textures, readable textures and textures that miss the quality gate or would not occupy fewer GPU bytes. BC1 and BC7 are lossy formats; the quality gates bound measured per-level error, but do not make them lossless. BC4 was not used on Alpha8 textures because that would move the scalar channel and can break masks, fonts or UI consumers.

The exact-hash manifests, asset backups, config backup and install receipt are under:

`/data/dev/workspace/PluginArtifacts/OldenPerf/mip-compression-20261027/`

The changed-texture inventory is `/data/dev/workspace/RunScratch/olden-texture-compression-20261027/texture-delta.csv`.

Unity references: [`streamingMipmapsActive`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/QualitySettings-streamingMipmapsActive.html) controls automatic mip streaming, and [`globalTextureMipmapLimit`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/QualitySettings-globalTextureMipmapLimit.html) sets the maximum mip resolution reduction.

## Initial 25 October pass — historical baseline

## Installed changes

The Steam build is still 26134266 / Unity 6000.0.66f1, guarded by GameAssembly SHA-256 `eb57aeecaa27ff5764ddff76c5a3459857ed73f3abdefb14cfa370facb85e940`.

The first exact-hash asset patch was installed across 26 files. It scanned 51 asset files and emitted block-compressed payloads using formats that reduce GPU storage:

- BC1/DXT1 (4 bits per pixel) for opaque RGB/RGBA art. Existing 8-bit-per-pixel BC7/DXT5 art is repacked only when alpha is fully opaque.
- BC7 (8 bits per pixel) for alpha art that passes its decoded quality gate.
- DXT5 and BC7 keep their 8-bit-per-pixel storage when alpha is needed. BC7 improves quality over DXT5 on supported desktop hardware, but does not reduce memory relative to it.

The first pass converted 667 `Texture2D` objects: 492 to BC7 and 175 to BC1/DXT1. Of the BC1 results, 162 were existing opaque BC7/DXT5 textures repacked to 4-bpp BC1. DXT1 candidates had to decode at PSNR >= 32 dB; BC7 candidates at >= 30 dB. The lowest accepted result was 30.20 dB. Six BC7 candidates failed the gate and remained in their source format. Mip chains, readable textures, tiny textures, and named data textures remained untouched, except that mipmapped textures retained their streaming flags.

The installed serialized `Texture2D` inventory has 8,899 objects. Format counts changed from 923 RGBA32 / 57 RGB24 / 1,505 BC7 / 2,396 DXT1 / 3,846 DXT5 to 430 / 45 / 1,836 / 2,571 / 3,845 respectively. The other 170 Alpha8 textures and two Crunch textures were left in their original formats.

## What this saves, and what it does not

Using block sizes for each mip, estimated full-chain `Texture2D` payload fell from 3,876.46 MiB to 3,826.86 MiB: **49.60 MiB less across all serialized assets**. This is an asset-format estimate, not physical VRAM residency, and assets from every scene are not all resident at once. Existing compression was already present, so the incremental reduction is much smaller than the initial raw-to-BC7 pass.

The streaming budget was temporarily lowered to 1,024 MiB with maximum mip reduction 3. The user then reported soft city textures, so I restored the previous 2,048 MiB budget and maximum reduction 2. Most high-resolution city diffuse maps in the inventory were already DXT1/DXT5. This initial pass changed only the small `dungeon_city` icon among city/town/building-named textures. The subsequent 27 October change disables mip streaming; the game remains closed, so neither setting has a post-change live visual check.

The remaining 170 Alpha8 textures account for about 343 MiB in the full-chain inventory. BC4 would halve their storage, but stores its scalar in the red channel while these assets use alpha-only semantics. Changing their format without updating every consuming material/shader can make masks, fonts, or UI art opaque. BC4 is therefore not applied. BC5 is also 8 bits per pixel, so it would not save memory over BC7/DXT5. Crunch only reduces disk size because Unity expands it to BCn before GPU upload. BC6H is not applicable to the dynamic half-float render targets that dominate the render-target inventory; those need renderable formats.

The previous live map inventory contained roughly 1.1 GiB of render textures, including multiple full-resolution HDR and depth surfaces. Texture block compression cannot reduce those writable render targets. The user asked that the game remain closed, so there is no new live VRAM or frame-time measurement after this installation.

## Reproducibility and rollback point

Installed files were checked against per-file source, output, external-resource and GameAssembly hashes. The prior installed versions were reflink-backed before replacement. The build inputs, manifests and install receipt are under:

`/data/dev/workspace/PluginArtifacts/OldenPerf/multi-format-20261025/` (first pass)

`/data/dev/workspace/PluginArtifacts/OldenPerf/mip-compression-20261027/` (current pass)

The before/after serialized inventories are under:

`/data/dev/workspace/RunScratch/olden-texture-compression-20261025/`

No game launch or live verification was performed.
