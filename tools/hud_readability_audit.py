"""Read-only audit: which HUD Texture2D readable flags are load-bearing.

Reads serialized assets and GameAssembly.dll under DATA_DIR and writes nothing.
Serialized facts: HUD textures and sprites, every MonoBehaviour whose raw bytes
point at a HUD sprite (PPtr scan), and the Image targets of the three
HUD/cutout scripts. Native facts: the alpha-hit-test threshold constants, read
only when GameAssembly.dll matches --expect-sha (fails closed otherwise).

Usage: tools/hud_readability_audit.py DATA_DIR [--json OUT] [--expect-sha SHA]
"""
import argparse
import hashlib
import json
import os
import re
import struct
import sys

HUD = re.compile(r"^HUD_(RightPanel_Color_\d+|ColoRPanel_Back_(\d+|neutral)|RightPanel_UnderColor|ColoRPanel_Top)$")
SERIALIZED = re.compile(r"^(level\d+|sharedassets\d+\.assets|resources\.assets|globalgamemanagers\.assets)$")
# BhUiCutoutTreshhold and the two HUD colour scripts each hold exactly one Image PPtr.
TARGET_SCRIPTS = ("BhUiCutoutTreshhold", "BhHudSessionColor", "BhHeroHudColor")
# Virtual addresses in GameAssembly.dll eb57aeec... (ISIL: BhUiCutoutTreshhold.Start and Image.IsRaycastLocationValid).
EXPECT_SHA = "eb57aeecaa27ff5764ddff76c5a3459857ed73f3abdefb14cfa370facb85e940"
CUTOUT_THRESHOLD_VA = 0x18469B930
RAYCAST_MAX_VA = 0x184689BA8


def pptr_bytes(file_id, path_id):
    return struct.pack("<i", file_id) + struct.pack("<q", path_id)


def single_pptr_field(raw):
    """Image PPtr of a MonoBehaviour whose only serialized field is one PPtr after m_Name."""
    name_len = struct.unpack_from("<i", raw, 28)[0]
    return struct.unpack_from("<iq", raw, (28 + 4 + name_len + 3) & ~3)


def va_to_offset(data, va):
    """File offset of a virtual address in a PE image (PE32+ only)."""
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("not a PE file")
    n_sections = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    if struct.unpack_from("<H", data, opt)[0] != 0x20B:
        raise ValueError("not PE32+")
    image_base = struct.unpack_from("<Q", data, opt + 24)[0]
    rva = va - image_base
    for i in range(n_sections):
        s = opt + opt_size + i * 40
        virt_size, virt_addr, raw_size, raw_ptr = struct.unpack_from("<IIII", data, s + 8)
        if virt_addr <= rva < virt_addr + max(virt_size, raw_size):
            return raw_ptr + (rva - virt_addr)
    raise ValueError(f"VA {va:#x} is not in any section")


def sha256_file(path):
    with open(path, "rb") as handle:
        return hashlib.file_digest(handle, "sha256").hexdigest()


def mib(nbytes):
    return nbytes / 2**20


def scan(data_dir):
    import UnityPy
    from UnityPy.enums import TextureFormat

    files = sorted(n for n in os.listdir(data_dir) if SERIALIZED.match(n))
    scripts, textures, sprites = {}, {}, {}
    # Pass 1: script names, HUD textures and HUD sprites (names and readable flags).
    for name in files:
        env = UnityPy.load(os.path.join(data_dir, name))
        for obj in env.objects:
            if obj.type.name == "MonoScript":
                scripts[(name, obj.path_id)] = obj.read_typetree(check_read=False).get("m_ClassName")
            elif obj.type.name == "Texture2D":
                tex = obj.read_typetree(check_read=False)
                if HUD.match(tex["m_Name"]):
                    textures[(name, obj.path_id)] = dict(
                        name=tex["m_Name"], readable=bool(tex["m_IsReadable"]),
                        w=tex["m_Width"], h=tex["m_Height"],
                        fmt=TextureFormat(tex["m_TextureFormat"]).name,
                    )
            elif obj.type.name == "Sprite":
                spr_name = obj.read_typetree(check_read=False)["m_Name"]
                if HUD.match(spr_name):
                    sprites[(name, obj.path_id)] = spr_name

    # Pass 2: every MonoBehaviour raw record that carries a PPtr to a HUD sprite, and the target-script records.
    refs, targets = [], {}
    for name in files:
        env = UnityPy.load(os.path.join(data_dir, name))
        externals = {}
        for sf in env.files.values():
            for i, ext in enumerate(getattr(sf, "externals", []) or [], start=1):
                externals[i] = os.path.basename(ext.path or ext.pathName or "")
        needles = {}
        for (sprite_file, sprite_pid), spr_name in sprites.items():
            if sprite_file == name:
                fid = 0
            else:
                fid = next((i for i, n in externals.items() if n == sprite_file), None)
            if fid is not None:
                needles[pptr_bytes(fid, sprite_pid)] = spr_name
        for obj in env.objects:
            if obj.type.name != "MonoBehaviour":
                continue
            raw = obj.get_raw_data()
            if len(raw) < 28:
                continue
            script_fid, script_pid = struct.unpack_from("<iq", raw, 16)
            script_file = name if script_fid == 0 else externals.get(script_fid, "")
            cls = scripts.get((script_file, script_pid))
            if cls in TARGET_SCRIPTS and len(raw) >= 44:
                fid, pid = single_pptr_field(raw)
                image_file = name if fid == 0 else externals.get(fid, "")
                targets.setdefault(cls, []).append(dict(
                    owner=[name, obj.path_id], image=[image_file, pid], length=len(raw)))
            for needle, spr_name in needles.items():
                if needle in raw:
                    refs.append(dict(file=name, path_id=obj.path_id, cls=cls, sprite=spr_name))
        print(f"scanned {name}: {len(refs)} sprite references so far", file=sys.stderr)
    return dict(textures=textures, sprites=sprites, refs=refs, targets=targets)


def classify(found):
    textures, refs, targets = found["textures"], found["refs"], found["targets"]
    image_of = {cls: {tuple(t["image"]) for t in rows} for cls, rows in targets.items()}
    cutout = image_of.get("BhUiCutoutTreshhold", set())
    session_pics = image_of.get("BhHudSessionColor", set())
    hero_pics = image_of.get("BhHeroHudColor", set())
    cut_rows = []
    for ref in refs:
        if ref["cls"] != "Image":
            continue
        key = (ref["file"], ref["path_id"])
        cut_rows.append(dict(ref, cutout_target=key in cutout, session_pic=key in session_pics,
                             hero_pic=key in hero_pics))
    static_required = sorted({r["sprite"] for r in cut_rows if r["cutout_target"]})
    # A cutout pic Image can receive any variant of its family at runtime (Init(colorId)).
    families = set()
    for r in cut_rows:
        if r["cutout_target"] and r["session_pic"]:
            families.add("HUD_RightPanel_Color_")
        if r["cutout_target"] and r["hero_pic"]:
            families.add("HUD_ColoRPanel_Back_")
    runtime_undetermined = sorted(
        n for n in {t["name"] for t in textures.values()}
        if any(n.startswith(f) and n[len(f):].isdigit() for f in families) and n not in static_required)
    readable = {v["name"]: v for v in textures.values() if v["readable"]}
    total = sum(mib(v["w"] * v["h"] * 4) for v in readable.values())
    static_mib = sum(mib(readable[n]["w"] * readable[n]["h"] * 4) for n in static_required if n in readable)
    rt_mib = sum(mib(readable[n]["w"] * readable[n]["h"] * 4) for n in runtime_undetermined if n in readable)
    return dict(
        cutout_rows=cut_rows,
        static_required=static_required,
        runtime_undetermined=runtime_undetermined,
        readable_cpu_mib_upper_bound=round(total, 3),
        static_required_mib=round(static_mib, 3),
        runtime_undetermined_mib=round(rt_mib, 3),
        provably_flippable_now=sorted(set(readable) - set(static_required) - set(runtime_undetermined)),
    )


def read_thresholds(game_assembly, expect_sha):
    actual = sha256_file(game_assembly)
    if actual != expect_sha:
        return dict(status="unverified", sha256=actual)
    with open(game_assembly, "rb") as handle:
        data = handle.read()
    values = {}
    for label, va in (("cutout_threshold", CUTOUT_THRESHOLD_VA), ("raycast_max", RAYCAST_MAX_VA)):
        values[label] = struct.unpack_from("<f", data, va_to_offset(data, va))[0]
    return dict(status="verified", sha256=actual, **values)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("data_dir")
    parser.add_argument("--json", help="write the report here")
    parser.add_argument("--expect-sha", default=EXPECT_SHA, help="required GameAssembly.dll SHA-256")
    args = parser.parse_args(argv)

    found = scan(args.data_dir)
    report = classify(found)
    report["textures"] = sorted(found["textures"].values(), key=lambda v: v["name"])
    # GameAssembly.dll sits next to the *_Data directory, one level above DATA_DIR.
    game_assembly = os.path.join(args.data_dir, "..", "GameAssembly.dll")
    report["thresholds"] = (read_thresholds(game_assembly, args.expect_sha)
                            if os.path.exists(game_assembly) else dict(status="missing"))
    text = json.dumps(report, indent=1, sort_keys=True)
    if args.json:
        with open(args.json, "w") as handle:
            handle.write(text + "\n")
    print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
