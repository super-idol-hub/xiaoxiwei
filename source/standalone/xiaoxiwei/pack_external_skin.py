#!/usr/bin/env python3
"""Validate and package a XiaoXiWei external skin from rebuilt 4K frames."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import zipfile
from pathlib import Path
from xml.sax.saxutils import quoteattr

from PIL import Image


FRAME_SIZE = (528, 808)
MAX_ARCHIVE_BYTES = 128 * 1024 * 1024
MAX_ENTRY_BYTES = 32 * 1024 * 1024
MAX_TOTAL_UNCOMPRESSED_BYTES = 128 * 1024 * 1024
ROW_COUNTS = (
    7, 8, 8, 4, 5, 8, 6, 6, 6, 8, 8, 6,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
)
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Package a validated XiaoXiWei skin.")
    parser.add_argument("--frames-root", required=True, type=Path)
    parser.add_argument("--motion-root", type=Path)
    parser.add_argument("--output-root", required=True, type=Path)
    parser.add_argument("--id", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--developer", default="Anbunensi")
    parser.add_argument(
        "--exclusive-action",
        default="",
        help="Optional apiVersion=1 opt-in label for the reserved r15 skin-exclusive action.",
    )
    parser.add_argument(
        "--include-linan-swing-exit",
        action="store_true",
        help=(
            "Package the optional r15/c05 -> r15/c02 loop-wrap mesh and "
            "r15/c02 -> r15/c06 click-exit mesh for the persistent swing."
        ),
    )
    parser.add_argument("--report", type=Path)
    return parser.parse_args()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def expected_paths() -> list[str]:
    return [
        f"r{row:02d}/c{column:02d}.png"
        for row, count in enumerate(ROW_COUNTS)
        for column in range(count)
    ]


def expected_motion_paths(
    include_linan_swing_exit: bool = False,
) -> list[str]:
    pairs: set[tuple[tuple[int, int], tuple[int, int]]] = set()
    for row, count in enumerate(ROW_COUNTS):
        if row in (9, 10):
            continue
        for column in range(count):
            pairs.add(((row, column), (row, (column + 1) % count)))
            if row != 0:
                pairs.add(((row, column), (0, 0)))
    if include_linan_swing_exit:
        pairs.add(((15, 5), (15, 2)))
        pairs.add(((15, 2), (15, 6)))
    return [
        f"motion/r{source[0]:02d}/c{source[1]:02d}-r{target[0]:02d}-c{target[1]:02d}.mtn"
        for source, target in sorted(pairs)
    ]


def validate_text(value: str, label: str) -> str:
    value = value.strip()
    if not value or len(value) > 64:
        raise SystemExit(f"{label} must contain 1-64 characters")
    return value


def main() -> int:
    args = parse_args()
    skin_id = args.id.strip()
    if not SAFE_ID.fullmatch(skin_id):
        raise SystemExit("skin id is unsafe")
    name = validate_text(args.name, "name")
    developer = validate_text(args.developer, "developer")
    exclusive_action = args.exclusive_action.strip()
    if exclusive_action and len(exclusive_action) > 64:
        raise SystemExit("exclusive action must contain at most 64 characters")

    frames_root = args.frames_root.resolve()
    if not frames_root.is_dir():
        raise SystemExit(f"frames root not found: {frames_root}")
    motion_root = args.motion_root.resolve() if args.motion_root else None
    if motion_root is not None and not motion_root.is_dir():
        raise SystemExit(f"motion root not found: {motion_root}")
    output_root = args.output_root.resolve()
    skin_dir = output_root / skin_id
    skin_dir.mkdir(parents=True, exist_ok=True)

    expected = expected_paths()
    expected_set = set(expected)
    actual = {
        path.relative_to(frames_root).as_posix()
        for path in frames_root.rglob("*.png")
        if path.is_file()
    }
    missing = sorted(expected_set - actual)
    extra = sorted(actual - expected_set)
    if missing or extra:
        raise SystemExit(
            f"frame contract mismatch: missing={len(missing)}, extra={len(extra)}"
        )

    frames: list[dict[str, object]] = []
    total_uncompressed_bytes = 0
    for relative in expected:
        path = frames_root / Path(relative)
        with Image.open(path) as image:
            rgba = image.convert("RGBA")
            if rgba.size != FRAME_SIZE:
                raise SystemExit(f"wrong frame dimensions: {relative} -> {rgba.size}")
            if rgba.getchannel("A").getbbox() is None:
                raise SystemExit(f"empty frame: {relative}")
        frame_bytes = path.stat().st_size
        if frame_bytes <= 0 or frame_bytes > MAX_ENTRY_BYTES:
            raise SystemExit(
                f"frame entry size is outside the runtime limit: {relative} -> {frame_bytes}"
            )
        total_uncompressed_bytes += frame_bytes
        if total_uncompressed_bytes > MAX_TOTAL_UNCOMPRESSED_BYTES:
            raise SystemExit(
                "frame archive expands beyond the runtime safety limit: "
                f"{total_uncompressed_bytes} bytes"
            )
        frames.append(
            {
                "entry": relative,
                "bytes": frame_bytes,
                "sha256": sha256(path),
            }
        )

    motion_entries: list[dict[str, object]] = []
    motion_paths = (
        expected_motion_paths(args.include_linan_swing_exit)
        if motion_root is not None
        else []
    )
    for entry in motion_paths:
        relative = entry.removeprefix("motion/")
        path = motion_root / Path(relative)
        if not path.is_file():
            raise SystemExit(f"missing motion mesh: {entry}")
        payload = path.read_bytes()
        if len(payload) < 12 or payload[:4] != b"XWM1":
            raise SystemExit(f"invalid motion mesh header: {entry}")
        total_uncompressed_bytes += len(payload)
        if total_uncompressed_bytes > MAX_TOTAL_UNCOMPRESSED_BYTES:
            raise SystemExit("frame and motion archive expands beyond the runtime safety limit")
        motion_entries.append(
            {
                "entry": entry,
                "bytes": len(payload),
                "sha256": hashlib.sha256(payload).hexdigest(),
            }
        )

    archive_path = skin_dir / "frames.zip"
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_STORED) as archive:
        for relative in expected:
            archive.write(frames_root / Path(relative), relative)
        for entry in motion_paths:
            archive.write(motion_root / Path(entry.removeprefix("motion/")), entry)

    archive_bytes = archive_path.stat().st_size
    if archive_bytes <= 0 or archive_bytes > MAX_ARCHIVE_BYTES:
        archive_path.unlink(missing_ok=True)
        raise SystemExit(
            f"written archive is outside the runtime size limit: {archive_bytes} bytes"
        )

    with zipfile.ZipFile(archive_path, "r") as archive:
        infos = archive.infolist()
        names = [info.filename for info in infos]
        expected_archive_names = expected + motion_paths
        if names != expected_archive_names:
            raise SystemExit("written archive order or entries do not match the skin contract")
        if any("\\" in name or name.startswith("/") or "../" in name for name in names):
            raise SystemExit("written archive contains an unsafe path")
        if any(info.file_size <= 0 or info.file_size > MAX_ENTRY_BYTES for info in infos):
            raise SystemExit("written archive contains an entry outside the runtime size limit")
        written_uncompressed_bytes = sum(info.file_size for info in infos)
        if written_uncompressed_bytes > MAX_TOTAL_UNCOMPRESSED_BYTES:
            raise SystemExit("written archive expands beyond the runtime safety limit")
        if written_uncompressed_bytes != total_uncompressed_bytes:
            raise SystemExit("written archive size metadata does not match the source frames")

    manifest_path = skin_dir / "skin.xml"
    exclusive_attribute = (
        " exclusiveAction=" + quoteattr(exclusive_action)
        if exclusive_action
        else ""
    )
    manifest = (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<skin apiVersion="1" id={id} name={name} developer={developer} archive="frames.zip"{exclusive} />\n'
    ).format(
        id=quoteattr(skin_id),
        # quoteattr performs XML escaping itself.  Escaping first would turn
        # an ampersand into ``&amp;amp;`` and change the user-visible metadata.
        name=quoteattr(name),
        developer=quoteattr(developer),
        exclusive=exclusive_attribute,
    )
    manifest_path.write_text(manifest, encoding="utf-8", newline="\n")

    report = {
        "schemaVersion": 1,
        "ok": True,
        "id": skin_id,
        "name": name,
        "developer": developer,
        "exclusiveAction": exclusive_action or None,
        "linanSwingLoopWrapMesh": args.include_linan_swing_exit,
        "linanSwingExitMesh": args.include_linan_swing_exit,
        "frameCount": len(expected),
        "motionPairCount": len(motion_paths),
        "ghostFreeMotionMeshes": bool(motion_paths),
        "frameSize": list(FRAME_SIZE),
        "archive": str(archive_path),
        "archiveBytes": archive_bytes,
        "archiveUncompressedBytes": written_uncompressed_bytes,
        "archiveSha256": sha256(archive_path),
        "manifest": str(manifest_path),
        "manifestSha256": sha256(manifest_path),
        "frames": frames,
        "motion": motion_entries,
        "errors": [],
    }
    report_path = args.report.resolve() if args.report else skin_dir / "skin-pack-report.json"
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    print(
        json.dumps(
            {
                "ok": True,
                "skin": str(skin_dir),
                "frames": len(expected),
                "archiveBytes": archive_bytes,
                "sha256": report["archiveSha256"],
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
