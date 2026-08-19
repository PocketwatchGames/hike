"""Imports the three climb-growth tiles from the external Synty terrain art.

Each BlockData.climbGrowthSurface names one of these; see the root CLAUDE.md
block model and resources/data/surfaces/CLAUDE.md for the atlas half.

Sources (browse-only, see docs/asset-sourcing.md) are Synty PolygonNatureBiomes
PNB_Meadow_Forest/Terrain — moss-and-lichen-ON-ROCK blend tiles, which is exactly
the structure a climb overlay wants:

  Climb_Lichen <- Rock_Rough_Moss_Red  (orange crust on grey rock)   desert
  Climb_Ivy    <- Rock_Moss            (dark dense green on rock)    forest/mountain
  Climb_Moss   <- Moss_Rock            (bright moss over rock plates) swamp

**Every climb-growth tile needs bare rock in it.** The shader blends the crust
over the wall on relief, so a tile that is growth edge-to-edge has no gaps for
stone to show through and reads as a painted decal rather than something growing.
All three sources have that built in — it is why these beat a plain moss carpet.

**Colour and normal are the source art, untouched. Only HEIGHT is derived** — the
Synty terrain pack ships no height maps, and the atlas's alpha channel needs one
because `height_blend_weight` is what decides where the crust wins over the rock.
A flat height would make the growth a uniform wash. It is keyed on each tile's
authored growth HUE, so growth sits proud of stone — the physical reading, and
what the blend needs. Verify a change with the per-material means, not by eye:
growth should land near 0.8 and rock near 0.25, and a thumbnail is easy to
misread.

Re-run after changing a source or knob, then rebuild the atlas:
    python tools/gen_climb_growth.py
    Godot ... --headless -- "atlas_rebuild 1"
Source art is read-only; only assets/textures/terrain/Climb_* is written.
"""
import os

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

SLOT = 256  # atlas slot size; must match VoxelAtlasManifest.Slot
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = (r"C:/Users/andy/source/AssetDump/Assets/Synty/PolygonNatureBiomes"
       r"/PNB_Meadow_Forest/Terrain/")

LUMA = np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)

# (output folder/prefix, source color, source normal, growth hue in degrees).
# Synty reuses one normal map across a tile's colour variants, so Red borrows the
# plain Rough_Moss one.
#
# The hue is authored per tile rather than detected, because chroma alone gets it
# BACKWARDS on Rock_Moss: that tile's rock is a warm tan and its moss a dark
# desaturated green, so "most colourful = growth" marks the stone as the raised
# material and the blend then eats the growth instead of the rock.
# The rock base is the SIDE surface of the blocks each tile grows on:
# desert cliffs wear DesertWall (cliff006), mountain/forest Rock (ForestRock),
# and swamp + caves Stone.
TILES = [
    ("Climb_Lichen", "ClimbLichen",
     "Rock_Rough_Moss_Red_Texture_01.png", "Rock_Rough_Moss_Normals_01.png", 25.0, "cliff006"),
    ("Climb_Ivy", "ClimbIvy",
     "Rock_Moss_Texture_01.png", "Rock_Moss_Normals.png", 100.0, "forestrock"),
    ("Climb_Moss", "ClimbMoss",
     "Moss_Rock_Texture_01.png", "Moss_Rock_Normals_01.png", 90.0, "stone"),
]

# The tile is its zone's WALL ROCK with growth composited on top, rather than the
# Synty blend tile's own generic rock. Two reasons, both learned the hard way:
#
#  - The overlay wins the height blend outright at the coverage the mesher marks
#    (verified: debug_overlay_cov showed blend = 1). So whatever rock is painted
#    into the tile IS what draws — the terrain never shows through it. A generic
#    tan rock therefore read as dirt smeared over the growth.
#  - Because the tile draws in full, using the WALL's rock makes the lip continue
#    the wall's material up over the edge and onto the terrace, tying the two
#    together instead of dropping a foreign texture on the ledge.
#
# Growth stays visually dominant through the HEIGHT map, not the colour: growth
# sits in the upper band and rock in the lower, and the shader's texture-space
# cavity term darkens the low half, so the rock recedes behind the growth.
ROCK_BASES = {
    "cliff006": "Stylized_Cliff_Rock_006_SD/Stylized_Cliff_Rock_006_%s.png",
    "forestrock": "Stylized_Rock/ForestRock_%s.png",
    "stone": "Stylized_Rocks_002_4K/Stylized_Rocks_002_%s.png",
}
# Height bands. Growth occupies [GROWTH_FLOOR, 1] and rock [0, ROCK_CEIL], so the
# two never overlap and the growth always wins the relief comparison against it.
ROCK_CEIL = 0.45
GROWTH_FLOOR = 0.55

# Half-width of the hue window, in degrees. Wide enough to hold a material's own
# variation, narrow enough to reject a tan rock sitting next to a green moss.
HUE_TOLERANCE = 45.0
# Saturation below this is treated as bare rock whatever its hue — this is what
# separates the lichen tile, whose rock is neutral grey rather than a rival hue.
SAT_FLOOR = 0.10

# How much of the height comes from the growth/rock split vs. fine luminance
# detail. All split = flat plateaus with no texture inside a colony; all detail =
# no separation between growth and stone, which is the case the blend needs most.
SPLIT_WEIGHT = 0.68
# Percentiles the growth mask is stretched between, so a tile that is mostly one
# material still uses the full height range instead of a narrow sliver.
MASK_LO_PCT, MASK_HI_PCT = 8, 92


def load(path, size=SLOT):
    im = Image.open(path).convert("RGB")
    if im.size != (size, size):
        im = im.resize((size, size), Image.LANCZOS)
    return np.asarray(im).astype(np.float32) / 255.0


def stretch(x, lo_pct=2, hi_pct=98):
    lo, hi = np.percentile(x, (lo_pct, hi_pct))
    return np.clip((x - lo) / max(hi - lo, 1e-4), 0.0, 1.0)


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


_written = {}

for folder, prefix, color_src, normal_src, growth_hue, rock_base in TILES:
    out = os.path.join(REPO, "assets", "textures", "terrain", folder)
    os.makedirs(out, exist_ok=True)

    color = load(SRC + color_src)
    normal = load(SRC + normal_src)

    # Growth is what sits near the authored hue AND carries some saturation;
    # everything else is rock. Both terms are needed: hue alone would keep a
    # grey rock whose noise happens to lean green, saturation alone is the
    # inverted case described at TILES.
    mx, mn = color.max(-1), color.min(-1)
    sat = (mx - mn) / np.maximum(mx, 1e-4)
    hsv = np.asarray(Image.fromarray((color * 255).round().astype(np.uint8), "RGB")
                     .convert("HSV"), dtype=np.float32)
    hue = hsv[..., 0] * (360.0 / 255.0)
    # Circular distance, so a hue near 0/360 doesn't read as maximally far.
    dh = np.abs((hue - growth_hue + 180.0) % 360.0 - 180.0)
    near_hue = 1.0 - smoothstep(HUE_TOLERANCE, HUE_TOLERANCE * 1.6, dh)
    growth = near_hue * smoothstep(SAT_FLOOR, SAT_FLOOR * 2.5, sat)
    growth = stretch(growth, MASK_LO_PCT, MASK_HI_PCT)

    # Fine relief inside each material: local luminance detail, high-passed so a
    # broad light/dark gradient across the tile doesn't tilt the whole surface.
    lum = color @ LUMA
    blur = np.asarray(
        Image.fromarray((lum * 255).astype(np.uint8))
        .resize((SLOT // 8, SLOT // 8), Image.LANCZOS)
        .resize((SLOT, SLOT), Image.BICUBIC), dtype=np.float32) / 255.0
    detail = stretch(lum - blur)

    # Composite growth over the zone's wall rock.
    rock_pat = os.path.join(REPO, "assets", "textures", "terrain", ROCK_BASES[rock_base])
    rock_col = load(rock_pat % "basecolor")
    rock_nrm = load(rock_pat % "normal")
    rock_h = np.asarray(Image.open(rock_pat % "height").convert("L")
                        .resize((SLOT, SLOT), Image.LANCZOS), dtype=np.float32) / 255.0

    gw = growth[..., None]
    color = rock_col * (1.0 - gw) + color * gw
    normal = rock_nrm * (1.0 - gw) + normal * gw

    # Two non-overlapping height bands, so growth always beats the rock it sits
    # on while each keeps its own internal relief.
    height = np.where(growth > 0.5,
                      GROWTH_FLOOR + (1.0 - GROWTH_FLOOR) * detail,
                      ROCK_CEIL * stretch(rock_h))
    height = np.clip(height, 0.0, 1.0)

    Image.fromarray((color * 255).round().astype(np.uint8), "RGB").save(
        os.path.join(out, prefix + "_basecolor.png"))
    Image.fromarray((normal * 255).round().astype(np.uint8), "RGB").save(
        os.path.join(out, prefix + "_normal.png"))
    Image.fromarray((height * 255).round().astype(np.uint8), "L").save(
        os.path.join(out, prefix + "_height.png"))
    _written[prefix] = (color.tobytes(), height.tobytes())
    print(f"{prefix:12} <- {color_src:34} growth {100 * (growth > 0.5).mean():4.1f}%"
          f"  height sd {height.std():.3f}")

# Two tiles rendering identically is silent and costly: the atlas bakes fine, the
# catalog validates, and the only symptom is a zone quietly wearing another
# zone's growth. Fail loudly instead.
for _a in _written:
    for _b in _written:
        if _a < _b and _written[_a] == _written[_b]:
            raise SystemExit(f"ERROR: {_a} and {_b} produced identical art - "
                             "check TILES for a duplicated source.")
print("ok: all tiles distinct")
