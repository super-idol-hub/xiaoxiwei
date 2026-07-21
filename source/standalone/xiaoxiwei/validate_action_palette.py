#!/usr/bin/env python3
"""Guard the built-in flying-kiss row against generated hair-colour drift."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image


FLYING_KISS_ROW = 21
FRAME_COUNT = 8
MINIMUM_DARK_SAMPLES = 500
MAXIMUM_WARM_FRACTION_SPREAD = 0.055
MAXIMUM_COLUMN_DEVIATION = 0.045


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--frames-root", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    return parser.parse_args()


def frame_signature(path: Path) -> dict[str, object]:
    with Image.open(path) as opened:
        rgba = np.asarray(opened.convert("RGBA"), dtype=np.uint8)
    alpha = rgba[:, :, 3]
    visible_y, visible_x = np.where(alpha > 32)
    if not visible_y.size:
        raise ValueError(f"empty frame: {path}")

    y0 = int(visible_y.min())
    y1 = int(visible_y.max()) + 1
    x0 = int(visible_x.min())
    x1 = int(visible_x.max()) + 1
    upper_body = np.zeros(alpha.shape, dtype=bool)
    upper_body[y0 : int(y0 + (y1 - y0) * 0.55), x0:x1] = True

    rgb = rgba[:, :, :3].astype(np.int16)
    dark_hair_candidates = (
        upper_body
        & (alpha > 200)
        & (rgb.max(axis=2) < 145)
        & (rgb.mean(axis=2) < 105)
    )
    samples = rgb[dark_hair_candidates]
    if samples.shape[0] < MINIMUM_DARK_SAMPLES:
        raise ValueError(
            f"too few upper-body dark samples in {path}: {samples.shape[0]}"
        )

    warmth = samples[:, 0] - (samples[:, 1] + samples[:, 2]) / 2.0
    warm_pixels = (
        (samples[:, 0] > samples[:, 1] + 18)
        & (samples[:, 0] > samples[:, 2] + 14)
        & (samples[:, 0] > 65)
    )
    return {
        "path": str(path.resolve()),
        "darkSampleCount": int(samples.shape[0]),
        "medianDarkRgb": [float(value) for value in np.median(samples, axis=0)],
        "medianWarmth": float(np.median(warmth)),
        "warmPixelFraction": float(np.mean(warm_pixels)),
    }


def main() -> int:
    args = parse_args()
    root = args.frames_root.expanduser().resolve()
    frames = [
        frame_signature(root / f"r{FLYING_KISS_ROW:02d}" / f"c{column:02d}.png")
        for column in range(FRAME_COUNT)
    ]
    fractions = np.asarray(
        [float(frame["warmPixelFraction"]) for frame in frames],
        dtype=np.float64,
    )
    median_fraction = float(np.median(fractions))
    deviations = np.abs(fractions - median_fraction)
    offending = [
        int(column)
        for column, deviation in enumerate(deviations)
        if float(deviation) > MAXIMUM_COLUMN_DEVIATION
    ]
    spread = float(fractions.max() - fractions.min())
    ok = spread <= MAXIMUM_WARM_FRACTION_SPREAD and not offending
    report = {
        "ok": ok,
        "row": FLYING_KISS_ROW,
        "frameCount": FRAME_COUNT,
        "method": "upper-body dark-pixel warm-fraction consistency",
        "warmPixelFractionMedian": median_fraction,
        "warmPixelFractionSpread": spread,
        "maximumWarmPixelFractionSpread": MAXIMUM_WARM_FRACTION_SPREAD,
        "maximumColumnDeviation": MAXIMUM_COLUMN_DEVIATION,
        "offendingColumns": offending,
        "frames": frames,
    }
    report_path = args.report.expanduser().resolve()
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(report, ensure_ascii=False))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
