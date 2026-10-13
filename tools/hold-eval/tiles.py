"""Drawing for the hold-by-hold comparison: crops of the full-resolution panel photo with outlines on them.

A tile is a row of square crops of the same spot at the same scale, one per layer (what is on the wall today, what
the current detector makes of it, what the improved one makes of it). In every crop the holds in focus are drawn
strong, their neighbours thin and grey. Used by compare.py; nothing here reads a database.
"""
import math

from PIL import Image, ImageDraw

import geometry as geo

CELL = 180
FOCUS = (255, 255, 255)
BASE = (255, 196, 0)
CHILD = (20, 110, 200)
NEIGHBOUR = (150, 150, 150)
EMPTY = (38, 38, 42)


class Layer:
    """One crop of a tile: every hold of that layer on the panel, and which of them are in focus."""

    def __init__(self, holds: list[dict], focus: list[int], bases: list[int] | None = None, children: list[int] | None = None):
        self.holds, self.focus = holds, set(focus)
        self.bases, self.children = set(bases or []), set(children or [])


def window(holds: list[dict], width: int, height: int, minimum: float = 56.0) -> tuple[float, float, float]:
    """Centre and half-size (pixels) of the square that shows the given holds with room around them."""
    xs, ys = [], []
    for hold in holds:
        for x, y in geo.outline_px(hold, width, height):
            xs.append(x)
            ys.append(y)
    cx, cy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
    half = max(minimum, 0.8 * max(max(xs) - min(xs), max(ys) - min(ys)))
    return cx, cy, half


def crop(photo: Image.Image, cx: float, cy: float, half: float, layer: Layer | None, cell: int = CELL) -> Image.Image:
    """One square crop with the layer's outlines; a dark cell with a dash when the layer has nothing here."""
    box = (int(round(cx - half)), int(round(cy - half)), int(round(cx + half)), int(round(cy + half)))
    scale = 2  # drawn at twice the size and shrunk, for smooth lines
    tile = photo.crop(box).resize((cell * scale, cell * scale), Image.BILINEAR)
    if layer is None:
        return Image.new("RGB", (cell, cell), EMPTY)
    draw = ImageDraw.Draw(tile)
    k = cell * scale / (2 * half)
    width, height = photo.size

    def points(hold: dict) -> list[tuple[float, float]]:
        return [((x - box[0]) * k, (y - box[1]) * k) for x, y in geo.outline_px(hold, width, height)]

    def near(hold: dict) -> bool:
        r = max(hold["radius"] * max(width, height), 1)
        return abs(hold["x"] * width - cx) < half + 3 * r and abs(hold["y"] * height - cy) < half + 3 * r

    for i, hold in enumerate(layer.holds):
        if i not in layer.focus and i not in layer.bases and i not in layer.children and near(hold):
            ring = points(hold)
            draw.line(ring + ring[:1], fill=NEIGHBOUR, width=scale)
    for group, colour in ((layer.bases, BASE), (layer.focus, FOCUS), (layer.children, CHILD)):
        for i in sorted(group):
            ring = points(layer.holds[i])
            draw.line(ring + ring[:1], fill=(0, 0, 0), width=3 * scale + 2, joint="curve")
            draw.line(ring + ring[:1], fill=colour, width=2 * scale, joint="curve")
            if layer.holds[i].get("outline") is None:
                _dash_mark(draw, ring, colour, scale)
    if not layer.focus and not layer.bases and not layer.children:
        draw.line([(cell * scale * 0.4, cell * scale * 0.5), (cell * scale * 0.6, cell * scale * 0.5)], fill=(230, 60, 60), width=3 * scale)
    return tile.resize((cell, cell), Image.LANCZOS)


def _dash_mark(draw: ImageDraw.ImageDraw, ring: list[tuple[float, float]], colour: tuple[int, int, int], scale: int) -> None:
    """A plain circle (no traced outline) gets a small cross at its centre, so it reads as "circle only"."""
    cx = sum(p[0] for p in ring) / len(ring)
    cy = sum(p[1] for p in ring) / len(ring)
    d = 4 * scale
    draw.line([(cx - d, cy), (cx + d, cy)], fill=colour, width=scale)
    draw.line([(cx, cy - d), (cx, cy + d)], fill=colour, width=scale)


def tile(photo: Image.Image, layers: list[Layer | None], frame: tuple[float, float, float]) -> Image.Image:
    """The crops of one spot side by side."""
    cx, cy, half = frame
    out = Image.new("RGB", (CELL * len(layers), CELL), EMPTY)
    for i, layer in enumerate(layers):
        out.paste(crop(photo, cx, cy, half, layer), (i * CELL, 0))
    return out


def sheets(tiles: list[Image.Image], columns: int = 10, rows: int = 10) -> list[Image.Image]:
    """Packs tiles into sheets of columns x rows; the last sheet is only as tall as it needs to be."""
    if not tiles:
        return []
    w, h = tiles[0].size
    out = []
    for start in range(0, len(tiles), columns * rows):
        chunk = tiles[start:start + columns * rows]
        sheet = Image.new("RGB", (columns * w, math.ceil(len(chunk) / columns) * h), EMPTY)
        for i, t in enumerate(chunk):
            sheet.paste(t, ((i % columns) * w, (i // columns) * h))
        out.append(sheet)
    return out


def panel_image(photo: Image.Image, holds: list[dict], colours: dict[int, tuple[int, int, int]] | None = None,
                longest: int = 2400, default: tuple[int, int, int] = (255, 255, 255)) -> Image.Image:
    """The whole panel, shrunk, with every outline; colours maps hold positions to a highlight colour."""
    width, height = photo.size
    k = min(1.0, longest / max(width, height))
    image = photo.resize((int(width * k), int(height * k)), Image.LANCZOS)
    draw = ImageDraw.Draw(image)
    colours = colours or {}
    for i, hold in sorted(enumerate(holds), key=lambda e: e[0] in colours):
        ring = [(x * k, y * k) for x, y in geo.outline_px(hold, width, height)]
        colour = colours.get(i, default)
        draw.line(ring + ring[:1], fill=(0, 0, 0), width=4 if i in colours else 3)
        draw.line(ring + ring[:1], fill=colour, width=3 if i in colours else 1)
    return image
