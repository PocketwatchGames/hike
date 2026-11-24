"""Imports the cracked-rock overlay tile (surface_cracked_rock) from external art.

The overlay marks a breakable "bomb wall" patch, so it has to read as DAMAGED
rock at the isometric camera while still being rock. It is drawn on cliff and
cave walls of every zone, so it cannot match one wall exactly; it is recoloured
to the game's shared neutral rock tone instead.

Source (browse-only, see docs/asset-sourcing.md): Stylized Desert Materials
"Desert Rock" — a dense craquelure of dark fissures over rock plates, shipped with
colour, normal, height and AO.

  - Colour: the source's own luminance structure (plates + fissures), recoloured
    from its orange desert hue onto ROCK_TINT — the mean of ForestRock, Stylized
    Rocks 002 and Cliff Rock 006, the side surfaces of the blocks it lands on — and
    keeping a little of the source chroma so it isn't a dead grey. Fissures are
    then deepened with the source AO so they survive the 256px slot and the
    pixel-art camera.
  - Normal: the source art, resampled only.
  - Height: DERIVED. The shipped height is dominated by broad low-frequency
    swell with the cracks barely in it, which is useless to `height_blend_weight`
    and the cavity term. It is rebuilt so fissures sit at the bottom and plates
    stand proud: AO (which is the crack network) blended with the high-passed
    source height. Verify with the per-region means printed below (fissures low,
    plates high), never by eye.

The climb overlays sit atop the same atlas; see tools/gen_climb_growth.py.

Re-run after changing a knob, then rebuild the atlas:
    python tools/gen_cracked_rock.py
    Godot ... --headless -- "atlas_rebuild 1"
Source art is read-only; only asset_src/textures/terrain/Cracked_Rock is written.
"""
import os

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

SLOT = 256  # atlas slot size; must match VoxelAtlasManifest.Slot
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = (r"C:/Users/andy/source/AssetDump/Assets/Stylized Desert Materials"
       r"/Textures/Desert Rock/Desert_Rock_%s.png")
OUT_DIR = os.path.join(REPO, "asset_src", "textures", "terrain", "Cracked_Rock")
PREFIX = "CrackedRock"

LUMA = np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)

# The game's wall rocks, whose mean colour the tile is recoloured onto.
ROCK_REFS = [
    "Stylized_Rock/ForestRock_basecolor.png",
    "Stylized_Rocks_002_4K/Stylized_Rocks_002_basecolor.png",
    "Stylized_Cliff_Rock_006_SD/Stylized_Cliff_Rock_006_basecolor.png",
]
# Share of the source's own chroma kept on top of the neutral tint. 0 = pure grey
# rock; 1 = the original orange.
CHROMA_KEEP = 0.15
# Plate brightness relative to the reference rocks. The AO deepening already
# darkens the tile overall, so the patch reads a little darker than its wall.
BRIGHTNESS = 1.0
# How hard the AO darkens the fissures (exponent on AO; 0 = off).
CRACK_DEEPEN = 1.6

# Height = HEIGHT_AO_WEIGHT * AO + (1 - w) * high-passed source height.
HEIGHT_AO_WEIGHT = 0.65
# AO below this percentile counts as fissure for the verification printout.
CRACK_PCT = 15


def load_rgb(path, size=SLOT):
    im = Image.open(path).convert("RGB")
    if im.size != (size, size):
        im = im.resize((size, size), Image.LANCZOS)
    return np.asarray(im).astype(np.float32) / 255.0


def load_l(path, size=SLOT):
    im = Image.open(path).convert("L")
    if im.size != (size, size):
        im = im.resize((size, size), Image.LANCZOS)
    return np.asarray(im).astype(np.float32) / 255.0


def stretch(x, lo_pct=1, hi_pct=99):
    lo, hi = np.percentile(x, (lo_pct, hi_pct))
    return np.clip((x - lo) / max(hi - lo, 1e-4), 0.0, 1.0)


def wrap_blur(x, down):
    """Low-pass that still tiles: tile 3x3, downsample, upsample, crop centre."""
    n = x.shape[0]
    big = np.tile(x, (3, 3))
    im = Image.fromarray((big * 255).astype(np.uint8))
    im = im.resize((3 * n // down, 3 * n // down), Image.LANCZOS).resize((3 * n, 3 * n), Image.BICUBIC)
    return (np.asarray(im, dtype=np.float32) / 255.0)[n:2 * n, n:2 * n]


os.makedirs(OUT_DIR, exist_ok=True)

color = load_rgb(SRC % "basecolor")
normal = load_rgb(SRC % "normal")
src_h = load_l(SRC % "Height")
ao = load_l(SRC % "AmbientOcclusion")

terrain = os.path.join(REPO, "asset_src", "textures", "terrain")
rock_tint = np.mean([load_rgb(os.path.join(terrain, p)).reshape(-1, 3).mean(0) for p in ROCK_REFS], axis=0)

# Recolour: keep the source's luminance pattern, move it onto the rock tint.
lum = color @ LUMA
chroma = color - lum[..., None]
rel = lum / max(lum.mean(), 1e-4)
out_col = rock_tint[None, None, :] * rel[..., None] * BRIGHTNESS + chroma * CHROMA_KEEP
out_col *= (np.clip(ao, 0.0, 1.0) ** CRACK_DEEPEN)[..., None]
out_col = np.clip(out_col, 0.0, 1.0)

# Height: fissures low, plates proud.
hp = stretch(src_h - wrap_blur(src_h, 16))
height = np.clip(HEIGHT_AO_WEIGHT * stretch(ao) + (1.0 - HEIGHT_AO_WEIGHT) * hp, 0.0, 1.0)
height = stretch(height)

Image.fromarray((out_col * 255).round().astype(np.uint8), "RGB").save(os.path.join(OUT_DIR, PREFIX + "_basecolor.png"))
Image.fromarray((normal * 255).round().astype(np.uint8), "RGB").save(os.path.join(OUT_DIR, PREFIX + "_normal.png"))
Image.fromarray((height * 255).round().astype(np.uint8), "L").save(os.path.join(OUT_DIR, PREFIX + "_height.png"))

crack = ao <= np.percentile(ao, CRACK_PCT)
print(f"rock tint {np.round(rock_tint, 3)}  tile mean {np.round(out_col.reshape(-1, 3).mean(0), 3)}")
print(f"height: fissures {height[crack].mean():.3f}  plates {height[~crack].mean():.3f}  sd {height.std():.3f}")
print(f"colour luma: fissures {(out_col @ LUMA)[crack].mean():.3f}  plates {(out_col @ LUMA)[~crack].mean():.3f}")
if height[crack].mean() >= height[~crack].mean():
    raise SystemExit("ERROR: height inverted - fissures must sit below the plates.")
print("ok")
