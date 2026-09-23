"""Builds assets/fonts/alien_glyphs.ttf - the placeholder in-world scripts.

One block of 36 codepoints per language, at the LanguageData.glyphBase the
.tres names: 26 letter glyphs then 10 numeral glyphs. Glyph shapes are
generated from a per-block seed, so this is scaffolding for real lettering
art, not the final look - redraw the outlines, keep the codepoint layout.

    python asset_src/fonts/build_alien_glyphs.py

Godot does not watch asset_src/, so rerun this by hand after editing it.
"""

import os
import random

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

UPEM = 1000
ASCENT = 800
DESCENT = 200
ADVANCE = 560

# Lattice the strokes snap to. Keeps every glyph sharing one skeleton, which
# is what makes a block read as a single alphabet rather than 36 doodles.
XS = (90, 280, 470)
YS = (0, 180, 360, 540, 720)

# Numeral glyphs carry an extra baseline bar so a number stays legible AS a
# number when the player has the language's letters but not its Numbers.
NUMERAL_BAR_Y = -70
NUMERAL_BAR_T = 30

LETTERS = 26
NUMERALS = 10
BLOCK = LETTERS + NUMERALS


def quad(x0, y0, x1, y1, t):
    """A stroke from (x0,y0) to (x1,y1) as a thickness-2t rectangle contour."""
    dx, dy = x1 - x0, y1 - y0
    length = (dx * dx + dy * dy) ** 0.5
    if length == 0:
        return None
    # Perpendicular, scaled to the half thickness.
    px, py = -dy / length * t, dx / length * t
    return [
        (x0 + px, y0 + py),
        (x1 + px, y1 + py),
        (x1 - px, y1 - py),
        (x0 - px, y0 - py),
    ]


def draw(contours):
    pen = TTGlyphPen(None)
    for pts in contours:
        if not pts:
            continue
        pen.moveTo(pts[0])
        for p in pts[1:]:
            pen.lineTo(p)
        pen.closePath()
    return pen.glyph()


def glyph_contours(rng, style, numeral):
    """One glyph: 3-4 strokes picked off the lattice in the block's style."""
    thickness, vertical_bias, top_row = style
    contours = []
    # A spine gives every glyph an anchor stroke, so shapes stay letter-like.
    col = rng.randrange(len(XS))
    if vertical_bias:
        contours.append(quad(XS[col], YS[0], XS[col], YS[top_row], thickness))
    else:
        row = rng.randrange(1, top_row + 1)
        contours.append(quad(XS[0], YS[row], XS[-1], YS[row], thickness))

    for _ in range(rng.randrange(2, 4)):
        x0 = XS[rng.randrange(len(XS))]
        x1 = XS[rng.randrange(len(XS))]
        y0 = YS[rng.randrange(0, top_row + 1)]
        y1 = YS[rng.randrange(0, top_row + 1)]
        if x0 == x1 and y0 == y1:
            continue
        contours.append(quad(x0, y0, x1, y1, thickness))

    if numeral:
        contours.append(
            quad(XS[0], NUMERAL_BAR_Y, XS[-1], NUMERAL_BAR_Y, NUMERAL_BAR_T)
        )
    return contours


def build_block(prefix, base, seed, style, glyphs, cmap):
    rng = random.Random(seed)
    for i in range(BLOCK):
        name = f"{prefix}{i:02d}"
        glyphs[name] = draw(glyph_contours(rng, style, i >= LETTERS))
        cmap[base + i] = name


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.normpath(os.path.join(here, "..", "..", "assets", "fonts", "alien_glyphs.ttf"))

    glyphs = {".notdef": draw([])}
    cmap = {}
    # (stroke half-thickness, vertical spine, tallest lattice row)
    # Vyeshal reads tall and angular; Muddish squat and heavy.
    build_block("vye", 0xE000, 0x5679, (34, True, 4), glyphs, cmap)
    build_block("mud", 0xE100, 0x1C3B, (56, False, 3), glyphs, cmap)

    order = [".notdef"] + sorted(n for n in glyphs if n != ".notdef")
    metrics = {n: (ADVANCE, 0) for n in order}
    metrics[".notdef"] = (ADVANCE, 0)

    fb = FontBuilder(UPEM, isTTF=True)
    fb.setupGlyphOrder(order)
    fb.setupCharacterMap(cmap)
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics(metrics)
    fb.setupHorizontalHeader(ascent=ASCENT, descent=-DESCENT)
    fb.setupNameTable({
        "familyName": "Hike Alien Glyphs",
        "styleName": "Regular",
        "psName": "HikeAlienGlyphs-Regular",
        "version": "1.0",
    })
    fb.setupOS2(sTypoAscender=ASCENT, sTypoDescender=-DESCENT, usWinAscent=ASCENT, usWinDescent=DESCENT)
    fb.setupPost()
    fb.save(out)
    print(f"wrote {out}: {len(cmap)} glyphs")


if __name__ == "__main__":
    main()
