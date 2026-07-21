#!/usr/bin/env python3
"""Render deterministic GIF QA for XiaoXiWeiPet runtime tweening.

The executable keeps compact 528x808 authored keyframes and synthesizes its
in-between display stages at runtime.  This script mirrors that presentation
path closely enough for visual review without launching the layered WinForms
window:

* smoothstep opacity blending;
* 38% bottom-centre silhouette alignment;
* at least 24 displayed tween stages per cycle or persistent-action segment;
* the sitting phone loop and side-rest entry/sleep/wake contracts.

Only Pillow and NumPy are required beyond the Python standard library.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, Iterator, List, Sequence, Tuple

import numpy as np
from PIL import Image, ImageDraw, ImageFont


SOURCE_SIZE = (528, 808)
QA_SIZE = (264, 404)
BACKGROUND = (31, 33, 37, 255)
MINIMUM_STAGES = 24
TIMING_REFERENCE_STAGES = 56
MINIMUM_TICK_MS = 16
ALIGNMENT_STRENGTH = 0.38
ALPHA_BOUNDS_THRESHOLD = 4

# This is the executable's authored-keyframe contract, not merely a directory
# listing.  Row 0 contains one spare PNG and gaze rows 9-10 are direct poses.
RUNTIME_FRAME_COUNTS = (
    6, 8, 8, 4, 5, 8, 6, 6, 6, 0, 0, 6,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
)

FRAME_DURATIONS_MS: Tuple[Tuple[int, ...], ...] = (
    (280, 110, 110, 140, 140, 320),
    (120, 120, 120, 120, 120, 120, 120, 220),
    (120, 120, 120, 120, 120, 120, 120, 220),
    (140, 140, 140, 280),
    (140, 140, 140, 140, 280),
    (140, 140, 140, 140, 140, 140, 140, 240),
    (150, 150, 150, 150, 150, 260),
    (120, 120, 120, 120, 120, 220),
    (150, 150, 150, 150, 150, 280),
    (120,),
    (120,),
    (420, 320, 240, 170, 190, 1100),
    (220, 140, 180, 240, 260, 180, 160, 260),
    (190, 150, 170, 170, 250, 170, 170, 220),
    (220, 180, 220, 260, 300, 240, 200, 260),
    (220, 180, 180, 220, 260, 200, 180, 260),
    (70, 70, 70, 70, 70, 70, 70, 70),
    (70, 70, 70, 70, 70, 70, 70, 70),
    (150, 150, 150, 150, 150, 150, 150, 150),
    (200, 200, 200, 200, 200, 200, 200, 200),
    (150, 150, 150, 150, 150, 150, 150, 150),
    (180, 180, 180, 180, 180, 180, 180, 180),
    (220, 220, 220, 220, 220, 220, 220, 220),
    (220, 220, 220, 220, 220, 220, 220, 220),
)


@dataclass(frozen=True)
class FrameRef:
    row: int
    column: int


@dataclass(frozen=True)
class RegularAnimation:
    animation_id: str
    label: str
    row: int


BASE_REGULAR_ANIMATIONS: Tuple[RegularAnimation, ...] = (
    RegularAnimation("idle", "Idle", 0),
    RegularAnimation("running-right", "Legacy run right", 1),
    RegularAnimation("running-left", "Legacy run left", 2),
    RegularAnimation("waving", "Wave", 3),
    RegularAnimation("jumping", "Jump", 4),
    RegularAnimation("failed", "Failed / comfort", 5),
    RegularAnimation("waiting", "Waiting", 6),
    RegularAnimation("working", "Working", 7),
    RegularAnimation("review", "Review", 8),
    RegularAnimation("angry-stomp", "Angry stomp", 11),
)

BUILTIN_EXTENSION_ANIMATIONS: Tuple[RegularAnimation, ...] = (
    RegularAnimation("adorable", "Built-in adorable", 12),
    RegularAnimation("laughing", "Built-in laughing", 13),
    RegularAnimation("crying", "Built-in crying", 14),
    RegularAnimation("builtin-exclusive", "White-dress starlight reveal", 15),
)

EXTERNAL_EXTENSION_ANIMATIONS: Tuple[RegularAnimation, ...] = (
    RegularAnimation("skin-exclusive", "External skin exclusive action", 15),
)

PUBLIC_IDLE_ANIMATIONS: Tuple[RegularAnimation, ...] = (
    RegularAnimation("handdance", "Hand dance", 18),
    RegularAnimation("singing", "Singing", 19),
    RegularAnimation("heroine", "Heroine acting", 20),
    RegularAnimation("flyingkiss", "Flying kiss", 21),
)

CONTACT_SHEET_SAMPLE_COUNT = 8


def parse_args() -> argparse.Namespace:
    project_root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(
        description="Render dark-background GIFs that mirror XiaoXiWeiPet runtime smoothing."
    )
    parser.add_argument(
        "--frames-root",
        type=Path,
        default=project_root / "work" / "xiaoxiwei" / "standalone-4k" / "frames",
        help="Directory containing rNN/cNN.png 528x808 keyframes.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=project_root / "work" / "xiaoxiwei" / "standalone-4k" / "runtime-smoothing-qa",
        help="Destination for GIFs and runtime-smoothing-qa-report.json.",
    )
    parser.add_argument(
        "--action-profile",
        choices=(
            "built-in-v305", "built-in-v306", "external-v304",
            "external-v306", "external-v306-linan",
        ),
        default="built-in-v306",
        help=(
            "Select the runtime-visible r12-r15 action mapping for QA labels. "
            "external-v306-linan also renders the manual persistent swing entry, "
            "loop, and click-to-dismount contract."
        ),
    )
    return parser.parse_args()


def steps_per_transition(transition_count: int) -> int:
    if transition_count <= 1:
        return 1
    return max(1, math.ceil(MINIMUM_STAGES / transition_count))


def smoothstep(step: int, steps: int) -> float:
    if step <= 0 or steps <= 1:
        return 0.0
    if step >= steps:
        return 1.0
    linear = step / float(steps)
    return linear * linear * (3.0 - 2.0 * linear)


def gif_duration(milliseconds: int) -> int:
    """Round to GIF's 10 ms clock while preserving the runtime's pacing."""
    return max(20, int(round(milliseconds / 10.0)) * 10)


def visible_bounds(image: Image.Image) -> Tuple[int, int, int, int]:
    alpha = np.asarray(image.getchannel("A"), dtype=np.uint8)
    ys, xs = np.nonzero(alpha > ALPHA_BOUNDS_THRESHOLD)
    if xs.size == 0:
        return (0, 0, 0, 0)
    return (int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1)


def silhouette_anchor(bounds: Tuple[int, int, int, int], size: Tuple[int, int]) -> Tuple[float, float]:
    left, top, right, bottom = bounds
    if right <= left or bottom <= top:
        return (size[0] * 0.5, size[1] * 0.9)
    return (left + (right - left) * 0.5, float(bottom))


class FrameStore:
    def __init__(self, frames_root: Path) -> None:
        self.frames_root = frames_root
        self._images: Dict[FrameRef, Image.Image] = {}
        self._bounds: Dict[FrameRef, Tuple[int, int, int, int]] = {}

    def path_for(self, ref: FrameRef) -> Path:
        return self.frames_root / f"r{ref.row:02d}" / f"c{ref.column:02d}.png"

    def get(self, ref: FrameRef) -> Image.Image:
        cached = self._images.get(ref)
        if cached is not None:
            return cached
        source_path = self.path_for(ref)
        if not source_path.is_file():
            raise FileNotFoundError(f"Missing runtime keyframe: {source_path}")
        with Image.open(source_path) as source:
            rgba = source.convert("RGBA")
            if rgba.size != SOURCE_SIZE:
                raise ValueError(
                    f"Unexpected keyframe size for {source_path}: {rgba.size}; expected {SOURCE_SIZE}"
                )
            # GDI+ uses HighQualityBicubic before it synthesizes tween frames.
            scaled = rgba.resize(QA_SIZE, Image.Resampling.BICUBIC)
        self._images[ref] = scaled
        self._bounds[ref] = visible_bounds(scaled)
        return scaled

    def bounds(self, ref: FrameRef) -> Tuple[int, int, int, int]:
        self.get(ref)
        return self._bounds[ref]


def with_opacity(image: Image.Image, opacity: float) -> Image.Image:
    if opacity >= 0.999:
        return image
    if opacity <= 0.001:
        return Image.new("RGBA", image.size, (0, 0, 0, 0))
    result = image.copy()
    alpha = np.asarray(image.getchannel("A"), dtype=np.float32)
    scaled_alpha = np.clip(np.rint(alpha * opacity), 0, 255).astype(np.uint8)
    result.putalpha(Image.fromarray(scaled_alpha, mode="L"))
    return result


def place(canvas: Image.Image, image: Image.Image, x: int, y: int) -> None:
    """Alpha composite with clipping, including small negative tween offsets."""
    source_left = max(0, -x)
    source_top = max(0, -y)
    source_right = min(image.width, canvas.width - x)
    source_bottom = min(image.height, canvas.height - y)
    if source_right <= source_left or source_bottom <= source_top:
        return
    crop = image.crop((source_left, source_top, source_right, source_bottom))
    canvas.alpha_composite(crop, (max(0, x), max(0, y)))


def tween_frame(store: FrameStore, source_ref: FrameRef, target_ref: FrameRef, amount: float) -> Image.Image:
    source = store.get(source_ref)
    target = store.get(target_ref)
    source_anchor = silhouette_anchor(store.bounds(source_ref), source.size)
    target_anchor = silhouette_anchor(store.bounds(target_ref), target.size)
    moving_anchor = (
        source_anchor[0] + (target_anchor[0] - source_anchor[0]) * amount,
        source_anchor[1] + (target_anchor[1] - source_anchor[1]) * amount,
    )
    source_offset = (
        int(round((moving_anchor[0] - source_anchor[0]) * ALIGNMENT_STRENGTH)),
        int(round((moving_anchor[1] - source_anchor[1]) * ALIGNMENT_STRENGTH)),
    )
    target_offset = (
        int(round((moving_anchor[0] - target_anchor[0]) * ALIGNMENT_STRENGTH)),
        int(round((moving_anchor[1] - target_anchor[1]) * ALIGNMENT_STRENGTH)),
    )

    composite = Image.new("RGBA", source.size, (0, 0, 0, 0))
    place(composite, with_opacity(source, 1.0 - amount), *source_offset)
    place(composite, with_opacity(target, amount), *target_offset)
    return composite


def transition_timing(
    row: int,
    column: int,
    steps: int,
    transition_count_for_smoothing: int,
) -> Tuple[List[int], int]:
    durations = FRAME_DURATIONS_MS[row]
    authored = durations[min(column, len(durations) - 1)]
    reference_steps = max(1, math.ceil(TIMING_REFERENCE_STAGES / transition_count_for_smoothing))
    if authored >= 400:
        reference_interval = MINIMUM_TICK_MS
    else:
        reference_interval = max(
            MINIMUM_TICK_MS,
            int(round(authored / float(reference_steps))),
        )
    motion_duration = reference_interval * reference_steps
    hold_candidate = authored - motion_duration
    hold = hold_candidate if hold_candidate >= MINIMUM_TICK_MS else 0
    step_delays = [
        ((step + 1) * motion_duration // steps) - (step * motion_duration // steps)
        for step in range(steps)
    ]
    return step_delays, hold


def render_transitions(
    store: FrameStore,
    transitions: Sequence[Tuple[FrameRef, FrameRef]],
    transition_count_for_smoothing: int,
) -> Tuple[List[Image.Image], List[int], int]:
    steps = steps_per_transition(transition_count_for_smoothing)
    frames: List[Image.Image] = []
    durations: List[int] = []
    for source_ref, target_ref in transitions:
        step_delays, hold = transition_timing(
            source_ref.row,
            source_ref.column,
            steps,
            transition_count_for_smoothing,
        )
        for step in range(steps):
            frames.append(tween_frame(store, source_ref, target_ref, smoothstep(step, steps)))
            durations.append(gif_duration(step_delays[step] + (hold if step == 0 else 0)))
    return frames, durations, steps


def over_background(frame: Image.Image) -> Image.Image:
    background = Image.new("RGBA", frame.size, BACKGROUND)
    background.alpha_composite(frame)
    return background.convert("RGB")


def build_palette(store: FrameStore, references: Iterable[FrameRef]) -> Image.Image:
    # A shared palette prevents independent-frame palette shimmer in the GIFs.
    unique = sorted(set(references), key=lambda ref: (ref.row, ref.column))
    thumb_size = (66, 101)
    columns = 8
    rows = max(1, math.ceil(len(unique) / columns))
    atlas = Image.new("RGB", (columns * thumb_size[0], rows * thumb_size[1]), BACKGROUND[:3])
    for index, ref in enumerate(unique):
        frame = over_background(store.get(ref)).resize(thumb_size, Image.Resampling.BICUBIC)
        atlas.paste(frame, ((index % columns) * thumb_size[0], (index // columns) * thumb_size[1]))
    return atlas.quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)


def save_gif(
    output_path: Path,
    rgba_frames: Sequence[Image.Image],
    durations: Sequence[int],
    palette: Image.Image,
) -> str:
    if not rgba_frames:
        raise ValueError(f"No frames supplied for {output_path.name}")
    if len(rgba_frames) != len(durations):
        raise ValueError(f"Frame/duration mismatch for {output_path.name}")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    paletted = [
        over_background(frame).quantize(palette=palette, dither=Image.Dither.NONE)
        for frame in rgba_frames
    ]
    paletted[0].save(
        output_path,
        format="GIF",
        save_all=True,
        append_images=paletted[1:],
        duration=list(durations),
        loop=0,
        disposal=1,
        optimize=False,
    )
    return hashlib.sha256(output_path.read_bytes()).hexdigest()


def evenly_spaced_indices(frame_count: int, sample_count: int) -> List[int]:
    if frame_count <= 0:
        raise ValueError("Cannot sample an empty animation")
    if sample_count <= 0:
        raise ValueError("Sample count must be positive")
    # Treat animations as cycles/continuous timelines and sample the start of
    # each equal-width interval.  This avoids repeating the first pose in the
    # final column while remaining deterministic for 24/25- and 60-stage rows.
    return [min(frame_count - 1, (index * frame_count) // sample_count) for index in range(sample_count)]


def remember_contact_sheet_samples(
    destination: Dict[str, Dict[str, object]],
    animation_id: str,
    label: str,
    frames: Sequence[Image.Image],
    selected_ids: Sequence[str],
) -> None:
    if animation_id not in selected_ids:
        return
    indices = evenly_spaced_indices(len(frames), CONTACT_SHEET_SAMPLE_COUNT)
    destination[animation_id] = {
        "label": label,
        "stageCount": len(frames),
        "indices": indices,
        # Copies ensure contact-sheet annotation/compositing can never mutate
        # either cached source keyframes or the frames passed to save_gif().
        "frames": [frames[index].copy() for index in indices],
    }


def save_contact_sheet(
    output_path: Path,
    sampled: Dict[str, Dict[str, object]],
    animation_ids: Sequence[str],
) -> Dict[str, object]:
    missing = [animation_id for animation_id in animation_ids if animation_id not in sampled]
    if missing:
        raise ValueError("Missing contact-sheet animations: " + ", ".join(missing))

    row_label_width = 176
    cell_width = 132
    image_height = 202
    stage_label_height = 28
    header_height = 38
    row_height = image_height + stage_label_height
    sheet_size = (
        row_label_width + CONTACT_SHEET_SAMPLE_COUNT * cell_width,
        header_height + len(animation_ids) * row_height,
    )
    sheet = Image.new("RGB", sheet_size, BACKGROUND[:3])
    draw = ImageDraw.Draw(sheet)
    header_font = font_for_size(18)
    row_font = font_for_size(16)
    stage_font = font_for_size(12)
    muted = (158, 168, 181)
    bright = (235, 240, 246)
    divider = (58, 63, 71)

    draw.text((12, 8), "Runtime smoothing QA - 8 evenly spaced stages", font=header_font, fill=bright)
    for row_index, animation_id in enumerate(animation_ids):
        item = sampled[animation_id]
        label = str(item["label"])
        stage_count = int(item["stageCount"])
        indices = list(item["indices"])
        frames = list(item["frames"])
        row_y = header_height + row_index * row_height
        draw.line((0, row_y, sheet.width, row_y), fill=divider, width=1)
        draw.text((12, row_y + 14), label, font=row_font, fill=bright)
        draw.text((12, row_y + 40), f"{stage_count} total stages", font=stage_font, fill=muted)

        for sample_index, (stage_index, frame) in enumerate(zip(indices, frames)):
            cell_x = row_label_width + sample_index * cell_width
            thumbnail = over_background(frame).resize((cell_width, image_height), Image.Resampling.BICUBIC)
            sheet.paste(thumbnail, (cell_x, row_y))
            draw.rectangle(
                (cell_x, row_y, cell_x + cell_width - 1, row_y + image_height - 1),
                outline=divider,
                width=1,
            )
            stage_text = f"stage {stage_index + 1:03d}/{stage_count:03d}"
            text_box = draw.textbbox((0, 0), stage_text, font=stage_font)
            text_width = text_box[2] - text_box[0]
            draw.text(
                (cell_x + (cell_width - text_width) // 2, row_y + image_height + 6),
                stage_text,
                font=stage_font,
                fill=muted,
            )

    draw.line((0, sheet.height - 1, sheet.width, sheet.height - 1), fill=divider, width=1)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output_path, format="PNG", optimize=False, compress_level=9)
    digest = hashlib.sha256(output_path.read_bytes()).hexdigest()
    return {
        "outputPath": str(output_path.resolve()),
        "sha256": digest,
        "size": list(sheet.size),
        "sampleCountPerAnimation": CONTACT_SHEET_SAMPLE_COUNT,
        "animationOrder": list(animation_ids),
        "sampledStageIndicesZeroBased": {
            animation_id: list(sampled[animation_id]["indices"])
            for animation_id in animation_ids
        },
    }


def regular_transitions(row: int) -> List[Tuple[FrameRef, FrameRef]]:
    count = RUNTIME_FRAME_COUNTS[row]
    refs = [FrameRef(row, column) for column in range(count)]
    return [(refs[index], refs[(index + 1) % count]) for index in range(count)]


def font_for_size(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    try:
        return ImageFont.truetype("DejaVuSans-Bold.ttf", size=size)
    except OSError:
        return ImageFont.load_default()


def add_sleep_particles(
    store: FrameStore,
    base_ref: FrameRef,
    particle_stage: int,
) -> Image.Image:
    base = store.get(base_ref).copy()
    bounds = store.bounds(base_ref)
    left, top, right, bottom = bounds
    if right <= left or bottom <= top:
        emitter = (QA_SIZE[0] * (54.0 / 132.0), QA_SIZE[1] * (88.0 / 202.0))
    else:
        emitter = (left + (right - left) * 0.24, top + (bottom - top) * 0.08)

    draw = ImageDraw.Draw(base, "RGBA")
    scale_x = QA_SIZE[0] / 132.0
    scale_y = QA_SIZE[1] / 202.0
    effect_scale = max(0.75, min(scale_x, scale_y))
    glyphs = ("z", "Z", "z")
    logical_sizes = (8.5, 11.5, 9.5)
    for index, glyph in enumerate(glyphs):
        phase = ((particle_stage / 56.0) + index * 0.34) % 1.0
        alpha = max(0, min(230, int(round(230.0 * ((1.0 - phase) ** 1.35)))))
        font = font_for_size(max(8, int(round(logical_sizes[index] * effect_scale))))
        text_box = draw.textbbox((0, 0), glyph, font=font, stroke_width=0)
        text_width = text_box[2] - text_box[0]
        text_height = text_box[3] - text_box[1]
        x = emitter[0] + index * 3.2 * effect_scale + phase * 7.0 * effect_scale
        y = emitter[1] - text_height * 0.88 - phase * 42.0 * scale_y - index * 1.6 * effect_scale
        x = max(1.0, min(x, base.width - text_width - 1.0))
        y = max(1.0, min(y, base.height - text_height - 1.0))
        glow_offset = max(1.0, effect_scale * 0.65)
        draw.text(
            (x + glow_offset, y + glow_offset),
            glyph,
            font=font,
            fill=(90, 186, 238, alpha // 2),
        )
        face = (219, 246, 255, alpha) if index % 2 == 0 else (255, 255, 255, alpha)
        draw.text((x, y), glyph, font=font, fill=face)
    return base


def report_entry(
    animation_id: str,
    label: str,
    output_path: Path,
    tween_stage_count: int,
    total_frames: int,
    durations: Sequence[int],
    steps: int | Sequence[int],
    sha256: str,
    rows: Sequence[int],
    segments: Dict[str, int] | None = None,
) -> Dict[str, object]:
    entry: Dict[str, object] = {
        "id": animation_id,
        "label": label,
        "rows": list(rows),
        "stepsPerTransition": steps,
        "tweenStageCount": tween_stage_count,
        "totalGifFrames": total_frames,
        "gifPreviewDurationMs": int(sum(durations)),
        "outputPath": str(output_path.resolve()),
        "sha256": sha256,
    }
    if segments is not None:
        entry["segments"] = segments
    return entry


def validate_contract(frames_root: Path) -> None:
    if not frames_root.is_dir():
        raise NotADirectoryError(f"Frames root does not exist: {frames_root}")
    if len(RUNTIME_FRAME_COUNTS) != len(FRAME_DURATIONS_MS):
        raise RuntimeError("Frame-count and duration contracts have different row counts")
    for row, count in enumerate(RUNTIME_FRAME_COUNTS):
        if row in (9, 10):
            continue
        if count <= 1:
            raise RuntimeError(f"Animated row {row} has an invalid keyframe count: {count}")
        if len(FRAME_DURATIONS_MS[row]) < count:
            raise RuntimeError(f"Animated row {row} has too few authored durations")


def main() -> int:
    args = parse_args()
    frames_root = args.frames_root.expanduser().resolve()
    output_dir = args.output_dir.expanduser().resolve()
    if args.action_profile in ("built-in-v305", "built-in-v306"):
        extension_animations = BUILTIN_EXTENSION_ANIMATIONS
    elif args.action_profile == "external-v306-linan":
        # Lin'an's r15 is a persistent state with a custom frame sequence,
        # rendered separately below instead of as a one-shot 0..7 cycle.
        extension_animations = ()
    else:
        extension_animations = EXTERNAL_EXTENSION_ANIMATIONS
    regular_animations = BASE_REGULAR_ANIMATIONS + extension_animations + PUBLIC_IDLE_ANIMATIONS
    if args.action_profile in ("built-in-v305", "built-in-v306"):
        contact_sheet_ids = (
            "adorable", "laughing", "crying", "builtin-exclusive",
            "flyingkiss", "sitting-phone-loop", "side-rest-entry-wake",
        )
    elif args.action_profile == "external-v306-linan":
        contact_sheet_ids = (
            "linan-swing", "flyingkiss", "sitting-phone-loop", "side-rest-entry-wake",
        )
    else:
        contact_sheet_ids = (
            "skin-exclusive", "flyingkiss", "sitting-phone-loop", "side-rest-entry-wake",
        )
    validate_contract(frames_root)
    output_dir.mkdir(parents=True, exist_ok=True)
    store = FrameStore(frames_root)

    all_refs: List[FrameRef] = []
    for animation in regular_animations:
        all_refs.extend(FrameRef(animation.row, column) for column in range(RUNTIME_FRAME_COUNTS[animation.row]))
    all_refs.extend(FrameRef(22, column) for column in range(8))
    all_refs.extend(FrameRef(23, column) for column in range(8))
    all_refs.append(FrameRef(0, 0))
    palette = build_palette(store, all_refs)

    entries: List[Dict[str, object]] = []
    contact_samples: Dict[str, Dict[str, object]] = {}
    for animation in regular_animations:
        transitions = regular_transitions(animation.row)
        frames, durations, steps = render_transitions(
            store,
            transitions,
            RUNTIME_FRAME_COUNTS[animation.row],
        )
        output_path = output_dir / f"{animation.animation_id}.gif"
        digest = save_gif(output_path, frames, durations, palette)
        remember_contact_sheet_samples(
            contact_samples,
            animation.animation_id,
            animation.label,
            frames,
            contact_sheet_ids,
        )
        entries.append(
            report_entry(
                animation.animation_id,
                animation.label,
                output_path,
                len(frames),
                len(frames),
                durations,
                steps,
                digest,
                (animation.row,),
            )
        )

    if args.action_profile == "external-v306-linan":
        # Manual persistent swing contract:
        # enter 0->1->2; loop low-A 2 -> forward 3 -> low-B 4 -> back 5
        # -> low-A 2.  A click requests exit, which waits for c2 before
        # dismounting 2->6->7->idle.  Two preview cycles exercise the 5->2->3
        # boundary where an old numeric ping-pong loop repeated "forward".
        swing_entry_refs = [FrameRef(15, column) for column in (0, 1, 2)]
        swing_loop_refs = [FrameRef(15, column) for column in (2, 3, 4, 5, 2)]
        swing_exit_refs = [FrameRef(15, column) for column in (2, 6, 7)] + [FrameRef(0, 0)]
        swing_entry_frames, swing_entry_durations, swing_entry_steps = render_transitions(
            store, list(zip(swing_entry_refs[:-1], swing_entry_refs[1:])), 2
        )
        swing_loop_frames, swing_loop_durations, swing_loop_steps = render_transitions(
            store, list(zip(swing_loop_refs[:-1], swing_loop_refs[1:])), 4
        )
        swing_exit_frames, swing_exit_durations, swing_exit_steps = render_transitions(
            store, list(zip(swing_exit_refs[:-1], swing_exit_refs[1:])), 3
        )
        swing_frames = (
            swing_entry_frames + swing_loop_frames + swing_loop_frames + swing_exit_frames
        )
        swing_durations = (
            swing_entry_durations
            + swing_loop_durations
            + swing_loop_durations
            + swing_exit_durations
        )
        swing_path = output_dir / "linan-swing-entry-loop-click-exit.gif"
        swing_digest = save_gif(swing_path, swing_frames, swing_durations, palette)
        remember_contact_sheet_samples(
            contact_samples,
            "linan-swing",
            "Lin'an persistent swing + click dismount",
            swing_frames,
            contact_sheet_ids,
        )
        swing_entry_stage_count = len(swing_entry_frames)
        swing_loop_stage_count = len(swing_loop_frames)
        swing_exit_stage_count = len(swing_exit_frames)
        swing_entry = report_entry(
            "linan-swing",
            "Manual Lin'an swing: mount, persistent loop, click at low point to dismount",
            swing_path,
            swing_entry_stage_count + (2 * swing_loop_stage_count) + swing_exit_stage_count,
            len(swing_frames),
            swing_durations,
            (swing_entry_steps, swing_loop_steps, swing_exit_steps),
            swing_digest,
            (15, 0),
            {
                "entryTweenStages": swing_entry_stage_count,
                "loopTweenStagesPerCycle": swing_loop_stage_count,
                "previewLoopCycles": 2,
                "exitTweenStages": swing_exit_stage_count,
            },
        )
        swing_entry.update(
            {
                "manualOnly": True,
                "randomIdleEligible": False,
                "clickExitWaitsForLowPoint": True,
                "exitStartsAtFrame": 2,
                "loopFrames": [2, 3, 4, 5, 2],
                "strictAlternatingDepthPeaks": True,
            }
        )
        entries.append(swing_entry)

    # Runtime phone loop: 3 -> 4 -> 5 -> 4 -> 3.  Four transitions times
    # six display stages is exactly the configured 24-stage loop.
    sitting_refs = [FrameRef(22, column) for column in (3, 4, 5, 4, 3)]
    sitting_transitions = list(zip(sitting_refs[:-1], sitting_refs[1:]))
    sitting_frames, sitting_durations, sitting_steps = render_transitions(
        store,
        sitting_transitions,
        transition_count_for_smoothing=4,
    )
    sitting_path = output_dir / "sitting-phone-loop.gif"
    sitting_digest = save_gif(sitting_path, sitting_frames, sitting_durations, palette)
    remember_contact_sheet_samples(
        contact_samples,
        "sitting-phone-loop",
        "Sitting phone loop",
        sitting_frames,
        contact_sheet_ids,
    )
    entries.append(
        report_entry(
            "sitting-phone-loop",
            "Persistent sitting phone loop (3-4-5-4)",
            sitting_path,
            len(sitting_frames),
            len(sitting_frames),
            sitting_durations,
            sitting_steps,
            sitting_digest,
            (22,),
            {"phoneLoopTweenStages": len(sitting_frames)},
        )
    )

    # Runtime side-rest contract.  Entry and wake are independently smoothed
    # to 24 stages; a full 56-particle-stage head-origin Z cycle is held
    # between them so the GIF also checks the persistent sleeping composition.
    entry_refs = [FrameRef(23, column) for column in (0, 1, 2, 3, 4)]
    wake_refs = [FrameRef(23, column) for column in (4, 5, 6, 7)] + [FrameRef(0, 0)]
    entry_transitions = list(zip(entry_refs[:-1], entry_refs[1:]))
    wake_transitions = list(zip(wake_refs[:-1], wake_refs[1:]))
    entry_frames, entry_durations, entry_steps = render_transitions(store, entry_transitions, 4)
    wake_frames, wake_durations, wake_steps = render_transitions(store, wake_transitions, 4)
    sleep_frames = [add_sleep_particles(store, FrameRef(23, 4), stage) for stage in range(56)]
    sleep_durations = [60] * len(sleep_frames)
    terminal_frame = store.get(FrameRef(0, 0)).copy()
    side_frames = entry_frames + sleep_frames + wake_frames + [terminal_frame]
    side_durations = entry_durations + sleep_durations + wake_durations + [300]
    side_path = output_dir / "side-rest-entry-wake.gif"
    side_digest = save_gif(side_path, side_frames, side_durations, palette)
    remember_contact_sheet_samples(
        contact_samples,
        "side-rest-entry-wake",
        "Side-rest entry + sleep + wake",
        side_frames,
        contact_sheet_ids,
    )
    entries.append(
        report_entry(
            "side-rest-entry-wake",
            "Side-rest entry, persistent sleep Z cycle, and click wake",
            side_path,
            len(entry_frames) + len(wake_frames),
            len(side_frames),
            side_durations,
            (entry_steps, wake_steps),
            side_digest,
            (23, 0),
            {
                "entryTweenStages": len(entry_frames),
                "sleepParticleStages": len(sleep_frames),
                "wakeTweenStages": len(wake_frames),
                "terminalIdleFrames": 1,
            },
        )
    )

    failures: List[str] = []
    for entry in entries:
        segments = entry.get("segments")
        if isinstance(segments, dict):
            checked = [
                value for key, value in segments.items()
                if key.endswith("TweenStages") or key == "phoneLoopTweenStages"
            ]
            if any(int(value) < MINIMUM_STAGES for value in checked):
                failures.append(f"{entry['id']}: a tween segment has fewer than {MINIMUM_STAGES} stages")
        elif int(entry["tweenStageCount"]) < MINIMUM_STAGES:
            failures.append(f"{entry['id']}: fewer than {MINIMUM_STAGES} stages")

    contact_sheet_path = output_dir / "runtime-smoothing-contact-sheet.png"
    contact_sheet = save_contact_sheet(contact_sheet_path, contact_samples, contact_sheet_ids)

    report = {
        "schemaVersion": 1,
        "ok": not failures,
        "framesRoot": str(frames_root),
        "outputDirectory": str(output_dir),
        "actionProfile": args.action_profile,
        "sourceFrameSize": list(SOURCE_SIZE),
        "qaFrameSize": list(QA_SIZE),
        "backgroundRgba": list(BACKGROUND),
        "runtimeModel": {
            "curve": "smoothstep(t) = t*t*(3-2*t)",
            "blend": "source-over opacity crossfade",
            "silhouetteAnchor": "alpha>4 bottom-center",
            "alignmentStrength": ALIGNMENT_STRENGTH,
            "minimumStagesPerCycleOrSegment": MINIMUM_STAGES,
            "timingReferenceStagesPerCycle": TIMING_REFERENCE_STAGES,
            "gazeRowsExcluded": [9, 10],
            "gifTimingNote": (
                "GIF delays use a minimum 20 ms clock and 10 ms quantization; "
                "gifPreviewDurationMs can therefore be slightly longer than the executable's timer cadence."
            ),
            "runtimeCadenceAuthority": "XiaoXiWeiPet executable self-test",
            "linanPersistentSwingManualOnly": args.action_profile == "external-v306-linan",
        },
        "animationCount": len(entries),
        "animations": entries,
        "contactSheet": contact_sheet,
        "errors": failures,
    }
    report_path = output_dir / "runtime-smoothing-qa-report.json"
    report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(f"Rendered {len(entries)} QA GIFs to {output_dir}")
    print(f"Report: {report_path}")
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
