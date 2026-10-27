"""Compare controlled captures; require deterministic references for equivalence.

Reference differences are reported as diagnostics only. They never authorize a
candidate difference: noisy references make the result inconclusive. Masks must
be identical and leave a non-empty comparison region. A pass establishes exact
RGB equality only in that region, not equivalence of masked or future frames.
"""

import argparse
import itertools
import json
import sys
from dataclasses import asdict, dataclass

import numpy as np
from PIL import Image


@dataclass(frozen=True)
class Diff:
    pixels: int
    differing: int
    max_delta: int

    @property
    def fraction(self):
        return self.differing / self.pixels if self.pixels else 0.0


def load(path, mask=None):
    """RGB uint8 array. mask is a list of (x, y, w, h) rectangles to ignore."""
    img = np.asarray(Image.open(path).convert("RGB"))
    if mask:
        img = img.copy()
        keep = np.ones(img.shape[:2], dtype=bool)
        for x, y, w, h in mask:
            keep[y:y + h, x:x + w] = False
        return img, keep
    return img, np.ones(img.shape[:2], dtype=bool)


def diff(a, b, keep):
    if a.shape != b.shape:
        raise ValueError(f"size mismatch {a.shape} vs {b.shape}")
    delta = np.abs(a.astype(np.int16) - b.astype(np.int16)).max(axis=2)
    delta = delta[keep]
    return Diff(pixels=int(delta.size), differing=int(np.count_nonzero(delta)), max_delta=int(delta.max()) if delta.size else 0)


def envelope(references):
    """Worst pairwise difference between unchanged reference captures."""
    worst = Diff(0, 0, 0)
    for (a, ka), (b, kb) in itertools.combinations(references, 2):
        keep = ka & kb
        d = diff(a, b, keep)
        worst = Diff(d.pixels, max(worst.differing, d.differing), max(worst.max_delta, d.max_delta))
    return worst


def judge(references, candidate):
    """Fail closed: noisy reference captures cannot certify equivalence."""
    if len(references) < 2:
        raise ValueError("need at least two unchanged reference captures to establish noise")
    shape = references[0][0].shape
    common_keep = references[0][1]
    for img, keep in [*references, candidate]:
        if img.shape != shape or keep.shape != shape[:2]:
            raise ValueError("capture or mask size mismatch")
        if not np.array_equal(keep, common_keep):
            raise ValueError("all captures must use the same comparison mask")
    if not np.any(common_keep):
        raise ValueError("comparison region must contain pixels")
    env = envelope(references)
    worst = Diff(0, 0, 0)
    for ref, keep_r in references:
        cand, keep_c = candidate
        d = diff(ref, cand, keep_r & keep_c)
        worst = Diff(d.pixels, max(worst.differing, d.differing), max(worst.max_delta, d.max_delta))
    reference_stable = env.differing == 0
    passed = reference_stable and worst.differing == 0
    status = ("equivalent_in_compared_region" if passed else
              "different" if reference_stable else "inconclusive_reference_noise")
    return {
        "pass": bool(passed),
        "status": status,
        "noise_envelope": asdict(env),
        "candidate_vs_reference_worst": asdict(worst),
        "pixels_compared": worst.pixels,
    }


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--ref", action="append", required=True, help="unchanged reference capture (give at least 2)")
    p.add_argument("--cand", required=True, help="candidate capture")
    p.add_argument("--mask", action="append", default=[], help="x,y,w,h rectangle to ignore (HUD, cursor)")
    args = p.parse_args(argv)
    mask = [tuple(int(v) for v in m.split(",")) for m in args.mask]
    refs = [load(r, mask) for r in args.ref]
    cand = load(args.cand, mask)
    result = judge(refs, cand)
    print(json.dumps(result, indent=2))
    return 0 if result["pass"] else 1


if __name__ == "__main__":
    sys.exit(main())
