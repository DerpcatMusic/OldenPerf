import importlib.util
import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

spec = importlib.util.spec_from_file_location("frame_compare", Path(__file__).resolve().parents[1] / "tools/frame_compare.py")
fc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fc)


def solid(value, shape=(8, 8)):
    return np.full((*shape, 3), value, dtype=np.uint8)


def ref_pair(a, b, keep=None):
    k = np.ones(a.shape[:2], dtype=bool) if keep is None else keep
    return (a, k), (b, k)


class FrameCompareTests(unittest.TestCase):
    def test_identical_images_have_zero_diff(self):
        a = solid(10)
        d = fc.diff(a, a.copy(), np.ones((8, 8), bool))
        self.assertEqual((d.differing, d.max_delta), (0, 0))

    def test_single_pixel_change_is_counted_and_measured(self):
        a = solid(10)
        b = a.copy()
        b[3, 4, 1] = 17
        d = fc.diff(a, b, np.ones((8, 8), bool))
        self.assertEqual((d.differing, d.max_delta), (1, 7))

    def test_mask_excludes_hud_rectangle(self):
        with tempfile.TemporaryDirectory(dir="/data/dev/workspace/RunScratch") as tmp:
            a = solid(10)
            b = a.copy()
            b[0:2, 0:2] = 200  # changed HUD corner
            path = Path(tmp) / "b.png"
            Image.fromarray(b).save(path)
            img, keep = fc.load(path, mask=[(0, 0, 2, 2)])
            d = fc.diff(a, img, keep)
            self.assertEqual(d.differing, 0)
            self.assertEqual(d.pixels, 64 - 4)

    def test_deterministic_references_require_bit_identical_candidate(self):
        ref = solid(50)
        refs = [(ref, np.ones((8, 8), bool)), (ref.copy(), np.ones((8, 8), bool))]
        self.assertTrue(fc.judge(refs, (ref.copy(), np.ones((8, 8), bool)))["pass"])
        off_by_one = ref.copy()
        off_by_one[5, 5, 0] = 51
        verdict = fc.judge(refs, (off_by_one, np.ones((8, 8), bool)))
        self.assertFalse(verdict["pass"])
        self.assertEqual(verdict["candidate_vs_reference_worst"]["max_delta"], 1)

    def test_noisy_references_are_inconclusive(self):
        base = solid(100)
        noisy = base.copy()
        noisy[0, 0] = 104  # animated/TAA pixel seen between two unchanged captures
        refs = [(base, np.ones((8, 8), bool)), (noisy, np.ones((8, 8), bool))]
        env = fc.envelope(refs)
        self.assertEqual((env.differing, env.max_delta), (1, 4))
        candidate = base.copy()
        candidate[0, 0] = 103  # within the measured noise
        result = fc.judge(refs, (candidate, np.ones((8, 8), bool)))
        self.assertFalse(result["pass"])
        self.assertEqual(result["status"], "inconclusive_reference_noise")
        wrong = base.copy()
        wrong[1, 1] = 130  # a real change far beyond the noise
        self.assertFalse(fc.judge(refs, (wrong, np.ones((8, 8), bool)))["pass"])

    def test_noise_elsewhere_cannot_hide_a_new_error(self):
        base = solid(100)
        noisy = base.copy()
        noisy[0, 0] = 104
        wrong = base.copy()
        wrong[1, 1] = 102  # Fits the old global count/delta envelope.
        keep = np.ones((8, 8), bool)
        self.assertFalse(fc.judge([(base, keep), (noisy, keep)], (wrong, keep))["pass"])

    def test_empty_comparison_is_rejected(self):
        a = solid(100)
        keep = np.zeros((8, 8), bool)
        with self.assertRaises(ValueError):
            fc.judge([(a, keep), (a, keep)], (a, keep))

    def test_candidate_cannot_mask_away_a_change(self):
        a = solid(100)
        b = a.copy()
        b[1, 1] = 200
        keep = np.ones((8, 8), bool)
        hidden = keep.copy()
        hidden[1, 1] = False
        with self.assertRaises(ValueError):
            fc.judge([(a, keep), (a, keep)], (b, hidden))

    def test_refuses_to_judge_without_two_references(self):
        with self.assertRaises(ValueError):
            fc.judge([ref_pair(solid(1), solid(1))[0]], (solid(1), np.ones((8, 8), bool)))

    def test_size_mismatch_is_an_error(self):
        with self.assertRaises(ValueError):
            fc.diff(solid(1, (8, 8)), solid(1, (4, 4)), np.ones((8, 8), bool))


if __name__ == "__main__":
    unittest.main()
