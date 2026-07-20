#!/usr/bin/env python3
"""Build compact optical-flow meshes for ghost-free runtime interpolation.

The authored 4K PNGs remain the only appearance source.  Motion files contain
only coarse displacement vectors; the standalone runtime warps exactly one
opaque key pose at a time, so an intermediate stage can never become two
cross-faded people.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
import sys
import zipfile
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


WORKSPACE = Path(__file__).resolve().parents[2]
VENDOR = WORKSPACE / ".build-tools" / "opencv"
if VENDOR.is_dir():
    sys.path.insert(0, str(VENDOR))
try:
    import cv2  # type: ignore
except Exception as exception:  # pragma: no cover - build-environment guard
    raise SystemExit(
        "OpenCV is required only while building motion meshes. Install "
        "opencv-python-headless into .build-tools/opencv. "
        f"Import failed: {exception}"
    )


LOGICAL_SIZE = (132, 202)
GRID_SIZE = (17, 25)
QUANTIZATION = 64.0
MAX_FLOW = 48.0
ROW_COUNTS = (
    7, 8, 8, 4, 5, 8, 6, 6, 6, 8, 8, 6,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
)
DIRECT_GAZE_ROWS = {9, 10}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build XiaoXiWei motion meshes")
    parser.add_argument("--frames-root", required=True, type=Path)
    parser.add_argument("--motion-root", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--qa-contact", required=True, type=Path)
    parser.add_argument("--archive", type=Path)
    return parser.parse_args()


def frame_path(root: Path, row: int, column: int) -> Path:
    return root / f"r{row:02d}" / f"c{column:02d}.png"


def motion_name(source: tuple[int, int], target: tuple[int, int]) -> str:
    return (
        f"r{source[0]:02d}/c{source[1]:02d}"
        f"-r{target[0]:02d}-c{target[1]:02d}.mtn"
    )


def expected_pairs() -> list[tuple[tuple[int, int], tuple[int, int]]]:
    pairs: set[tuple[tuple[int, int], tuple[int, int]]] = set()
    for row, count in enumerate(ROW_COUNTS):
        if row in DIRECT_GAZE_ROWS:
            continue
        for column in range(count):
            pairs.add(((row, column), (row, (column + 1) % count)))
            if row != 0:
                pairs.add(((row, column), (0, 0)))
    return sorted(pairs)


def load_logical(path: Path) -> np.ndarray:
    with Image.open(path) as opened:
        rgba = opened.convert("RGBA").resize(LOGICAL_SIZE, Image.Resampling.LANCZOS)
    return np.asarray(rgba, dtype=np.uint8)


def flow_input(rgba: np.ndarray) -> np.ndarray:
    alpha = rgba[..., 3:4].astype(np.float32) / 255.0
    rgb = rgba[..., :3].astype(np.float32)
    # A mid-gray transparent field gives both light and dark costume edges
    # enough contrast while avoiding a false high-contrast black halo.
    composite = rgb * alpha + 112.0 * (1.0 - alpha)
    gray = cv2.cvtColor(composite.astype(np.uint8), cv2.COLOR_RGB2GRAY)
    edge_alpha = rgba[..., 3]
    gray = cv2.addWeighted(gray, 0.72, edge_alpha, 0.28, 0.0)
    return gray


def dense_flow(source: np.ndarray, target: np.ndarray) -> np.ndarray:
    flow = cv2.calcOpticalFlowFarneback(
        flow_input(source),
        flow_input(target),
        None,
        0.5,
        5,
        25,
        5,
        7,
        1.5,
        0,
    )
    source_mask = (source[..., 3] > 8).astype(np.uint8)
    support = cv2.dilate(source_mask, np.ones((13, 13), np.uint8), iterations=1)
    flow = cv2.GaussianBlur(flow, (0, 0), 1.4)
    flow *= support[..., None]
    return np.clip(flow, -MAX_FLOW, MAX_FLOW).astype(np.float32)


def sample_mesh(flow: np.ndarray) -> np.ndarray:
    columns, rows = GRID_SIZE
    xs = np.linspace(0.0, LOGICAL_SIZE[0] - 1.0, columns, dtype=np.float32)
    ys = np.linspace(0.0, LOGICAL_SIZE[1] - 1.0, rows, dtype=np.float32)
    grid_x, grid_y = np.meshgrid(xs, ys)
    sampled_x = cv2.remap(
        flow[..., 0], grid_x, grid_y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT
    )
    sampled_y = cv2.remap(
        flow[..., 1], grid_x, grid_y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT
    )
    return np.stack((sampled_x, sampled_y), axis=-1)


def encode_mesh(forward: np.ndarray, backward: np.ndarray) -> bytes:
    columns, rows = GRID_SIZE
    header = struct.pack(
        "<4sHHHH", b"XWM1", columns, rows, LOGICAL_SIZE[0], LOGICAL_SIZE[1]
    )
    interleaved = np.empty((rows, columns, 4), dtype="<i2")
    interleaved[..., 0:2] = np.rint(forward * QUANTIZATION).astype("<i2")
    interleaved[..., 2:4] = np.rint(backward * QUANTIZATION).astype("<i2")
    return header + interleaved.tobytes(order="C")


def expand_mesh(mesh: np.ndarray) -> np.ndarray:
    return cv2.resize(mesh, LOGICAL_SIZE, interpolation=cv2.INTER_CUBIC)


def warp_single_source(rgba: np.ndarray, mesh: np.ndarray, strength: float) -> np.ndarray:
    flow = expand_mesh(mesh)
    height, width = rgba.shape[:2]
    grid_x, grid_y = np.meshgrid(
        np.arange(width, dtype=np.float32), np.arange(height, dtype=np.float32)
    )
    map_x = grid_x - flow[..., 0] * strength
    map_y = grid_y - flow[..., 1] * strength
    return cv2.remap(
        rgba,
        map_x,
        map_y,
        cv2.INTER_LINEAR,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0, 0),
    )


def alpha_metrics(image: np.ndarray) -> dict[str, object]:
    alpha = image[..., 3]
    visible = alpha > 16
    core = alpha >= 224
    components, labels = cv2.connectedComponents(visible.astype(np.uint8), connectivity=8)
    sizes = [int((labels == index).sum()) for index in range(1, components)]
    largest = max(sizes, default=0)
    return {
        "visiblePixels": int(visible.sum()),
        "opaqueCorePixels": int(core.sum()),
        "largestComponentCoverage": float(largest / max(1, int(visible.sum()))),
        "largeComponentCount": int(sum(size >= 16 for size in sizes)),
    }


def save_contact(
    samples: list[tuple[str, np.ndarray, np.ndarray, np.ndarray]],
    output: Path,
) -> None:
    scale = 2
    pane = (LOGICAL_SIZE[0] * scale, LOGICAL_SIZE[1] * scale)
    header = 42
    label_width = 150
    canvas = Image.new(
        "RGB",
        (label_width + pane[0] * 3, header + pane[1] * len(samples)),
        (31, 33, 36),
    )
    draw = ImageDraw.Draw(canvas)
    font_path = Path(r"C:\Windows\Fonts\arial.ttf")
    font = ImageFont.truetype(str(font_path), 17) if font_path.is_file() else ImageFont.load_default()
    draw.text((12, 12), "GHOST-FREE MOTION MESH: FROM / MIDPOINT / TO", fill=(245, 245, 245), font=font)
    for row, (label, source, midpoint, target) in enumerate(samples):
        top = header + row * pane[1]
        draw.text((10, top + pane[1] // 2), label, fill=(220, 225, 230), font=font)
        for column, rgba in enumerate((source, midpoint, target)):
            image = Image.fromarray(rgba, mode="RGBA").resize(pane, Image.Resampling.LANCZOS)
            left = label_width + column * pane[0]
            canvas.paste(image, (left, top), image)
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output, optimize=True)


def append_archive(archive_path: Path, motion_root: Path, manifest: dict[str, object]) -> None:
    with zipfile.ZipFile(archive_path, "a", compression=zipfile.ZIP_STORED) as archive:
        existing = set(archive.namelist())
        for path in sorted(motion_root.rglob("*.mtn")):
            name = "motion/" + path.relative_to(motion_root).as_posix()
            if name in existing:
                raise SystemExit(f"archive already contains {name}")
            archive.write(path, name)
        archive.writestr(
            "motion/manifest.json",
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        )


def main() -> int:
    args = parse_args()
    frames_root = args.frames_root.resolve()
    motion_root = args.motion_root.resolve()
    report_path = args.report.resolve()
    qa_contact = args.qa_contact.resolve()
    motion_root.mkdir(parents=True, exist_ok=True)

    cache: dict[tuple[int, int], np.ndarray] = {}
    records: list[dict[str, object]] = []
    samples: list[tuple[str, np.ndarray, np.ndarray, np.ndarray]] = []
    representative = {(12, 0), (14, 0), (16, 3), (18, 0), (23, 3)}

    def frame(key: tuple[int, int]) -> np.ndarray:
        if key not in cache:
            path = frame_path(frames_root, *key)
            if not path.is_file():
                raise SystemExit(f"missing frame for motion build: {path}")
            cache[key] = load_logical(path)
        return cache[key]

    for source_key, target_key in expected_pairs():
        source = frame(source_key)
        target = frame(target_key)
        forward_dense = dense_flow(source, target)
        backward_dense = dense_flow(target, source)
        forward = sample_mesh(forward_dense)
        backward = sample_mesh(backward_dense)
        payload = encode_mesh(forward, backward)
        relative = Path(motion_name(source_key, target_key))
        output = motion_root / relative
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(payload)

        # At the exact midpoint the runtime owns the target pose and warps it
        # halfway backward.  No source pose is composited into this bitmap.
        midpoint = warp_single_source(target, backward, 0.5)
        metrics = alpha_metrics(midpoint)
        record = {
            "source": list(source_key),
            "target": list(target_key),
            "entry": "motion/" + relative.as_posix(),
            "bytes": len(payload),
            "sha256": hashlib.sha256(payload).hexdigest(),
            "midpoint": metrics,
        }
        records.append(record)
        if source_key in representative and target_key[0] == source_key[0]:
            samples.append((f"r{source_key[0]:02d} c{source_key[1]:02d}", source, midpoint, target))

    minimum_core = min(int(record["midpoint"]["opaqueCorePixels"]) for record in records)
    minimum_component_coverage = min(
        float(record["midpoint"]["largestComponentCoverage"]) for record in records
    )
    manifest = {
        "formatVersion": 1,
        "algorithm": "single-owner bidirectional Farneback mesh warp",
        "logicalSize": list(LOGICAL_SIZE),
        "gridSize": list(GRID_SIZE),
        "quantization": QUANTIZATION,
        "pairCount": len(records),
        "singleSilhouette": True,
        "crossFade": False,
    }
    save_contact(samples, qa_contact)
    if args.archive:
        append_archive(args.archive.resolve(), motion_root, manifest)

    report = {
        "ok": minimum_core > 0 and minimum_component_coverage >= 0.70,
        "manifest": manifest,
        "minimumMidpointOpaqueCorePixels": minimum_core,
        "minimumMidpointLargestComponentCoverage": minimum_component_coverage,
        "qaContact": str(qa_contact),
        "records": records,
    }
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "ok": report["ok"],
        "pairs": len(records),
        "minimumOpaqueCore": minimum_core,
        "minimumLargestComponentCoverage": minimum_component_coverage,
        "report": str(report_path),
    }, ensure_ascii=False))
    if not report["ok"]:
        raise SystemExit("motion mesh QA failed; inspect report")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
