import importlib.util
from pathlib import Path
import struct
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("hud_readability_audit", ROOT / "tools/hud_readability_audit.py")
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


def mb_with_pptr(name, target):
    """MonoBehaviour raw bytes: 28-byte header, m_Name, then one PPtr (fileID, pathID)."""
    raw = bytearray(28) + struct.pack("<i", len(name)) + name.encode()
    raw += b"\0" * (-len(raw) % 4)
    return bytes(raw) + audit.pptr_bytes(*target)


def minimal_pe(va_base=0x180000000):
    data = bytearray(0x800)
    pe = 0x40
    data[0x3C:0x40] = struct.pack("<I", pe)
    data[pe:pe + 4] = b"PE\0\0"
    struct.pack_into("<H", data, pe + 6, 1)           # one section
    struct.pack_into("<H", data, pe + 20, 240)        # optional header size
    opt = pe + 24
    struct.pack_into("<H", data, opt, 0x20B)          # PE32+
    struct.pack_into("<Q", data, opt + 24, va_base)   # ImageBase
    sec = opt + 240
    struct.pack_into("<IIII", data, sec + 8, 0x2000, 0x1000, 0x2000, 0x400)
    return bytes(data)


class RecordLayoutTests(unittest.TestCase):
    def test_sprite_pptr_matches_dump_bytes(self):
        # resources.assets sprite HUD_ColoRPanel_Back_3 is pathID 24304 seen through fileID 2.
        self.assertEqual(audit.pptr_bytes(2, 24304).hex(), "02000000f05e000000000000")

    def test_single_pptr_after_empty_and_nonempty_name(self):
        self.assertEqual(audit.single_pptr_field(mb_with_pptr("", (2, 7))), (2, 7))
        self.assertEqual(audit.single_pptr_field(mb_with_pptr("ab", (0, 9))), (0, 9))

    def test_virtual_address_maps_to_section_offset(self):
        self.assertEqual(audit.va_to_offset(minimal_pe(), 0x180001234), 0x400 + 0x234)
        with self.assertRaises(ValueError):
            audit.va_to_offset(minimal_pe(), 0x180005000)


class PolicyTests(unittest.TestCase):
    def fake_found(self, cutout_rows):
        textures = {}
        names = [f"HUD_RightPanel_Color_{i}" for i in range(9)] + [f"HUD_ColoRPanel_Back_{i}" for i in range(9)]
        names += ["HUD_ColoRPanel_Back_neutral", "HUD_RightPanel_UnderColor", "HUD_ColoRPanel_Top"]
        for i, name in enumerate(names):
            readable = name != "HUD_ColoRPanel_Back_neutral"
            textures[("f", i)] = dict(name=name, readable=readable, w=455, h=2048 if "Color_" in name else 62,
                                      fmt="RGBA32")
        return dict(textures=textures, sprites={}, refs=cutout_rows["refs"], targets=cutout_rows["targets"])

    def test_cutout_image_pins_variant_family_and_static_hud_textures(self):
        refs = [
            dict(file="level12", path_id=5970, cls="Image", sprite="HUD_RightPanel_Color_3"),
            dict(file="level11", path_id=1, cls="Image", sprite="HUD_ColoRPanel_Back_1"),
            dict(file="level12", path_id=6222, cls="Image", sprite="HUD_RightPanel_UnderColor"),
        ]
        targets = {
            "BhUiCutoutTreshhold": [dict(owner=["x", 1], image=["level12", 5970], length=44),
                                    dict(owner=["x", 2], image=["level12", 6222], length=44)],
            "BhHudSessionColor": [dict(owner=["x", 3], image=["level12", 5970], length=44)],
        }
        report = audit.classify(self.fake_found(dict(refs=refs, targets=targets)))
        self.assertEqual(report["static_required"], ["HUD_RightPanel_Color_3", "HUD_RightPanel_UnderColor"])
        self.assertIn("HUD_RightPanel_Color_0", report["runtime_undetermined"])
        self.assertNotIn("HUD_RightPanel_Color_3", report["runtime_undetermined"])
        self.assertNotIn("HUD_ColoRPanel_Back_0", report["runtime_undetermined"])
        self.assertEqual(report["provably_flippable_now"], ["HUD_ColoRPanel_Back_0", "HUD_ColoRPanel_Back_1",
                                                           "HUD_ColoRPanel_Back_2", "HUD_ColoRPanel_Back_3",
                                                           "HUD_ColoRPanel_Back_4", "HUD_ColoRPanel_Back_5",
                                                           "HUD_ColoRPanel_Back_6", "HUD_ColoRPanel_Back_7",
                                                           "HUD_ColoRPanel_Back_8", "HUD_ColoRPanel_Top"])


if __name__ == "__main__":
    unittest.main()
