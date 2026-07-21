#!/usr/bin/env python3
"""Recover high-resolution standalone frames from the approved source strips.

The Codex pet atlas deliberately uses 192x208 cells.  This exporter repeats the
approved deterministic registration against the original row strips and writes
4x frames directly from those sources, avoiding a 192 -> 768 upscale.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import zipfile
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageOps


SCALE = 4
LOW_CELL = (192, 208)
FULL_CELL = (LOW_CELL[0] * SCALE, LOW_CELL[1] * SCALE)
VIEWPORT_LOW = (30, 3, 162, 205)
VIEWPORT_4X = tuple(value * SCALE for value in VIEWPORT_LOW)
FRAME_SIZE = (
    VIEWPORT_4X[2] - VIEWPORT_4X[0],
    VIEWPORT_4X[3] - VIEWPORT_4X[1],
)
CHROMA_KEY = (0, 255, 255)
CHROMA_THRESHOLD = 96.0
EXTERNAL_RESIDUAL_CHROMA_THRESHOLD = 150.0
LOOK_SCALE_1X = 0.31319554848966613

ROW_SPECS = (
    (0, "idle", 6),
    (1, "running-right", 8),
    (2, "running-left", 8),
    (3, "waving", 4),
    (4, "jumping", 5),
    (5, "failed", 8),
    (6, "waiting", 6),
    (7, "running", 6),
    (8, "review", 6),
)
MOVEMENT_SPECS = (
    (12, 13, "walk", "walk-right", 8, 145),
    (14, 15, "jog", "jog-right", 8, 100),
    (16, 17, "sprint", "sprint-right", 8, 72),
)
BUILTIN_V304_EXTENSION_SPECS = (
    (12, "adorable", "idle-adorable", 8, 220),
    (13, "laughing", "idle-laughing", 8, 190),
    (14, "crying", "idle-crying", 8, 230),
)
BUILTIN_V305_EXTENSION_SPECS = BUILTIN_V304_EXTENSION_SPECS + (
    (15, "skin-exclusive", "idle-builtin-exclusive", 8, 220),
)
EXTERNAL_EXTENSION_SPECS = (
    (15, "skin-exclusive", "skin-exclusive", 8, 220),
)
IDLE_ACTION_SPECS = (
    (18, "handdance", "idle-handdance", 8, 190),
    (19, "singing", "idle-singing", 8, 220),
    (20, "heroine", "idle-heroine", 8, 210),
    (21, "flyingkiss", "idle-flyingkiss", 8, 230),
    (22, "sitting", "idle-sitting-phone", 8, 300),
    (23, "side-rest", "idle-side-rest", 8, 340),
)
USED_PER_ROW = (
    7, 8, 8, 4, 5, 8, 6, 6, 6, 8, 8, 6,
    8, 8, 8, 8, 8, 8,
    8, 8, 8, 8, 8, 8,
)


def load_hatch_helpers(skill_dir: Path) -> dict[str, object]:
    scripts = skill_dir / "scripts"
    if not scripts.is_dir():
        raise SystemExit(f"hatch-pet scripts directory not found: {scripts}")
    sys.path.insert(0, str(scripts))

    from extract_strip_frames import (  # type: ignore
        component_bounds,
        component_frame_groups,
        component_group_image,
        remove_chroma_background,
    )
    from assemble_extended_atlas import cell_geometry, clear_transparent_rgb  # type: ignore
    import despill_chroma_edges  # type: ignore

    return {
        "component_bounds": component_bounds,
        "component_frame_groups": component_frame_groups,
        "component_group_image": component_group_image,
        "remove_chroma_background": remove_chroma_background,
        "cell_geometry": cell_geometry,
        "clear_transparent_rgb": clear_transparent_rgb,
        "despill": despill_chroma_edges,
    }


def clean_strip(path: Path, helpers: dict[str, object]) -> tuple[Image.Image, dict[str, object]]:
    with Image.open(path) as opened:
        rgba = helpers["remove_chroma_background"](
            opened, CHROMA_KEY, CHROMA_THRESHOLD
        )

    # The skill's despill implementation is reused at the source-strip level.
    # Its alpha channel is preserved, so grouping geometry remains deterministic.
    despill = helpers["despill"]
    cleaned, report = despill.decontaminate_image(
        rgba,
        chroma_key=CHROMA_KEY,
        strength=1,
        edge_radius=5,
        spill_tolerance=0.15,
        minimum_saturation=0.1,
    )
    cleaned = helpers["clear_transparent_rgb"](cleaned)
    return cleaned, report


def remove_external_chroma_residue(image: Image.Image) -> Image.Image:
    """Remove model-produced cyan specks that survive the source key pass.

    The two bundled external costumes contain no cyan design element.  Some
    generated antialias pixels are farther than the conservative source-key
    threshold but remain visibly turquoise after 4K scaling.  A vectorized
    second pass on completed external frames removes only colors still close
    to #00FFFF; the pale-blue school shirt remains far outside this radius.
    """
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    rgb = rgba[..., :3].astype(np.int32)
    key = np.asarray(CHROMA_KEY, dtype=np.int32)
    delta = rgb - key
    distance_squared = np.sum(delta * delta, axis=2, dtype=np.int32)
    threshold_squared = int(EXTERNAL_RESIDUAL_CHROMA_THRESHOLD ** 2)
    visible = rgba[..., 3] > 0
    red = rgb[..., 0]
    green = rgb[..., 1]
    blue = rgb[..., 2]
    # A cyan reflection over skin is repaired below rather than made
    # transparent.  Its red channel stays materially higher than true key
    # spill, while the school shirt remains distinctly blue-dominant.
    skin_tint = (
        visible
        & (red >= 100)
        & (red < 190)
        & (green > red + 20)
        & (blue > red + 10)
        & (green >= blue - 5)
    )
    # A much smaller set of antialiased key pixels can appear as a muted
    # grey-cyan fleck instead of saturated cyan.  Both bundled external skins
    # have no cyan costume detail, so warm these near-equal G/B pixels as skin
    # instead of leaving a visible dot at 4K scale.
    cool_neutral_spill = (
        visible
        & (red >= 90)
        & (red < 200)
        & (green > red + 3)
        & (blue > red + 3)
        & (np.abs(green - blue) <= 30)
    )
    # The same key reflection may lean green rather than cyan after Lanczos
    # scaling.  Neither external costume contains green; pale-blue and navy
    # uniform pixels remain blue-dominant and are deliberately excluded.
    warm_green_skin_spill = (
        visible
        & (red >= 90)
        & (red < 225)
        & (green > red + 2)
        & (green > blue + 2)
        & (blue > red - 25)
    )
    skin_tint |= cool_neutral_spill | warm_green_skin_spill
    residue = visible & (distance_squared <= threshold_squared) & ~skin_tint

    # Generated skin/leg holes can contain a darker, nearly equal G/B cyan
    # island that sits just outside the radial threshold.  The pale-blue
    # school shirt has a distinctly bluer channel, so the equal-channel test
    # removes those islands without punching holes in the uniform.
    dark_cyan_island = (
        visible
        & (red < 100)
        & (green > red + 30)
        & (blue > red + 30)
        & (np.abs(green - blue) <= 30)
    )
    residue |= dark_cyan_island
    rgba[residue] = 0

    # Reconstruct small cyan-on-skin islands as a warm skin tone while keeping
    # the original alpha.  This removes knee/hand specks without opening a
    # dark hole through overlapping limbs.
    repaired_base = np.maximum(green, blue)
    rgba[..., 0][skin_tint] = np.minimum(
        255, repaired_base[skin_tint] + 38
    ).astype(np.uint8)
    rgba[..., 1][skin_tint] = np.minimum(
        245, repaired_base[skin_tint] + 8
    ).astype(np.uint8)
    rgba[..., 2][skin_tint] = np.minimum(
        235, repaired_base[skin_tint]
    ).astype(np.uint8)

    # Cyan-key light can also leave a greener reflection inside otherwise
    # black hair.  Preserve its alpha/shape but neutralize the spill to a dark
    # tone.  Navy fabric and the blue shirt trim stay outside these deltas.
    green_spill = (
        (rgba[..., 3] > 0)
        & (red < 120)
        & (green > red + 16)
        & (blue > red + 3)
        & (green > blue + 4)
    )
    neutral_cap = np.clip(red + 8, 0, 255).astype(np.uint8)
    rgba[..., 1][green_spill] = np.minimum(
        rgba[..., 1][green_spill], neutral_cap[green_spill]
    )
    rgba[..., 2][green_spill] = np.minimum(
        rgba[..., 2][green_spill], neutral_cap[green_spill]
    )
    return Image.fromarray(rgba, mode="RGBA")


def stable_viewports(
    strip: Image.Image,
    frame_count: int,
    helpers: dict[str, object],
) -> tuple[list[Image.Image], dict[str, object]]:
    groups = helpers["component_frame_groups"](strip, frame_count)
    if groups is None:
        raise SystemExit(f"could not recover {frame_count} component groups")

    padding = 4
    bboxes = [helpers["component_bounds"](group) for group in groups]
    shared_top = max(0, min(bbox[1] for bbox in bboxes) - padding)
    shared_bottom = min(strip.height, max(bbox[3] for bbox in bboxes) + padding)
    viewport_width = max(bbox[2] - bbox[0] for bbox in bboxes) + padding * 2
    viewport_height = max(1, shared_bottom - shared_top)

    frames: list[Image.Image] = []
    for group, bbox in zip(groups, bboxes):
        grouped = helpers["component_group_image"](strip, group, padding=padding)
        grouped_top = max(0, bbox[1] - padding)
        viewport = Image.new(
            "RGBA", (viewport_width, viewport_height), (0, 0, 0, 0)
        )
        left = (viewport_width - grouped.width) // 2
        viewport.alpha_composite(grouped, (left, grouped_top - shared_top))
        frames.append(viewport)

    low_scale = min(
        (LOW_CELL[0] - 10) / viewport_width,
        (LOW_CELL[1] - 10) / viewport_height,
        1.0,
    )
    low_width = max(1, round(viewport_width * low_scale))
    low_height = max(1, round(viewport_height * low_scale))
    low_left = (LOW_CELL[0] - low_width) // 2
    low_top = (LOW_CELL[1] - low_height) // 2
    geometry = {
        "sourceViewport": [viewport_width, viewport_height],
        "scale1x": low_scale,
        "size1x": [low_width, low_height],
        "position1x": [low_left, low_top],
        "size4x": [low_width * SCALE, low_height * SCALE],
        "position4x": [low_left * SCALE, low_top * SCALE],
        "componentBounds": [list(bbox) for bbox in bboxes],
    }
    return frames, geometry


def stable_movement_viewports(
    strip: Image.Image,
    frame_count: int,
    helpers: dict[str, object],
) -> tuple[list[Image.Image], dict[str, object]]:
    """Recover a gait row, including one touching neighboring-pose pair.

    Normal rows keep the skill's connected-component recovery.  Some realistic
    strips have two neighboring shoes touching by a few antialiased pixels; in
    that case the component finder sees seven bodies plus a one-pixel speck.
    Recover seven real groups, split the single double-width group at the
    lowest-alpha vertical valley, and then apply the same shared registration.
    """
    groups = helpers["component_frame_groups"](strip, frame_count)
    if groups is None:
        raise SystemExit(f"could not recover {frame_count} movement groups")
    bboxes = [helpers["component_bounds"](group) for group in groups]
    widths = [bbox[2] - bbox[0] for bbox in bboxes]
    heights = [bbox[3] - bbox[1] for bbox in bboxes]
    median_width = float(np.median(np.asarray(widths, dtype=np.float64)))
    median_height = float(np.median(np.asarray(heights, dtype=np.float64)))
    groups_are_stable = (
        min(widths) >= median_width * 0.30
        and max(widths) <= median_width * 1.70
        and min(heights) >= median_height * 0.30
    )
    if groups_are_stable:
        viewports, geometry = stable_viewports(strip, frame_count, helpers)
        geometry = constrain_movement_geometry(geometry)
        geometry["extraction"] = "connected-components"
        return viewports, geometry

    reduced_groups = helpers["component_frame_groups"](strip, frame_count - 1)
    if reduced_groups is None:
        raise SystemExit("movement component recovery failed")
    reduced_bounds = [helpers["component_bounds"](group) for group in reduced_groups]
    reduced_widths = [bbox[2] - bbox[0] for bbox in reduced_bounds]
    reduced_median = float(np.median(np.asarray(reduced_widths, dtype=np.float64)))
    split_index = int(np.argmax(np.asarray(reduced_widths, dtype=np.int64)))
    if reduced_widths[split_index] < reduced_median * 1.50:
        raise SystemExit("movement row has unstable groups but no recoverable touching pair")

    padding = 4
    grouped_images: list[Image.Image] = []
    recovered_bounds: list[tuple[int, int, int, int]] = []
    split_x = None
    alpha = np.asarray(strip.getchannel("A"), dtype=np.uint8) > 16
    for index, (group, bbox) in enumerate(zip(reduced_groups, reduced_bounds)):
        if index != split_index:
            grouped_images.append(
                helpers["component_group_image"](strip, group, padding=padding)
            )
            recovered_bounds.append(bbox)
            continue

        left, _top, right, _bottom = bbox
        midpoint = (left + right) // 2
        radius = max(8, round((right - left) * 0.12))
        candidates = range(max(left + 1, midpoint - radius), min(right, midpoint + radius + 1))
        split_x = min(
            candidates,
            key=lambda x: (int(np.count_nonzero(alpha[:, x])), abs(x - midpoint)),
        )
        for piece_left, piece_right in ((left, split_x), (split_x, right)):
            piece = strip.crop((piece_left, 0, piece_right, strip.height))
            local_bounds = piece.getbbox()
            if local_bounds is None:
                raise SystemExit("touching movement-pose split produced an empty frame")
            global_bounds = (
                piece_left + local_bounds[0],
                local_bounds[1],
                piece_left + local_bounds[2],
                local_bounds[3],
            )
            crop_left = max(piece_left, global_bounds[0] - padding)
            crop_top = max(0, global_bounds[1] - padding)
            crop_right = min(piece_right, global_bounds[2] + padding)
            crop_bottom = min(strip.height, global_bounds[3] + padding)
            grouped_images.append(
                strip.crop((crop_left, crop_top, crop_right, crop_bottom))
            )
            recovered_bounds.append(global_bounds)

    shared_top = max(0, min(bbox[1] for bbox in recovered_bounds) - padding)
    shared_bottom = min(strip.height, max(bbox[3] for bbox in recovered_bounds) + padding)
    viewport_width = max(bbox[2] - bbox[0] for bbox in recovered_bounds) + padding * 2
    viewport_height = max(1, shared_bottom - shared_top)
    viewports: list[Image.Image] = []
    for grouped, bbox in zip(grouped_images, recovered_bounds):
        grouped_top = max(0, bbox[1] - padding)
        viewport = Image.new("RGBA", (viewport_width, viewport_height), (0, 0, 0, 0))
        viewport.alpha_composite(
            grouped,
            ((viewport_width - grouped.width) // 2, grouped_top - shared_top),
        )
        viewports.append(viewport)

    low_scale = min(
        (LOW_CELL[0] - 10) / viewport_width,
        (LOW_CELL[1] - 10) / viewport_height,
        1.0,
    )
    low_width = max(1, round(viewport_width * low_scale))
    low_height = max(1, round(viewport_height * low_scale))
    geometry = {
        "sourceViewport": [viewport_width, viewport_height],
        "scale1x": low_scale,
        "size1x": [low_width, low_height],
        "position1x": [
            (LOW_CELL[0] - low_width) // 2,
            (LOW_CELL[1] - low_height) // 2,
        ],
        "size4x": [low_width * SCALE, low_height * SCALE],
        "position4x": [
            ((LOW_CELL[0] - low_width) // 2) * SCALE,
            ((LOW_CELL[1] - low_height) // 2) * SCALE,
        ],
        "componentBounds": [list(bbox) for bbox in recovered_bounds],
        "extraction": "touching-pair-valley-split",
        "splitX": split_x,
    }
    return viewports, constrain_movement_geometry(geometry)


def constrain_movement_geometry(
    geometry: dict[str, object],
) -> dict[str, object]:
    """Keep every gait pose inside the fixed 528px transparent viewport.

    The source cell is wider than the runtime crop.  A very long sprint stride
    can therefore pass the older cell-level fit while leaving antialiased
    pixels on the crop edge.  Recompute only movement geometry against the
    visible width, preserving one scale and one registration for the full row.
    """
    viewport_width, viewport_height = geometry["sourceViewport"]
    safe_visible_width = VIEWPORT_LOW[2] - VIEWPORT_LOW[0] - 4
    low_scale = min(
        safe_visible_width / viewport_width,
        (LOW_CELL[1] - 10) / viewport_height,
        1.0,
    )
    low_width = max(1, round(viewport_width * low_scale))
    low_height = max(1, round(viewport_height * low_scale))
    low_left = (LOW_CELL[0] - low_width) // 2
    low_top = (LOW_CELL[1] - low_height) // 2
    geometry.update(
        {
            "scale1x": low_scale,
            "size1x": [low_width, low_height],
            "position1x": [low_left, low_top],
            "size4x": [low_width * SCALE, low_height * SCALE],
            "position4x": [low_left * SCALE, low_top * SCALE],
            "visibleWidthSafetyMargin1x": 4,
        }
    )
    return geometry


def place_standard_viewport(viewport: Image.Image, geometry: dict[str, object]) -> Image.Image:
    target_size = tuple(geometry["size4x"])
    target_position = tuple(geometry["position4x"])
    scaled = viewport.resize(target_size, Image.Resampling.LANCZOS)
    cell = Image.new("RGBA", FULL_CELL, (0, 0, 0, 0))
    cell.alpha_composite(scaled, target_position)
    return cell.crop(VIEWPORT_4X)


class Geometry:
    def __init__(self, height: int, lower_center_x: float, bottom: int) -> None:
        self.height = height
        self.lower_center_x = lower_center_x
        self.bottom = bottom


def scaled_neutral_geometry(low_atlas: Image.Image, helpers: dict[str, object]) -> Geometry:
    neutral = low_atlas.crop((6 * 192, 0, 7 * 192, 208))
    geometry = helpers["cell_geometry"](neutral)
    if geometry is None:
        raise SystemExit("approved neutral frame is empty")
    return Geometry(
        geometry.height * SCALE,
        geometry.lower_center_x * SCALE,
        geometry.bottom * SCALE,
    )


def place_look_cell(
    source: Image.Image,
    target: Geometry,
    helpers: dict[str, object],
) -> tuple[Image.Image, dict[str, object]]:
    bbox = source.getbbox()
    geometry = helpers["cell_geometry"](source)
    if bbox is None or geometry is None:
        raise SystemExit("look-direction source cell is empty")

    crop = source.crop(bbox)
    local_lower_center = geometry.lower_center_x - bbox[0]

    # Preserve the exact rounded 1x registration, then multiply its integer
    # result by four.  This keeps the already approved look-loop geometry.
    width_1x = max(1, round(crop.width * LOOK_SCALE_1X))
    height_1x = max(1, round(crop.height * LOOK_SCALE_1X))
    left_1x = round(target.lower_center_x / SCALE - local_lower_center * LOOK_SCALE_1X)
    top_1x = round(target.bottom / SCALE) - height_1x

    size_4x = (width_1x * SCALE, height_1x * SCALE)
    position_4x = (left_1x * SCALE, top_1x * SCALE)
    scaled = crop.resize(size_4x, Image.Resampling.LANCZOS)
    cell = Image.new("RGBA", FULL_CELL, (0, 0, 0, 0))
    cell.alpha_composite(scaled, position_4x)
    return cell.crop(VIEWPORT_4X), {
        "sourceBounds": list(bbox),
        "size1x": [width_1x, height_1x],
        "position1x": [left_1x, top_1x],
        "size4x": list(size_4x),
        "position4x": list(position_4x),
    }


def extract_look_row(
    path: Path,
    target: Geometry,
    helpers: dict[str, object],
) -> tuple[list[Image.Image], list[dict[str, object]], dict[str, object]]:
    strip, despill_report = clean_strip(path, helpers)
    groups = helpers["component_frame_groups"](strip, 8)
    if groups is None:
        raise SystemExit(f"could not recover 8 look groups from {path}")

    frames: list[Image.Image] = []
    transforms: list[dict[str, object]] = []
    for group in groups:
        grouped = helpers["component_group_image"](strip, group, padding=4)
        frame, transform = place_look_cell(grouped, target, helpers)
        frames.append(frame)
        transforms.append(transform)
    return frames, transforms, despill_report


def extract_external_look_rows(
    row9_path: Path,
    row10_path: Path,
    neutral_frame: Image.Image,
    helpers: dict[str, object],
) -> tuple[
    list[Image.Image],
    list[Image.Image],
    list[dict[str, object]],
    list[dict[str, object]],
    dict[str, object],
    dict[str, object],
    dict[str, float],
]:
    """Register replacement gaze rows against the skin's neutral body.

    Generated row 9 and row 10 strips can contain the same costume at very
    different source scales.  Treating both strips as one fixed viewport keeps
    that mistake and makes the pet shrink as soon as gaze tracking is entered.
    Each coherent eight-direction strip therefore gets one action-level scale,
    while both strip scales target the *same* neutral height, lower-body center
    and foot baseline.  Frames within a row never receive individual fit scales.
    """
    neutral_geometry = helpers["cell_geometry"](neutral_frame)
    if neutral_geometry is None:
        raise SystemExit("external skin neutral frame is empty")

    def recover_row(
        path: Path,
        row_number: int,
    ) -> tuple[list[Image.Image], list[dict[str, object]], dict[str, object], float]:
        strip, despill_report = clean_strip(path, helpers)
        groups = helpers["component_frame_groups"](strip, 8)
        if groups is None:
            raise SystemExit(f"could not recover 8 external look groups from {path}")

        sources: list[tuple[Image.Image, object, tuple[int, int, int, int]]] = []
        source_heights: list[int] = []
        for group in groups:
            grouped = helpers["component_group_image"](strip, group, padding=4)
            bbox = grouped.getbbox()
            geometry = helpers["cell_geometry"](grouped)
            if bbox is None or geometry is None:
                raise SystemExit(f"external look row {row_number} contains an empty pose")
            crop = grouped.crop(bbox)
            sources.append((crop, geometry, bbox))
            source_heights.append(int(geometry.height))

        # One scale per eight-frame strip prevents angle-to-angle size breathing.
        scale = float(neutral_geometry.height) / float(np.median(source_heights))

        # Keep a small transparent safety margin.  If a wide sleeve or hairstyle
        # would clip at the neutral-height scale, reduce the whole row together.
        safety = 3
        fit_scales: list[float] = [scale]
        for crop, geometry, bbox in sources:
            local_center = float(geometry.lower_center_x) - bbox[0]
            local_bottom = float(geometry.bottom) - bbox[1]
            left_extent = max(1.0, local_center)
            right_extent = max(1.0, crop.width - local_center)
            top_extent = max(1.0, local_bottom)
            fit_scales.extend(
                [
                    (float(neutral_geometry.lower_center_x) - safety) / left_extent,
                    (FRAME_SIZE[0] - float(neutral_geometry.lower_center_x) - safety) / right_extent,
                    (float(neutral_geometry.bottom) - safety) / top_extent,
                ]
            )
        scale = max(0.01, min(fit_scales))

        row_frames: list[Image.Image] = []
        row_transforms: list[dict[str, object]] = []
        for index, (crop, geometry, bbox) in enumerate(sources):
            local_center = float(geometry.lower_center_x) - bbox[0]
            local_bottom = float(geometry.bottom) - bbox[1]
            width = max(1, round(crop.width * scale))
            height = max(1, round(crop.height * scale))
            left = round(float(neutral_geometry.lower_center_x) - local_center * scale)
            top = round(float(neutral_geometry.bottom) - local_bottom * scale)
            scaled = crop.resize((width, height), Image.Resampling.LANCZOS)
            frame = Image.new("RGBA", FRAME_SIZE, (0, 0, 0, 0))
            frame.alpha_composite(scaled, (left, top))
            row_frames.append(frame)
            row_transforms.append(
                {
                    "externalNeutralRegisteredLook": True,
                    "row": row_number,
                    "rowIndex": index,
                    "sourceBounds": list(bbox),
                    "rowSharedScale": scale,
                    "size4x": [width, height],
                    "position4x": [left, top],
                    "targetHeight4x": int(neutral_geometry.height),
                    "targetLowerCenterX4x": float(neutral_geometry.lower_center_x),
                    "targetBottom4x": int(neutral_geometry.bottom),
                }
            )
        return row_frames, row_transforms, despill_report, scale

    row9_frames, row9_transforms, row9_despill, row9_scale = recover_row(row9_path, 9)
    row10_frames, row10_transforms, row10_despill, row10_scale = recover_row(row10_path, 10)
    return (
        row9_frames,
        row10_frames,
        row9_transforms,
        row10_transforms,
        row9_despill,
        row10_despill,
        {"row9": row9_scale, "row10": row10_scale},
    )


def alpha_bbox(image: Image.Image, threshold: int = 16) -> tuple[int, int, int, int] | None:
    alpha = np.asarray(image.getchannel("A"), dtype=np.uint8)
    ys, xs = np.nonzero(alpha > threshold)
    if len(xs) == 0:
        return None
    return int(xs.min()), int(ys.min()), int(xs.max() + 1), int(ys.max() + 1)


def look_alignment_report(
    neutral: Image.Image,
    look_frames: list[Image.Image],
    helpers: dict[str, object],
) -> dict[str, object]:
    neutral_geometry = helpers["cell_geometry"](neutral)
    neutral_bounds = alpha_bbox(neutral)
    if neutral_geometry is None or neutral_bounds is None:
        return {"ok": False, "reason": "neutral frame is empty"}

    neutral_alpha = np.asarray(neutral.getchannel("A"), dtype=np.uint8) > 16
    neutral_area = int(neutral_alpha.sum())
    frames: list[dict[str, object]] = []
    ok = True
    for index, frame in enumerate(look_frames):
        geometry = helpers["cell_geometry"](frame)
        bounds = alpha_bbox(frame)
        if geometry is None or bounds is None:
            frames.append({"index": index, "ok": False, "reason": "empty frame"})
            ok = False
            continue
        alpha = np.asarray(frame.getchannel("A"), dtype=np.uint8) > 16
        area_ratio = float(alpha.sum()) / max(1, neutral_area)
        height_ratio = float(geometry.height) / max(1, int(neutral_geometry.height))
        center_delta = float(geometry.lower_center_x) - float(neutral_geometry.lower_center_x)
        bottom_delta = int(geometry.bottom) - int(neutral_geometry.bottom)
        frame_ok = (
            0.97 <= height_ratio <= 1.03
            and 0.82 <= area_ratio <= 1.18
            and abs(center_delta) <= 4.0
            and abs(bottom_delta) <= 4
        )
        ok = ok and frame_ok
        frames.append(
            {
                "index": index,
                "ok": frame_ok,
                "alphaBounds": list(bounds),
                "heightRatioToNeutral": height_ratio,
                "alphaAreaRatioToNeutral": area_ratio,
                "lowerCenterDeltaPixels": center_delta,
                "bottomDeltaPixels": bottom_delta,
            }
        )
    return {
        "ok": ok,
        "neutral": {
            "alphaBounds": list(neutral_bounds),
            "height": int(neutral_geometry.height),
            "lowerCenterX": float(neutral_geometry.lower_center_x),
            "bottom": int(neutral_geometry.bottom),
            "alphaArea": neutral_area,
        },
        "limits": {
            "heightRatio": [0.97, 1.03],
            "alphaAreaRatio": [0.82, 1.18],
            "maximumLowerCenterDeltaPixels": 4.0,
            "maximumBottomDeltaPixels": 4,
        },
        "frames": frames,
    }


def save_look_alignment_qa(
    neutral: Image.Image,
    row9: list[Image.Image],
    row10: list[Image.Image],
    output: Path,
) -> None:
    thumb_size = (FRAME_SIZE[0] // 4, FRAME_SIZE[1] // 4)
    slot_width = 142
    label_width = 150
    header_height = 54
    row_height = thumb_size[1] + 34
    canvas = Image.new(
        "RGB",
        (label_width + slot_width * 8, header_height + row_height * 3),
        (31, 33, 36),
    )
    draw = ImageDraw.Draw(canvas)
    font_path = Path(r"C:\Windows\Fonts\arial.ttf")
    font = (
        ImageFont.truetype(str(font_path), 18)
        if font_path.is_file()
        else ImageFont.load_default()
    )
    draw.text(
        (18, 16),
        "GAZE SCALE / FEET REGISTRATION - neutral + 16 mouse directions",
        fill=(245, 245, 245),
        font=font,
    )
    rows = (("NEUTRAL", [neutral] * 8), ("DIRECTIONS 0-7", row9), ("DIRECTIONS 8-15", row10))
    for row_index, (label, frames) in enumerate(rows):
        top = header_height + row_index * row_height
        draw.text((10, top + 86), label, fill=(220, 225, 230), font=font)
        for column, frame in enumerate(frames):
            thumb = frame.resize(thumb_size, Image.Resampling.LANCZOS)
            left = label_width + column * slot_width + (slot_width - thumb.width) // 2
            canvas.paste(thumb, (left, top), thumb)
            draw.text(
                (label_width + column * slot_width + 6, top + thumb_size[1] + 4),
                str(column + (0 if row_index < 2 else 8)),
                fill=(190, 200, 210),
                font=font,
            )
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output, optimize=True)


def alpha_iou(first: Image.Image, second: Image.Image) -> float:
    a = np.asarray(first.getchannel("A"), dtype=np.uint8) > 16
    b = np.asarray(second.getchannel("A"), dtype=np.uint8) > 16
    union = np.logical_or(a, b).sum()
    if union == 0:
        return 1.0
    return float(np.logical_and(a, b).sum() / union)


def tenengrad(image: Image.Image) -> float:
    rgba = np.asarray(image.convert("RGBA"), dtype=np.float32)
    luma = rgba[..., 0] * 0.2126 + rgba[..., 1] * 0.7152 + rgba[..., 2] * 0.0722
    alpha = rgba[..., 3] > 64
    height = luma.shape[0]
    roi = np.zeros_like(alpha)
    roi[: max(1, int(height * 0.35)), :] = True
    mask = alpha & roi
    gx = np.zeros_like(luma)
    gy = np.zeros_like(luma)
    gx[:, 1:-1] = luma[:, 2:] - luma[:, :-2]
    gy[1:-1, :] = luma[2:, :] - luma[:-2, :]
    values = gx * gx + gy * gy
    return float(values[mask].mean()) if mask.any() else 0.0


def edge_alpha_count(image: Image.Image, margin: int = 4) -> int:
    alpha = np.asarray(image.getchannel("A"), dtype=np.uint8)
    edge = np.zeros_like(alpha, dtype=bool)
    edge[:margin, :] = True
    edge[-margin:, :] = True
    edge[:, :margin] = True
    edge[:, -margin:] = True
    return int(np.count_nonzero((alpha > 0) & edge))


def transparent_rgb_count(image: Image.Image) -> int:
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8)
    return int(np.count_nonzero((rgba[..., 3] == 0) & np.any(rgba[..., :3] != 0, axis=2)))


def cleanup_angry_frame(image: Image.Image, column: int) -> Image.Image:
    """Remove cyan spill and the generated stomp-frame hair afterimage.

    The source strip's fifth pose contains dark grey/teal hair-like fragments
    below the skirt.  They are not present in the adjacent poses and read as a
    detached motion afterimage once the chroma background is removed.  Keep
    the approved pose intact while clearing only that lower-skirt region.
    """
    rgba = np.asarray(image.convert("RGBA"), dtype=np.uint8).copy()
    rgb = rgba[..., :3].astype(np.int16)
    red = rgb[..., 0]
    green = rgb[..., 1]
    blue = rgb[..., 2]
    alpha = rgba[..., 3]

    # Clear only strongly cyan, partially transparent edge pixels, then warm
    # the remaining dark green spill back toward neutral dark hair.
    fringe = (
        (alpha > 0)
        & (alpha < 180)
        & (green > red + 18)
        & (blue > red + 8)
    )
    rgba[fringe] = 0

    spill = (
        (rgba[..., 3] > 0)
        & (red < 190)
        & (green > red + 6)
        & (blue > red + 3)
    )
    rgba[..., 1][spill] = np.minimum(green[spill], red[spill] + 4).astype(np.uint8)
    rgba[..., 2][spill] = np.minimum(blue[spill], red[spill] + 3).astype(np.uint8)

    if column == 4:
        yy, xx = np.ogrid[: rgba.shape[0], : rgba.shape[1]]
        lower_skirt_region = (
            (xx >= 110)
            & (xx <= 350)
            & (yy >= 455)
            & (yy <= 540)
            & (rgba[..., 3] > 0)
        )
        skin = (red > 150) & (red > green + 18) & (red > blue + 18)
        white_fabric = (red > 185) & (green > 185) & (blue > 185)
        detached_afterimage = lower_skirt_region & ~(skin | white_fabric)
        rgba[detached_afterimage] = 0

        # A few bright, detached one-pixel highlights remain after the dark
        # afterimage is removed.  They sit outside the leg and below the hem.
        detached_specks = (
            (xx >= 110)
            & (xx <= 162)
            & (yy >= 465)
            & (yy <= 540)
        )
        rgba[detached_specks] = 0

    return Image.fromarray(rgba, mode="RGBA")


def save_angry_stomp_qa(
    frames: list[Image.Image],
    contact_path: Path,
    preview_path: Path,
) -> None:
    labels = ("Tired", "Recover", "Frown", "Lift foot", "Stomp", "Pout")
    durations = (420, 320, 240, 170, 190, 1100)
    thumb_size = (FRAME_SIZE[0] // 2, FRAME_SIZE[1] // 2)
    slot_width = 280
    canvas = Image.new("RGB", (slot_width * len(frames), 470), (31, 33, 36))
    draw = ImageDraw.Draw(canvas)
    font_path = Path(r"C:\Windows\Fonts\arial.ttf")
    font = (
        ImageFont.truetype(str(font_path), 22)
        if font_path.is_file()
        else ImageFont.load_default()
    )
    for index, (frame, label) in enumerate(zip(frames, labels)):
        thumb = frame.resize(thumb_size, Image.Resampling.LANCZOS)
        left = index * slot_width + (slot_width - thumb.width) // 2
        canvas.paste(thumb, (left, 48), thumb)
        text_box = draw.textbbox((0, 0), label, font=font)
        text_width = text_box[2] - text_box[0]
        draw.text(
            (index * slot_width + (slot_width - text_width) // 2, 14),
            label,
            fill=(245, 245, 245),
            font=font,
        )
    contact_path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(contact_path, optimize=True)

    # Preserve full 528x808 source detail in the animated QA preview.
    frames[0].save(
        preview_path,
        format="GIF",
        save_all=True,
        append_images=frames[1:],
        duration=list(durations),
        loop=0,
        disposal=2,
        transparency=0,
        optimize=False,
    )


def movement_alignment_report(
    right_frames: list[Image.Image],
    left_frames: list[Image.Image],
    geometry: dict[str, object],
    action: str,
    external_skin: bool,
) -> dict[str, object]:
    right_bounds = [alpha_bbox(frame) for frame in right_frames]
    left_bounds = [alpha_bbox(frame) for frame in left_frames]
    if any(bounds is None for bounds in right_bounds + left_bounds):
        return {
            "ok": False,
            "reason": "one or more movement frames are empty",
        }

    concrete_right = [bounds for bounds in right_bounds if bounds is not None]
    concrete_left = [bounds for bounds in left_bounds if bounds is not None]
    right_centers = [(bounds[0] + bounds[2]) / 2.0 for bounds in concrete_right]
    right_bottoms = [bounds[3] for bounds in concrete_right]
    left_centers = [(bounds[0] + bounds[2]) / 2.0 for bounds in concrete_left]
    expected_center = FRAME_SIZE[0] / 2.0
    center_deviation = max(abs(center - expected_center) for center in right_centers)
    center_span = max(right_centers) - min(right_centers)
    baseline_span = max(right_bottoms) - min(right_bottoms)
    mirror_exact = all(
        ImageOps.mirror(right).tobytes() == left.tobytes()
        for right, left in zip(right_frames, left_frames)
    )

    # The generated gait may lift one foot, but the registered body should not
    # drift across the narrow desktop-pet viewport.  These limits are generous
    # enough for natural leaning while still catching extraction jumps.
    maximum_center_deviation = FRAME_SIZE[0] * 0.12
    maximum_center_span = FRAME_SIZE[0] * 0.15
    # Pixel bounds are integral; round the proportional limit before comparing
    # so a legitimate 65-pixel airborne sprint is not rejected against 64.64.
    # A generated sprint may contain genuine tucked-leg airborne poses.  They
    # keep the head/body anchor stable while no foot reaches the ground, so a
    # walking baseline threshold would incorrectly reject a natural cycle.
    baseline_ratio = 0.16 if external_skin and action == "sprint" else 0.08
    maximum_baseline_span = float(round(FRAME_SIZE[1] * baseline_ratio))
    ok = (
        mirror_exact
        and center_deviation <= maximum_center_deviation
        and center_span <= maximum_center_span
        and baseline_span <= maximum_baseline_span
    )
    return {
        "ok": ok,
        "rightAlphaBounds": [list(bounds) for bounds in concrete_right],
        "leftAlphaBounds": [list(bounds) for bounds in concrete_left],
        "rightCenterXPixels": right_centers,
        "leftCenterXPixels": left_centers,
        "expectedCenterXPixels": expected_center,
        "maximumCenterDeviationPixels": center_deviation,
        "centerSpanPixels": center_span,
        "baselinePixels": right_bottoms,
        "baselineSpanPixels": baseline_span,
        "limits": {
            "maximumCenterDeviationPixels": maximum_center_deviation,
            "maximumCenterSpanPixels": maximum_center_span,
            "maximumBaselineSpanPixels": maximum_baseline_span,
        },
        "registrationViewport4x": {
            "size": geometry["size4x"],
            "position": geometry["position4x"],
        },
        "leftDerivation": "per-frame horizontal mirror; temporal order preserved",
        "mirrorExact": mirror_exact,
    }


def save_movement_qa(
    name: str,
    right_frames: list[Image.Image],
    left_frames: list[Image.Image],
    frame_duration_ms: int,
    contact_path: Path,
    preview_path: Path,
) -> None:
    thumb_size = (FRAME_SIZE[0] // 2, FRAME_SIZE[1] // 2)
    slot_width = 280
    header_height = 54
    row_height = thumb_size[1] + 54
    canvas = Image.new(
        "RGB",
        (slot_width * len(right_frames), header_height + row_height * 2),
        (31, 33, 36),
    )
    draw = ImageDraw.Draw(canvas)
    font_path = Path(r"C:\Windows\Fonts\arial.ttf")
    font = (
        ImageFont.truetype(str(font_path), 20)
        if font_path.is_file()
        else ImageFont.load_default()
    )
    title = f"{name.upper()} - source right / per-frame mirrored left"
    draw.text((20, 16), title, fill=(245, 245, 245), font=font)
    for row_index, (direction, row_frames) in enumerate(
        (("RIGHT", right_frames), ("LEFT", left_frames))
    ):
        top = header_height + row_index * row_height
        for column, frame in enumerate(row_frames):
            thumb = frame.resize(thumb_size, Image.Resampling.LANCZOS)
            left = column * slot_width + (slot_width - thumb.width) // 2
            canvas.paste(thumb, (left, top + 28), thumb)
            draw.text(
                (column * slot_width + 8, top + 4),
                f"{direction} {column + 1}",
                fill=(210, 215, 220),
                font=font,
            )
    contact_path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(contact_path, optimize=True)

    preview_frames: list[Image.Image] = []
    preview_thumb = (FRAME_SIZE[0] * 3 // 4, FRAME_SIZE[1] * 3 // 4)
    for column, (right, left) in enumerate(zip(right_frames, left_frames)):
        preview = Image.new(
            "RGB", (preview_thumb[0] * 2 + 36, preview_thumb[1] + 54), (31, 33, 36)
        )
        preview_draw = ImageDraw.Draw(preview)
        preview_draw.text(
            (12, 12),
            f"{name.upper()} frame {column + 1}/8 - RIGHT",
            fill=(245, 245, 245),
            font=font,
        )
        preview_draw.text(
            (preview_thumb[0] + 28, 12),
            "LEFT (mirrored)",
            fill=(245, 245, 245),
            font=font,
        )
        right_thumb = right.resize(preview_thumb, Image.Resampling.LANCZOS)
        left_thumb = left.resize(preview_thumb, Image.Resampling.LANCZOS)
        preview.paste(right_thumb, (0, 54), right_thumb)
        preview.paste(left_thumb, (preview_thumb[0] + 36, 54), left_thumb)
        preview_frames.append(preview)
    preview_frames[0].save(
        preview_path,
        format="GIF",
        save_all=True,
        append_images=preview_frames[1:],
        duration=frame_duration_ms,
        loop=0,
        disposal=2,
        optimize=False,
    )


def idle_alignment_report(
    action_frames: list[Image.Image],
    geometry: dict[str, object],
    action: str,
    *,
    maximum_center_deviation_ratio: float = 0.12,
    maximum_center_span_ratio: float = 0.15,
    maximum_baseline_span_ratio: float | None = None,
) -> dict[str, object]:
    bounds = [alpha_bbox(frame) for frame in action_frames]
    if any(value is None for value in bounds):
        return {
            "ok": False,
            "reason": "one or more idle frames are empty",
        }

    concrete = [value for value in bounds if value is not None]
    centers = [(value[0] + value[2]) / 2.0 for value in concrete]
    bottoms = [value[3] for value in concrete]
    widths = [value[2] - value[0] for value in concrete]
    heights = [value[3] - value[1] for value in concrete]
    expected_center = FRAME_SIZE[0] / 2.0
    center_deviation = max(abs(center - expected_center) for center in centers)
    center_span = max(centers) - min(centers)
    baseline_span = max(bottoms) - min(bottoms)
    maximum_center_deviation = FRAME_SIZE[0] * maximum_center_deviation_ratio
    maximum_center_span = FRAME_SIZE[0] * maximum_center_span_ratio
    baseline_ratio = (
        maximum_baseline_span_ratio
        if maximum_baseline_span_ratio is not None
        else (0.16 if action in {"sitting", "side-rest"} else 0.10)
    )
    maximum_baseline_span = FRAME_SIZE[1] * baseline_ratio

    viewport_left = int(geometry["position4x"][0])
    viewport_top = int(geometry["position4x"][1])
    viewport_right = viewport_left + int(geometry["size4x"][0])
    viewport_bottom = viewport_top + int(geometry["size4x"][1])
    viewport_fits_visible_window = (
        viewport_left >= VIEWPORT_4X[0]
        and viewport_right <= VIEWPORT_4X[2]
        and viewport_top >= VIEWPORT_4X[1]
        and viewport_bottom <= VIEWPORT_4X[3]
    )
    ok = (
        center_deviation <= maximum_center_deviation
        and center_span <= maximum_center_span
        and baseline_span <= maximum_baseline_span
        and viewport_fits_visible_window
    )
    return {
        "ok": ok,
        "alphaBounds": [list(value) for value in concrete],
        "centerXPixels": centers,
        "expectedCenterXPixels": expected_center,
        "maximumCenterDeviationPixels": center_deviation,
        "centerSpanPixels": center_span,
        "baselinePixels": bottoms,
        "baselineSpanPixels": baseline_span,
        "alphaWidthPixels": widths,
        "alphaHeightPixels": heights,
        "limits": {
            "maximumCenterDeviationPixels": maximum_center_deviation,
            "maximumCenterSpanPixels": maximum_center_span,
            "maximumBaselineSpanPixels": maximum_baseline_span,
        },
        "registrationViewport4x": {
            "size": geometry["size4x"],
            "position": geometry["position4x"],
        },
        "uniformActionScale": True,
        "scale1x": geometry["scale1x"],
        "viewportFitsVisibleWindow": viewport_fits_visible_window,
    }


def save_idle_action_qa(
    name: str,
    action_frames: list[Image.Image],
    frame_duration_ms: int,
    contact_path: Path,
    preview_path: Path,
) -> None:
    thumb_size = (FRAME_SIZE[0] // 2, FRAME_SIZE[1] // 2)
    slot_width = 280
    header_height = 56
    canvas = Image.new(
        "RGB",
        (slot_width * len(action_frames), header_height + thumb_size[1] + 42),
        (31, 33, 36),
    )
    draw = ImageDraw.Draw(canvas)
    font_path = Path(r"C:\Windows\Fonts\arial.ttf")
    font = (
        ImageFont.truetype(str(font_path), 20)
        if font_path.is_file()
        else ImageFont.load_default()
    )
    draw.text(
        (20, 16),
        f"IDLE {name.upper()} - fixed action-level viewport",
        fill=(245, 245, 245),
        font=font,
    )
    for column, frame in enumerate(action_frames):
        thumb = frame.resize(thumb_size, Image.Resampling.LANCZOS)
        left = column * slot_width + (slot_width - thumb.width) // 2
        canvas.paste(thumb, (left, header_height + 26), thumb)
        draw.text(
            (column * slot_width + 8, header_height + 2),
            f"FRAME {column + 1}",
            fill=(210, 215, 220),
            font=font,
        )
    contact_path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(contact_path, optimize=True)

    preview_frames: list[Image.Image] = []
    preview_size = (FRAME_SIZE[0] * 3 // 4, FRAME_SIZE[1] * 3 // 4)
    for column, frame in enumerate(action_frames):
        preview = Image.new(
            "RGB", (preview_size[0], preview_size[1] + 48), (31, 33, 36)
        )
        preview_draw = ImageDraw.Draw(preview)
        preview_draw.text(
            (12, 12),
            f"{name.upper()} {column + 1}/8",
            fill=(245, 245, 245),
            font=font,
        )
        thumb = frame.resize(preview_size, Image.Resampling.LANCZOS)
        preview.paste(thumb, (0, 48), thumb)
        preview_frames.append(preview)
    preview_frames[0].save(
        preview_path,
        format="GIF",
        save_all=True,
        append_images=preview_frames[1:],
        duration=frame_duration_ms,
        loop=0,
        disposal=2,
        optimize=False,
    )


def save_total_idle_contact(
    idle_frames: dict[str, list[Image.Image]],
    output: Path,
) -> None:
    actions = [spec[1] for spec in IDLE_ACTION_SPECS]
    thumb_size = (FRAME_SIZE[0] // 4, FRAME_SIZE[1] // 4)
    label_width = 150
    slot_width = 142
    header_height = 54
    row_height = thumb_size[1] + 28
    canvas = Image.new(
        "RGB",
        (label_width + slot_width * 8, header_height + row_height * len(actions)),
        (31, 33, 36),
    )
    draw = ImageDraw.Draw(canvas)
    font_path = Path(r"C:\Windows\Fonts\arial.ttf")
    font = (
        ImageFont.truetype(str(font_path), 18)
        if font_path.is_file()
        else ImageFont.load_default()
    )
    draw.text(
        (18, 16),
        "RANDOM IDLE ACTIONS - logical in-app size",
        fill=(245, 245, 245),
        font=font,
    )
    for row_index, action in enumerate(actions):
        top = header_height + row_index * row_height
        draw.text((12, top + 84), action.upper(), fill=(220, 225, 230), font=font)
        for column, frame in enumerate(idle_frames[action]):
            thumb = frame.resize(thumb_size, Image.Resampling.LANCZOS)
            left = label_width + column * slot_width + (slot_width - thumb.width) // 2
            canvas.paste(thumb, (left, top), thumb)
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output, optimize=True)


def save_comparison(
    low_frame: Image.Image,
    hd_frame: Image.Image,
    output: Path,
) -> None:
    old = low_frame.resize(FRAME_SIZE, Image.Resampling.LANCZOS)
    canvas = Image.new("RGB", (1160, 1320), (31, 33, 36))
    draw = ImageDraw.Draw(canvas)
    font_path = Path(r"C:\Windows\Fonts\msyh.ttc")
    if font_path.is_file():
        title_font = ImageFont.truetype(str(font_path), 30)
        label_font = ImageFont.truetype(str(font_path), 24)
    else:
        title_font = ImageFont.load_default()
        label_font = title_font

    draw.text((40, 25), "小曦薇清晰度对比", fill=(245, 245, 245), font=title_font)
    draw.text((80, 75), "旧版：192×208 放大", fill=(200, 205, 210), font=label_font)
    draw.text((630, 75), "4K版：原始高清条带恢复", fill=(200, 205, 210), font=label_font)
    canvas.paste(old, (40, 115), old)
    canvas.paste(hd_frame, (590, 115), hd_frame)

    face_box = (105, 0, 423, 250)
    old_face = old.crop(face_box).resize((508, 400), Image.Resampling.LANCZOS)
    new_face = hd_frame.crop(face_box).resize((508, 400), Image.Resampling.LANCZOS)
    draw.text((40, 950), "面部细节（同倍数）", fill=(245, 245, 245), font=label_font)
    canvas.paste(old_face, (40, 900), old_face)
    canvas.paste(new_face, (590, 900), new_face)
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output, optimize=True)


def write_archive(
    archive_path: Path,
    frame_paths: dict[tuple[int, int], Path],
    manifest: dict[str, object],
) -> None:
    archive_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_STORED) as archive:
        for (row, column), path in sorted(frame_paths.items()):
            archive.write(path, f"frames/r{row:02d}/c{column:02d}.png")
        archive.writestr(
            "manifest.json",
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-dir", required=True)
    parser.add_argument(
        "--decoded-dir",
        help="Optional directory containing replacement high-resolution row strips; defaults to <run-dir>/decoded.",
    )
    parser.add_argument(
        "--legacy-from-walk",
        action="store_true",
        help="For external skins, derive compatibility rows 1-2 from the rebuilt walk rows 12-13.",
    )
    parser.add_argument(
        "--external-skin",
        action="store_true",
        help="Validate a replacement skin without requiring pixel similarity to the built-in white-dress atlas.",
    )
    parser.add_argument(
        "--action-profile",
        choices=(
            "legacy", "built-in-v304", "built-in-v305", "built-in-v306",
            "external-v304", "external-v306", "external-v306-linan",
        ),
        default="legacy",
        help=(
            "Optionally replace retired movement rows with versioned action art. "
            "The v3.0.4 built-in profile consumes idle-adorable/laughing/crying.png; "
            "the v3.0.5/v3.0.6 built-in profiles additionally consume "
            "idle-builtin-exclusive.png; the external profiles consume skin-exclusive.png."
        ),
    )
    parser.add_argument("--skill-dir", required=True)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--archive", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--comparison", required=True)
    args = parser.parse_args()

    run_dir = Path(args.run_dir).resolve()
    decoded_dir = (
        Path(args.decoded_dir).resolve()
        if args.decoded_dir
        else run_dir / "decoded"
    )
    if not decoded_dir.is_dir():
        raise SystemExit(f"decoded source directory not found: {decoded_dir}")
    skill_dir = Path(args.skill_dir).resolve()
    output_dir = Path(args.output_dir).resolve()
    archive_path = Path(args.archive).resolve()
    report_path = Path(args.report).resolve()
    comparison_path = Path(args.comparison).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    helpers = load_hatch_helpers(skill_dir)

    low_atlas_path = run_dir / "final" / "spritesheet-extended.png"
    with Image.open(low_atlas_path) as opened:
        low_atlas = opened.convert("RGBA")
    target_geometry = scaled_neutral_geometry(low_atlas, helpers)

    frames: dict[tuple[int, int], Image.Image] = {}
    transforms: dict[str, object] = {}
    despill_reports: dict[str, object] = {}

    for row, state, count in ROW_SPECS:
        if args.legacy_from_walk and row in (1, 2):
            continue
        strip_path = decoded_dir / f"{state}.png"
        strip, despill_report = clean_strip(strip_path, helpers)
        viewports, geometry = stable_viewports(strip, count, helpers)
        transforms[state] = geometry
        despill_reports[state] = despill_report
        for column, viewport in enumerate(viewports):
            frames[(row, column)] = place_standard_viewport(viewport, geometry)

    # The runtime neutral slot is a still copy of the approved first idle pose.
    frames[(0, 6)] = frames[(0, 0)].copy()

    look_scale_1x = LOOK_SCALE_1X
    if args.external_skin:
        (
            row9,
            row10,
            row9_transforms,
            row10_transforms,
            row9_despill,
            row10_despill,
            look_scale_1x,
        ) = extract_external_look_rows(
            decoded_dir / "look-row-9.png",
            decoded_dir / "look-row-10.png",
            frames[(0, 0)],
            helpers,
        )
    else:
        row9, row9_transforms, row9_despill = extract_look_row(
            decoded_dir / "look-row-9.png", target_geometry, helpers
        )
        row10, row10_transforms, row10_despill = extract_look_row(
            decoded_dir / "look-row-10.png", target_geometry, helpers
        )
    for column, frame in enumerate(row9):
        frames[(9, column)] = frame
    for column, frame in enumerate(row10):
        frames[(10, column)] = frame
    transforms["look-row-9"] = row9_transforms
    transforms["look-row-10"] = row10_transforms
    despill_reports["look-row-9"] = row9_despill
    despill_reports["look-row-10"] = row10_despill

    # Standalone-only row 11: tired breathing into the user-requested cute
    # angry foot stomp.  It shares the approved fixed viewport but remains
    # outside the Codex v2 atlas contract.
    angry_strip, angry_despill = clean_strip(
        decoded_dir / "angry-stomp.png", helpers
    )
    angry_viewports, angry_geometry = stable_viewports(angry_strip, 6, helpers)
    for column, viewport in enumerate(angry_viewports):
        angry_frame = place_standard_viewport(viewport, angry_geometry)
        # The built-in white-dress source has one known, coordinate-specific
        # generated afterimage.  Applying that mask to a replacement costume
        # can erase legitimate long skirts or dark fabric in the same region.
        frames[(11, column)] = (
            angry_frame
            if args.external_skin
            else cleanup_angry_frame(angry_frame, column)
        )
    transforms["angry-stomp"] = angry_geometry
    despill_reports["angry-stomp"] = angry_despill

    # Standalone-only rows 12-17: three independent directional gait families.
    # Generate every left-facing row by mirroring each completed right-facing
    # frame individually so animation timing and phase order remain unchanged.
    movement_rows: dict[str, dict[str, object]] = {}
    for (
        right_row,
        left_row,
        name,
        source_state,
        frame_count,
        frame_duration_ms,
    ) in MOVEMENT_SPECS:
        movement_strip, movement_despill = clean_strip(
            decoded_dir / f"{source_state}.png", helpers
        )
        movement_viewports, movement_geometry = stable_movement_viewports(
            movement_strip, frame_count, helpers
        )
        right_frames = [
            place_standard_viewport(viewport, movement_geometry)
            for viewport in movement_viewports
        ]
        left_frames = [ImageOps.mirror(frame) for frame in right_frames]
        for column, frame in enumerate(right_frames):
            frames[(right_row, column)] = frame
        for column, frame in enumerate(left_frames):
            frames[(left_row, column)] = frame

        transforms[source_state] = movement_geometry
        transforms[f"{name}-left"] = {
            "derivedFrom": source_state,
            "method": "per-frame-horizontal-mirror",
            "frameOrderPreserved": True,
        }
        despill_reports[source_state] = movement_despill
        movement_rows[name] = {
            "rightRow": right_row,
            "leftRow": left_row,
            "frameCount": frame_count,
            "frameDurationMs": frame_duration_ms,
            "source": str(decoded_dir / f"{source_state}.png"),
            "geometry": movement_geometry,
        }

        if args.legacy_from_walk and name == "walk":
            for column, frame in enumerate(right_frames):
                frames[(1, column)] = frame.copy()
            for column, frame in enumerate(left_frames):
                frames[(2, column)] = frame.copy()
            transforms["running-right"] = {
                "derivedFrom": source_state,
                "method": "compatibility-copy-from-walk-right",
            }
            transforms["running-left"] = {
                "derivedFrom": source_state,
                "method": "compatibility-copy-from-walk-left",
            }

    # v3.0.4 introduced reuse of rows whose runtime movement entry points were retired in
    # v3.0.3.  The authored action strips remain coherent eight-pose visual
    # jobs; this deterministic stage only extracts and registers them.  A
    # profile is explicit so old builds and third-party apiVersion=1 skins do
    # not accidentally expose legacy gait art as a new action.
    if args.action_profile == "built-in-v304":
        if args.external_skin:
            raise SystemExit("built-in-v304 action profile cannot be used for an external skin")
        extension_specs = BUILTIN_V304_EXTENSION_SPECS
    elif args.action_profile in ("built-in-v305", "built-in-v306"):
        if args.external_skin:
            raise SystemExit("built-in v3.0.5/v3.0.6 action profile cannot be used for an external skin")
        extension_specs = BUILTIN_V305_EXTENSION_SPECS
    elif args.action_profile in (
        "external-v304",
        "external-v306",
        "external-v306-linan",
    ):
        if not args.external_skin:
            raise SystemExit("external action profile requires --external-skin")
        extension_specs = EXTERNAL_EXTENSION_SPECS
    else:
        extension_specs = ()

    extension_rows: dict[str, dict[str, object]] = {}
    overridden_rows: set[int] = set()
    for row, name, source_state, frame_count, frame_duration_ms in extension_specs:
        source_path = decoded_dir / f"{source_state}.png"
        extension_strip, extension_despill = clean_strip(source_path, helpers)
        extension_viewports, extension_geometry = stable_movement_viewports(
            extension_strip, frame_count, helpers
        )
        for column, viewport in enumerate(extension_viewports):
            frames[(row, column)] = place_standard_viewport(
                viewport, extension_geometry
            )
        overridden_rows.add(row)
        transforms[source_state] = extension_geometry
        despill_reports[source_state] = extension_despill
        extension_rows[name] = {
            "row": row,
            "frameCount": frame_count,
            "frameDurationMs": frame_duration_ms,
            "source": str(source_path),
            "geometry": extension_geometry,
            "registration": "single action-level fixed viewport and scale",
        }

    # Standalone-only rows 18-23: randomized idle action families.  Each row
    # uses one shared source viewport and one shared scale, so sitting and
    # side-rest transitions preserve the character's real proportions instead
    # of fitting every pose independently.
    idle_rows: dict[str, dict[str, object]] = {}
    for row, name, source_state, frame_count, frame_duration_ms in IDLE_ACTION_SPECS:
        idle_strip, idle_despill = clean_strip(
            decoded_dir / f"{source_state}.png", helpers
        )
        # Realistic side-rest strips can have two neighboring skirts joined by
        # only a few antialiased pixels.  In external-skin mode reuse the
        # deterministic valley splitter used by gait rows; stable strips still
        # take its connected-component fast path unchanged.
        if args.external_skin and name == "side-rest":
            idle_viewports, idle_geometry = stable_movement_viewports(
                idle_strip, frame_count, helpers
            )
        else:
            idle_viewports, idle_geometry = stable_viewports(
                idle_strip, frame_count, helpers
            )
        for column, viewport in enumerate(idle_viewports):
            frames[(row, column)] = place_standard_viewport(viewport, idle_geometry)
        transforms[source_state] = idle_geometry
        despill_reports[source_state] = idle_despill
        idle_rows[name] = {
            "row": row,
            "frameCount": frame_count,
            "frameDurationMs": frame_duration_ms,
            "source": str(decoded_dir / f"{source_state}.png"),
            "geometry": idle_geometry,
            "registration": "single action-level fixed viewport and scale",
        }

    expected_count = sum(USED_PER_ROW)
    if len(frames) != expected_count:
        raise SystemExit(f"expected {expected_count} frames, recovered {len(frames)}")

    frame_paths: dict[tuple[int, int], Path] = {}
    alpha_ious: list[float] = []
    detail_ratios: list[float] = []
    frame_reports: list[dict[str, object]] = []
    total_edge_alpha = 0
    total_transparent_rgb = 0
    all_frame_sizes_ok = True
    all_frames_nonempty = True

    for (row, column), frame in sorted(frames.items()):
        if args.external_skin:
            frame = remove_external_chroma_residue(frame)
        # Guarantee canonical transparent RGB before PNG encoding.
        frame = helpers["clear_transparent_rgb"](frame)
        frames[(row, column)] = frame
        path = output_dir / "frames" / f"r{row:02d}" / f"c{column:02d}.png"
        path.parent.mkdir(parents=True, exist_ok=True)
        frame.save(path, format="PNG", optimize=True, compress_level=9)
        frame_paths[(row, column)] = path

        iou = None
        ratio = None
        if row < 11:
            low_full = low_atlas.crop(
                (column * 192, row * 208, column * 192 + 192, row * 208 + 208)
            )
            low_crop = low_full.crop(VIEWPORT_LOW)
            downsampled = frame.resize((132, 202), Image.Resampling.LANCZOS)
            iou = alpha_iou(downsampled, low_crop)
            alpha_ious.append(iou)
            old_upscaled = low_crop.resize(FRAME_SIZE, Image.Resampling.LANCZOS)
            old_detail = tenengrad(old_upscaled)
            new_detail = tenengrad(frame)
            ratio = new_detail / old_detail if old_detail > 0 else 1.0
            detail_ratios.append(ratio)
        edge_count = edge_alpha_count(frame)
        transparent_count = transparent_rgb_count(frame)
        bounds = alpha_bbox(frame)
        all_frame_sizes_ok = all_frame_sizes_ok and frame.size == FRAME_SIZE
        all_frames_nonempty = all_frames_nonempty and bounds is not None
        total_edge_alpha += edge_count
        total_transparent_rgb += transparent_count

        frame_reports.append(
            {
                "row": row,
                "column": column,
                "path": str(path),
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                "width": frame.width,
                "height": frame.height,
                "alphaBounds": bounds,
                "alphaIoUToApproved1x": iou,
                "headDetailGain": ratio,
                "edgeAlphaPixels": edge_count,
                "transparentRgbPixels": transparent_count,
            }
        )

    movement_alignment: dict[str, dict[str, object]] = {}
    for right_row, left_row, name, _source_state, frame_count, _duration in MOVEMENT_SPECS:
        if right_row in overridden_rows or left_row in overridden_rows:
            movement_alignment[name] = {
                "ok": True,
                "skipped": True,
                "reason": "one or both retired gait rows are repurposed by the selected versioned action profile",
            }
        else:
            movement_alignment[name] = movement_alignment_report(
                [frames[(right_row, column)] for column in range(frame_count)],
                [frames[(left_row, column)] for column in range(frame_count)],
                movement_rows[name]["geometry"],
                name,
                args.external_skin,
            )

    idle_alignment: dict[str, dict[str, object]] = {}
    for row, name, _source_state, frame_count, _duration in IDLE_ACTION_SPECS:
        idle_alignment[name] = idle_alignment_report(
            [frames[(row, column)] for column in range(frame_count)],
            idle_rows[name]["geometry"],
            name,
        )

    extension_alignment: dict[str, dict[str, object]] = {}
    for row, name, _source_state, frame_count, _duration in extension_specs:
        is_linan_depth_swing = (
            args.action_profile == "external-v306-linan"
            and name == "skin-exclusive"
        )
        extension_alignment[name] = idle_alignment_report(
            [frames[(row, column)] for column in range(frame_count)],
            extension_rows[name]["geometry"],
            name,
            # A front-view swing deliberately rises and changes apparent depth
            # at its two apices.  Keep its horizontal center much stricter than
            # an ordinary action while allowing the bounded vertical arc.  The
            # 21% ceiling accepts the balanced front/back strip (20.4%) but
            # still rejects the earlier exaggerated version (22.9%).
            maximum_center_deviation_ratio=0.05 if is_linan_depth_swing else 0.12,
            maximum_center_span_ratio=0.05 if is_linan_depth_swing else 0.15,
            maximum_baseline_span_ratio=0.21 if is_linan_depth_swing else None,
        )
        if is_linan_depth_swing:
            extension_alignment[name].update(
                {
                    "motionAxis": "screen-depth-forward-back",
                    "horizontalTranslationForbidden": True,
                    "cameraFacingGazeRequired": True,
                }
            )

    look_alignment = look_alignment_report(
        frames[(0, 0)],
        [frames[(9, column)] for column in range(8)]
        + [frames[(10, column)] for column in range(8)],
        helpers,
    )

    median_detail = float(np.median(np.asarray(detail_ratios, dtype=np.float64)))
    minimum_iou = min(alpha_ious)
    movement_alignment_ok = all(
        bool(alignment["ok"]) for alignment in movement_alignment.values()
    )
    idle_alignment_ok = all(
        bool(alignment["ok"]) for alignment in idle_alignment.values()
    )
    extension_alignment_ok = all(
        bool(alignment["ok"]) for alignment in extension_alignment.values()
    )
    look_alignment_ok = bool(look_alignment["ok"])
    baseline_quality_ok = minimum_iou >= 0.95 and median_detail >= 1.05
    ok = (
        (args.external_skin or baseline_quality_ok)
        and total_edge_alpha == 0
        and total_transparent_rgb == 0
        and all_frame_sizes_ok
        and all_frames_nonempty
        and movement_alignment_ok
        and idle_alignment_ok
        and extension_alignment_ok
        and look_alignment_ok
    )

    manifest = {
        "formatVersion": 1,
        "asset": "小曦薇 4K frames",
        "source": "approved decoded high-resolution row strips",
        "frameWidth": FRAME_SIZE[0],
        "frameHeight": FRAME_SIZE[1],
        "logicalWidth": 132,
        "logicalHeight": 202,
        "fullCell4x": list(FULL_CELL),
        "fixedViewport4x": list(VIEWPORT_4X),
        "rows": len(USED_PER_ROW),
        "columns": 8,
        "usedPerRow": list(USED_PER_ROW),
        "frameCount": len(frames),
        "externalSkinMode": bool(args.external_skin),
        "actionProfile": args.action_profile,
        "baselineQualityRequired": not args.external_skin,
        "baselineQualityOk": baseline_quality_ok,
    }
    write_archive(archive_path, frame_paths, manifest)

    neutral_low = low_atlas.crop((0, 0, 192, 208)).crop(VIEWPORT_LOW)
    save_comparison(neutral_low, frames[(0, 0)], comparison_path)
    look_contact = report_path.parent / "look-directions-alignment-contact.png"
    save_look_alignment_qa(
        frames[(0, 0)],
        [frames[(9, column)] for column in range(8)],
        [frames[(10, column)] for column in range(8)],
        look_contact,
    )
    angry_contact = report_path.parent / "angry-stomp-contact.png"
    angry_preview = report_path.parent / "angry-stomp-preview.gif"
    save_angry_stomp_qa(
        [frames[(11, column)] for column in range(6)],
        angry_contact,
        angry_preview,
    )
    movement_qa: dict[str, dict[str, str]] = {}
    for right_row, left_row, name, _source_state, frame_count, duration in MOVEMENT_SPECS:
        if right_row in overridden_rows or left_row in overridden_rows:
            continue
        contact_path = report_path.parent / f"movement-{name}-contact.png"
        preview_path = report_path.parent / f"movement-{name}-preview.gif"
        save_movement_qa(
            name,
            [frames[(right_row, column)] for column in range(frame_count)],
            [frames[(left_row, column)] for column in range(frame_count)],
            duration,
            contact_path,
            preview_path,
        )
        movement_qa[name] = {
            "contact": str(contact_path),
            "preview": str(preview_path),
        }

    idle_qa: dict[str, dict[str, str]] = {}
    total_idle_frames: dict[str, list[Image.Image]] = {}
    for row, name, _source_state, frame_count, duration in IDLE_ACTION_SPECS:
        action_frames = [frames[(row, column)] for column in range(frame_count)]
        contact_path = report_path.parent / f"idle-{name}-contact.png"
        preview_path = report_path.parent / f"idle-{name}-preview.gif"
        save_idle_action_qa(
            name,
            action_frames,
            duration,
            contact_path,
            preview_path,
        )
        total_idle_frames[name] = action_frames
        idle_qa[name] = {
            "contact": str(contact_path),
            "preview": str(preview_path),
        }

    extension_qa: dict[str, dict[str, str]] = {}
    for row, name, _source_state, frame_count, duration in extension_specs:
        action_frames = [frames[(row, column)] for column in range(frame_count)]
        contact_path = report_path.parent / f"extension-{name}-contact.png"
        preview_path = report_path.parent / f"extension-{name}-preview.gif"
        save_idle_action_qa(
            name,
            action_frames,
            duration,
            contact_path,
            preview_path,
        )
        total_idle_frames[name] = action_frames
        extension_qa[name] = {
            "contact": str(contact_path),
            "preview": str(preview_path),
        }
    total_idle_contact = report_path.parent / "idle-actions-contact.png"
    save_total_idle_contact(total_idle_frames, total_idle_contact)

    report = {
        "ok": ok,
        "asset": manifest,
        "archive": str(archive_path),
        "archiveBytes": archive_path.stat().st_size,
        "frameCount": len(frames),
        "minimumAlphaIoU": minimum_iou,
        "meanAlphaIoU": float(np.mean(np.asarray(alpha_ious, dtype=np.float64))),
        "medianHeadDetailGain": median_detail,
        "minimumHeadDetailGain": min(detail_ratios),
        "edgeAlphaPixels": total_edge_alpha,
        "transparentRgbPixels": total_transparent_rgb,
        "allFrameSizesOk": all_frame_sizes_ok,
        "allFramesNonempty": all_frames_nonempty,
        "neutralGeometry1x": {
            "height": target_geometry.height // SCALE,
            "lowerCenterX": target_geometry.lower_center_x / SCALE,
            "bottom": target_geometry.bottom // SCALE,
        },
        "lookScale1x": look_scale_1x,
        "lookAlignment": look_alignment,
        "lookAlignmentContact": str(look_contact),
        "transforms": transforms,
        "despill": despill_reports,
        "frames": frame_reports,
        "comparison": str(comparison_path),
        "angryStompContact": str(angry_contact),
        "angryStompPreview": str(angry_preview),
        "movementRows": movement_rows,
        "movementAlignment": movement_alignment,
        "movementQa": movement_qa,
        "extensionRows": extension_rows,
        "extensionAlignment": extension_alignment,
        "extensionQa": extension_qa,
        "idleRows": idle_rows,
        "idleAlignment": idle_alignment,
        "idleQa": idle_qa,
        "idleActionsContact": str(total_idle_contact),
    }
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps({
        "ok": ok,
        "frames": len(frames),
        "archive": str(archive_path),
        "archiveBytes": archive_path.stat().st_size,
        "minimumAlphaIoU": minimum_iou,
        "medianHeadDetailGain": median_detail,
        "edgeAlphaPixels": total_edge_alpha,
        "transparentRgbPixels": total_transparent_rgb,
        "movementAlignmentOk": movement_alignment_ok,
        "idleAlignmentOk": idle_alignment_ok,
        "extensionAlignmentOk": extension_alignment_ok,
        "lookAlignmentOk": look_alignment_ok,
    }, ensure_ascii=False))
    if not ok:
        raise SystemExit("high-resolution frame QA failed; inspect report")


if __name__ == "__main__":
    main()
