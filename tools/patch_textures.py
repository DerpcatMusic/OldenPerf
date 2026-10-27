"""Repack Olden Era textures into smaller GPU-resident block formats.

Opaque RGB/RGBA art is eligible for BC1/DXT1 (4 bpp). Other eligible raw RGBA
art uses BC7 (8 bpp). Raw mip chains are encoded level-by-level with per-level
decode checks. Existing BC7/DXT5 art is moved to BC1 only when alpha is fully
opaque and a fresh encode clears the stricter quality gate. Alpha/data textures
are preserved. Crunch is intentionally not used because it saves disk space but
expands to BCn before reaching VRAM.

Writes patched copies to --out; install only through the exact-hash manifest.
"""
import argparse, os, re, sys, math, hashlib, json
import numpy as np
import UnityPy
from PIL import Image
from UnityPy.enums import TextureFormat as TF
from UnityPy.export.Texture2DConverter import image_to_texture2d, parse_image_data

RAW = {TF.RGBA32, TF.ARGB32, TF.RGB24, TF.BGRA32}
OPAQUE_REPACK = {TF.DXT5, TF.DXT5Crunched, TF.BC7}
# Data textures where block compression would change behaviour, not just looks.
SKIP = re.compile(r"lut|ramp|noise|dither|bayer|lookup|height|normal|flow|distort|gradient|splatalpha|metallic|roughness|occlusion|smoothness|\borm\b|city|town|building|settlement|fortress|_n$|mask|sactx", re.I)
MIN_PIXELS = 16 * 16


def psnr(a, b):
    mse = np.mean((a.astype(np.float32) - b.astype(np.float32)) ** 2)
    return 99.0 if mse == 0 else 10 * math.log10(255 ** 2 / mse)


def gpu_payload_size(width, height, fmt):
    """Estimate one mip's GPU payload using the format's block dimensions."""
    blocks = ((width + 3) // 4) * ((height + 3) // 4)
    if fmt in {TF.DXT1, TF.DXT1Crunched, TF.BC4}:
        return blocks * 8
    if fmt in {TF.DXT3, TF.DXT5, TF.DXT5Crunched, TF.BC5, TF.BC6H, TF.BC7}:
        return blocks * 16
    bpp = {TF.RGBA32: 32, TF.ARGB32: 32, TF.BGRA32: 32, TF.RGB24: 24}.get(fmt)
    if bpp is None:
        raise ValueError(f"No GPU payload estimate for {fmt.name}")
    return width * height * bpp // 8


def encode_mip_chain(images, target, version, platform, quality_floor):
    """Encode and independently decode each mip; small tail levels do not gate the chain."""
    payloads, scores = [], []
    base_needs_quality_gate = not (images[0][1] >= 16 and images[0][2] >= 16)
    for level, (image, width, height) in enumerate(images):
        data, newfmt = image_to_texture2d(image, target)
        bytes_per_block = 8 if target == TF.DXT1 else 16
        expected = ((width + 3) // 4) * ((height + 3) // 4) * bytes_per_block
        if newfmt != target or len(data) != expected:
            return None, scores
        decoded = parse_image_data(data, width, height, newfmt, version, platform).convert("RGBA")
        score = psnr(np.asarray(image), np.asarray(decoded))
        scores.append(score)
        quality_gated = (width >= 16 and height >= 16) or (level == 0 and base_needs_quality_gate)
        if decoded.size != image.size or (quality_gated and score < quality_floor):
            return None, scores
        payloads.append(data)
    return b"".join(payloads), scores


def decode_mip_chain(raw, width, height, mip_count, fmt, version, platform):
    """Decode raw or BC3/BC7 mip payloads level-by-level; return None on layout mismatch."""
    if fmt in RAW:
        source_bpp = 3 if fmt == TF.RGB24 else 4
        block_bytes = None
    elif fmt in {TF.DXT5, TF.BC7}:
        source_bpp = None
        block_bytes = 16
    else:
        return None
    levels, offset = [], 0
    for level in range(mip_count):
        mw, mh = max(1, width >> level), max(1, height >> level)
        size = mw * mh * source_bpp if block_bytes is None else ((mw + 3) // 4) * ((mh + 3) // 4) * block_bytes
        chunk = raw[offset:offset + size]
        if len(chunk) != size:
            return None
        try:
            image = parse_image_data(chunk, mw, mh, fmt, version, platform).convert("RGBA")
        except Exception:
            return None
        levels.append((image, mw, mh))
        offset += size
    return levels if offset == len(raw) else None


def patch(path, out, stream, dxt1_psnr, bc7_psnr, mipchains_only=False):
    if os.path.realpath(path) == os.path.realpath(out):
        raise ValueError("Patch output must not overwrite the source asset")
    def sha256(p):
        with open(p, "rb") as handle:
            return hashlib.file_digest(handle, "sha256").hexdigest()
    source_hash = sha256(path)
    env = UnityPy.load(path)
    f = list(env.files.values())[0]
    scaled = {}
    stats = dict(bc7=0, bc7_before=0, bc7_after=0, dxt1=0, dxt1_before=0, dxt1_after=0,
                 opaque_repacked=0, stream=0, skipped_readable=0, skipped_name=0,
                 skipped_small=0, skipped_mips=0, skipped_mip_layout=0, skipped_alpha_mips=0,
                 skipped_no_savings=0, mip_chains=0, skipped_format=0,
                 dxt1_quality_rejected=0, bc7_quality_rejected=0)
    verified_psnr = []
    all_mip_psnr = []
    n = 0
    for o in env.objects:
        if o.type.name != "Texture2D":
            continue
        t = o.read_typetree()
        fmt, w, h, mips = TF(t["m_TextureFormat"]), t["m_Width"], t["m_Height"], t["m_MipCount"]
        if t.get("m_TextureDimension", 2) != 2 or t.get("m_ImageCount", 1) != 1:
            continue
        if mipchains_only and mips == 1:
            continue

        if stream and mips > 1 and not t["m_StreamingMipmaps"]:
            t["m_StreamingMipmaps"] = True
            o.save_typetree(t)
            stats["stream"] += 1

        if t["m_IsReadable"]:
            stats["skipped_readable"] += 1
            continue
        if SKIP.search(t["m_Name"]):
            stats["skipped_name"] += 1
            continue
        if w * h < MIN_PIXELS:
            stats["skipped_small"] += 1
            continue

        from_raw = fmt in RAW
        if mips > 1:
            if not (from_raw or fmt in {TF.DXT5, TF.BC7}):
                stats["skipped_mips"] += 1
                continue
            try:
                raw = bytes(o.read().get_image_data())
            except Exception:
                stats["skipped_mip_layout"] += 1
                continue
            levels = decode_mip_chain(raw, w, h, mips, fmt, o.version, f.target_platform)
            if levels is None:
                stats["skipped_mip_layout"] += 1
                continue
            if w * h < MIN_PIXELS:
                stats["skipped_small"] += 1
                continue
            opaque = all(image.getchannel("A").getextrema() == (255, 255) for image, _, _ in levels)
            if not from_raw and not opaque:
                stats["skipped_alpha_mips"] += 1
                continue
            target = TF.DXT1 if opaque else TF.BC7
            floor = dxt1_psnr if target == TF.DXT1 else bc7_psnr
            data, scores = encode_mip_chain(levels, target, o.version, f.target_platform, floor)
            if data is None and target == TF.DXT1 and from_raw:
                target, floor = TF.BC7, bc7_psnr
                data, scores = encode_mip_chain(levels, target, o.version, f.target_platform, floor)
            if data is None:
                stats["bc7_quality_rejected" if target == TF.BC7 else "dxt1_quality_rejected"] += 1
                continue
            if len(data) >= len(raw):
                stats["skipped_no_savings"] += 1
                continue
            if target == TF.DXT1:
                stats["dxt1"] += 1
                stats["dxt1_before"] += len(raw)
                stats["dxt1_after"] += len(data)
                if not from_raw:
                    stats["opaque_repacked"] += 1
            else:
                stats["bc7"] += 1
                stats["bc7_before"] += len(raw)
                stats["bc7_after"] += len(data)
            stats["mip_chains"] += 1
            n += 1
            all_mip_psnr.extend(scores)
            base_needs_quality_gate = not (levels[0][1] >= 16 and levels[0][2] >= 16)
            verified_psnr.extend(
                score for level, (score, (_, mw, mh)) in enumerate(zip(scores, levels))
                if (mw >= 16 and mh >= 16) or (level == 0 and base_needs_quality_gate)
            )
            t.update(m_TextureFormat=int(target), m_CompleteImageSize=len(data), m_MipCount=mips)
            t["image data"] = data
            t["m_StreamData"] = {"offset": 0, "size": 0, "path": ""}
            o.save_typetree(t)
            continue
        if mips != 1:
            stats["skipped_mips"] += 1
            continue
        if not from_raw and fmt not in OPAQUE_REPACK:
            stats["skipped_format"] += 1
            continue

        img = o.read().image.convert("RGBA")
        opaque = img.getchannel("A").getextrema() == (255, 255)
        # Existing compressed art only gets another lossy generation when alpha is
        # provably unused. This is where 8-bpp BC7/DXT5 can become 4-bpp BC1.
        if not from_raw and not opaque:
            continue
        nw, nh = (w + 3) & ~3, (h + 3) & ~3
        src = img if (nw, nh) == (w, h) else img.resize((nw, nh), Image.LANCZOS)
        target = TF.DXT1 if opaque else TF.BC7
        data, newfmt = image_to_texture2d(src, target)
        expected_bytes = ((nw + 3) // 4) * ((nh + 3) // 4) * (8 if newfmt == TF.DXT1 else 16)
        if newfmt != target or len(data) != expected_bytes:
            raise ValueError(f"Invalid {target.name} payload for {t['m_Name']}: {len(data)} != {expected_bytes}")
        # Decode every emitted block payload independently. This is the quality gate
        # for an asset build, not an occasional sample of it.
        decoded = parse_image_data(data, nw, nh, newfmt, o.version, f.target_platform).convert("RGBA")
        score = psnr(np.asarray(src), np.asarray(decoded))
        floor = dxt1_psnr if target == TF.DXT1 else bc7_psnr
        if decoded.size != src.size or score < floor:
            if target == TF.DXT1 and from_raw:
                # Keep opaque RGBA art at 8 bpp when BC1's lower color precision
                # misses the gate; BC7 still has a separate quality check.
                data, newfmt = image_to_texture2d(src, TF.BC7)
                expected_bytes = ((nw + 3) // 4) * ((nh + 3) // 4) * 16
                decoded = parse_image_data(data, nw, nh, newfmt, o.version, f.target_platform).convert("RGBA")
                score = psnr(np.asarray(src), np.asarray(decoded))
                target = TF.BC7
                floor = bc7_psnr
            else:
                stats["dxt1_quality_rejected" if target == TF.DXT1 else "bc7_quality_rejected"] += 1
                continue
        if newfmt != target or len(data) != expected_bytes or decoded.size != src.size or score < floor:
            stats["bc7_quality_rejected"] += 1
            continue

        before = gpu_payload_size(w, h, fmt)
        if len(data) >= before:
            stats["skipped_no_savings"] += 1
            continue
        if target == TF.BC7:
            stats["bc7"] += 1
            stats["bc7_before"] += before
            stats["bc7_after"] += len(data)
        else:
            stats["dxt1"] += 1
            stats["dxt1_before"] += before
            stats["dxt1_after"] += len(data)
            if not from_raw:
                stats["opaque_repacked"] += 1
        n += 1
        verified_psnr.append(score)
        all_mip_psnr.append(score)
        t.update(m_Width=nw, m_Height=nh, m_TextureFormat=int(newfmt), m_CompleteImageSize=len(data), m_MipCount=1)
        t["image data"] = data
        t["m_StreamData"] = {"offset": 0, "size": 0, "path": ""}
        o.save_typetree(t)
        if (nw, nh) != (w, h):
            scaled[o.path_id] = (nw / w, nh / h)

    fixed = 0
    if scaled:
        for o in env.objects:
            if o.type.name != "Sprite":
                continue
            s = o.read_typetree()
            tex = s["m_RD"]["texture"]
            if tex["m_FileID"] != 0 or tex["m_PathID"] not in scaled:
                continue
            sx, sy = scaled[tex["m_PathID"]]
            r = s["m_RD"]["textureRect"]
            r.update(x=r["x"] * sx, y=r["y"] * sy, width=r["width"] * sx, height=r["height"] * sy)
            off = s["m_RD"]["textureRectOffset"]
            off.update(x=off["x"] * sx, y=off["y"] * sy)
            o.save_typetree(s)
            fixed += 1

    with open(out + ".partial", "wb") as fh:
        fh.write(f.save())
    os.replace(out + ".partial", out)
    # Preserve a verifiable source-to-output relationship across Steam updates. External
    # resources are still used by unchanged objects and must match the source build too.
    external = []
    for suffix in (".resS", ".resource"):
        if os.path.isfile(path + suffix):
            external.append({"name": os.path.basename(path + suffix), "sha256": sha256(path + suffix)})
    manifest = dict(schema=1, asset=os.path.basename(path), source_sha256=source_hash,
                    patched_sha256=sha256(out), externals=external, stats=stats,
                    decoded_objects=n, min_verified_psnr_db=min(verified_psnr) if verified_psnr else None,
                    min_mip_psnr_db=min(all_mip_psnr) if all_mip_psnr else None,
                    quality_floors_db={"dxt1": dxt1_psnr, "bc7": bc7_psnr})
    game_binary = os.path.join(os.path.dirname(os.path.dirname(path)), "GameAssembly.dll")
    if os.path.isfile(game_binary):
        manifest["gameassembly_sha256"] = sha256(game_binary)
    with open(out + ".manifest.json", "w") as fh:
        json.dump(manifest, fh, indent=2)
    mb = lambda b: b / 2**20
    print(f"{os.path.basename(path)}: mipChains={stats['mip_chains']} BC7={stats['bc7']} ({mb(stats['bc7_before']):.1f}->{mb(stats['bc7_after']):.1f} MiB) "
          f"BC1/DXT1={stats['dxt1']} ({mb(stats['dxt1_before']):.1f}->{mb(stats['dxt1_after']):.1f} MiB; opaque_repacked={stats['opaque_repacked']}) "
          f"stream={stats['stream']} decoded={n} minGatedPSNR={min(verified_psnr) if verified_psnr else 'n/a'} "
          f"minMipPSNR={min(all_mip_psnr) if all_mip_psnr else 'n/a'} "
          f"resampled={len(scaled)} sprites_fixed={fixed} rejected={stats['dxt1_quality_rejected']+stats['bc7_quality_rejected']}", flush=True)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("files", nargs="+")
    ap.add_argument("--out", required=True)
    ap.add_argument("--stream", action="store_true")
    ap.add_argument("--dxt1-psnr", type=float, default=32.0,
                    help="Minimum decoded PSNR for 4-bpp BC1; raw art falls back to BC7")
    ap.add_argument("--bc7-psnr", type=float, default=30.0,
                    help="Minimum decoded PSNR for BC7 RGBA art")
    ap.add_argument("--mipchains-only", action="store_true",
                    help="Process raw mip chains only; leave single-mip textures untouched")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    for p in a.files:
        patch(p, os.path.join(a.out, os.path.basename(p)), a.stream, a.dxt1_psnr, a.bc7_psnr, a.mipchains_only)
