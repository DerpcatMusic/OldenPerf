import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("gpu_residency", Path(__file__).resolve().parents[1] / "tools/gpu_residency.py")
gpu = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gpu)


class GpuResidencyTests(unittest.TestCase):
    def test_units_and_missing_values(self):
        self.assertEqual(gpu.kib("2 MiB"), 2048)
        self.assertEqual(gpu.kib("2048 bytes"), 2)
        self.assertEqual(gpu.kib("0 KiB"), 0)
        self.assertIsNone(gpu.kib(None))

    def test_duplicate_descriptors_do_not_double_count_client(self):
        text = "drm-driver:\tamdgpu\ndrm-client-id:\t1\ndrm-pdev:\t0000:03:00.0\ndrm-resident-vram:\t100 KiB\ndrm-resident-gtt:\t80 KiB\namd-requested-vram:\t160 KiB\n"
        with patch.object(gpu.glob, "glob", return_value=["/fake/1", "/fake/2"]), patch.object(Path, "read_text", return_value=text):
            rows = gpu.sample_process(1)
        self.assertEqual(rows["0000:03:00.0"]["drm-resident-vram"], 100)
        self.assertEqual(rows["0000:03:00.0"]["drm-resident-gtt"], 80)
        self.assertIsNone(rows["0000:03:00.0"]["amd-evicted-vram"])


if __name__ == "__main__":
    unittest.main()
