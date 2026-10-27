import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("asset_manifest", ROOT / "tools/asset_manifest.py")
assets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assets)


class AssetInstallTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir="/data/dev/workspace/RunScratch")
        self.root = Path(self.temp.name)
        self.game = self.root / "game"
        self.game.mkdir()
        self.backups = self.root / "backup"
        self.target = self.game / "resources.assets"
        self.target.write_bytes(b"new Steam build original")
        self.external = self.game / "resources.assets.resS"
        self.external.write_bytes(b"external resource")
        self.patched = self.root / "resources.assets"
        self.patched.write_bytes(b"compressed new build")
        self.manifest = self.root / "resources.assets.manifest.json"
        self.manifest.write_text(json.dumps(dict(schema=1, asset="resources.assets",
            source_sha256=assets.sha256(self.target), patched_sha256=assets.sha256(self.patched),
            externals=[dict(name=self.external.name, sha256=assets.sha256(self.external))])))

    def tearDown(self):
        self.temp.cleanup()

    def apply(self, action):
        assets.apply(action, self.manifest, self.game, self.backups, check_running=False)

    def test_round_trip_preserves_current_build_original(self):
        original = self.target.read_bytes()
        self.apply("install")
        self.assertEqual(self.target.read_bytes(), self.patched.read_bytes())
        self.assertEqual((self.backups / self.target.name).read_bytes(), original)
        self.apply("uninstall")
        self.assertEqual(self.target.read_bytes(), original)

    def test_steam_update_refuses_stale_restore(self):
        self.apply("install")
        self.target.write_bytes(b"future Steam build")
        with self.assertRaises(ValueError):
            self.apply("uninstall")
        self.assertEqual(self.target.read_bytes(), b"future Steam build")

    def test_changed_external_refuses_install(self):
        original = self.target.read_bytes()
        self.external.write_bytes(b"future external")
        with self.assertRaises(ValueError):
            self.apply("install")
        self.assertEqual(self.target.read_bytes(), original)

    def test_corrupt_patch_refuses_install(self):
        self.patched.write_bytes(b"truncated")
        with self.assertRaises(ValueError):
            self.apply("install")
        self.assertFalse(self.backups.exists())


if __name__ == "__main__":
    unittest.main()
