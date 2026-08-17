"""Generates the tiling source art for the climbable-ledge lichen overlay.

Writes assets/textures/terrain/Climb_Lichen/{basecolor,normal,height}.png, which
resources/data/surfaces/voxel_atlas_manifest.tres names as one atlas layer. This
is generated rather than sourced because the look is specific (bright orange
crustose lichen, readable as a gameplay affordance at isometric distance) and
wants to be tunable — every knob is at the top of this file. Swap the three PNGs
for authored/photographed art any time; nothing in the game references this
script, only its output.

Everything is periodic: the value-noise lattice wraps, and the normal is derived
with np.roll, so the tile has no seam at any octave.

Coverage of the overlay is NOT in the color — the terrain shader blends this
layer over rock by comparing HEIGHT relief (see height_blend_weight in
voxel_clip.gdshader). So the height map carries the patch shape: rosettes stand
proud and win, the gaps between them sit low and let the rock show through.

    python tools/gen_climb_lichen.py
"""
import os

import numpy as np
from PIL import Image

SIZE = 256  # must match SLOT in stitch_voxel_atlas.py
SEED = 20260817

# --- Look knobs -----------------------------------------------------------
# Patch shape. PATCH_FREQ is how many rosette clusters span the tile; raising
# COVERAGE grows them until they merge into a crust.
# PATCH_FREQ is in cells per TILE, and the terrain shader maps one tile to
# 1/tile_uv_scale metres (~3.3m at the current 0.3), so freq 9 puts a colony at
# roughly 0.37m — small enough that the strip breaks into many short runs rather
# than a few long ones.
# PATCH_FREQ is the lever for colony size: thresholding an FBM gives blobs whose
# scale is set by the BASE octave (it carries amplitude 1 against the rest's
# 0.5, 0.25, ...), so adding octaves adds edge detail but does NOT shrink the
# blobs — only raising this does.
PATCH_FREQ = 18
PATCH_OCTAVES = 4  # edge fray; see above, these do not set colony size
COVERAGE = 0.56
PATCH_EDGE = 0.12  # rosette rim softness, in noise-value space

# Areole cracking — the crazed-mud pattern a crustose lichen dries into. This is
# most of what makes it read as lichen rather than orange paint. Keep it well
# above PATCH_FREQ or the cracks stop reading as detail WITHIN a colony.
CRACK_FREQ = 40
CRACK_WIDTH = 0.055
CRACK_DEPTH = 0.55

# Fine granularity on top of everything, so the crust is never flat.
GRAIN_FREQ = 64
GRAIN_AMOUNT = 0.18

# Colors. Real Xanthoria/Caloplaca run bright orange with a paler, more yellow
# margin and dark crevices — the margin contrast is what carries the shape at
# distance, so keep RIM well clear of MID.
COL_MID = np.array([228, 108, 24], dtype=float)
COL_RIM = np.array([255, 190, 70], dtype=float)
COL_CRACK = np.array([116, 48, 12], dtype=float)
# Bare rock in the gaps. Kept dark and desaturated so any bleed past the height
# blend reads as shadow rather than a colored halo around the patch.
COL_BARE = np.array([84, 76, 68], dtype=float)

# Height, in the atlas' 0..1 A channel. The mean matters: the shader subtracts
# each layer's own mean (tile_height_mid) before comparing relief, so what
# decides coverage is the SPREAD between rosette and gap, not the absolute.
HEIGHT_LICHEN = 0.82
HEIGHT_BARE = 0.18

NORMAL_STRENGTH = 2.6

OUT_DIR = os.path.join(
    os.path.dirname(__file__), "..", "assets", "textures", "terrain", "Climb_Lichen"
)


def tileable_value_noise(size, freq, rng):
    """Bilinear value noise on a lattice that wraps, so it tiles at any freq."""
    lattice = rng.random((freq, freq))
    coords = np.arange(size) * (freq / size)
    i0 = np.floor(coords).astype(int) % freq
    i1 = (i0 + 1) % freq
    t = coords - np.floor(coords)
    t = t * t * (3.0 - 2.0 * t)  # smoothstep

    # Outer-product the two axes into full-tile index/weight grids.
    y0, x0 = np.meshgrid(i0, i0, indexing="ij")
    y1, x1 = np.meshgrid(i1, i1, indexing="ij")
    ty, tx = np.meshgrid(t, t, indexing="ij")

    n00 = lattice[y0, x0]
    n10 = lattice[y0, x1]
    n01 = lattice[y1, x0]
    n11 = lattice[y1, x1]
    return (n00 * (1 - tx) + n10 * tx) * (1 - ty) + (n01 * (1 - tx) + n11 * tx) * ty


def fbm(size, freq, octaves, rng):
    total = np.zeros((size, size))
    amp = 1.0
    norm = 0.0
    for o in range(octaves):
        total += tileable_value_noise(size, freq * (2 ** o), rng) * amp
        norm += amp
        amp *= 0.5
    return total / norm


def smoothstep(edge0, edge1, x):
    t = np.clip((x - edge0) / max(edge1 - edge0, 1e-6), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def main():
    rng = np.random.default_rng(SEED)

    # --- Patch mask: where lichen has taken hold at all.
    patch_field = fbm(SIZE, PATCH_FREQ, PATCH_OCTAVES, rng)
    thresh = 1.0 - COVERAGE
    patch = smoothstep(thresh, thresh + PATCH_EDGE, patch_field)

    # Distance-ish term for the pale margin: the rosette rim is where the field
    # only just clears the threshold, the mature centre is well above it.
    rim = smoothstep(thresh, thresh + PATCH_EDGE * 2.2, patch_field)

    # --- Areoles: ridged noise thresholded into a crack network. Ridged (the
    # |2x-1| fold) gives closed cells; plain noise gives blobs, not cracks.
    crack_field = fbm(SIZE, CRACK_FREQ, 2, rng)
    ridged = np.abs(crack_field * 2.0 - 1.0)
    cracks = 1.0 - smoothstep(0.0, CRACK_WIDTH, ridged)
    cracks *= patch  # cracks only exist inside the crust

    grain = fbm(SIZE, GRAIN_FREQ, 2, rng) - 0.5

    # --- Height -----------------------------------------------------------
    height = HEIGHT_BARE + (HEIGHT_LICHEN - HEIGHT_BARE) * patch
    height -= cracks * CRACK_DEPTH * (HEIGHT_LICHEN - HEIGHT_BARE)
    height += grain * GRAIN_AMOUNT * patch
    height = np.clip(height, 0.0, 1.0)

    # --- Color ------------------------------------------------------------
    lichen = COL_RIM[None, None, :] + (COL_MID - COL_RIM)[None, None, :] * rim[..., None]
    lichen = lichen * (1.0 + (grain * GRAIN_AMOUNT * 1.6)[..., None])
    lichen = lichen + (COL_CRACK - lichen) * (cracks * 0.85)[..., None]
    color = COL_BARE[None, None, :] + (lichen - COL_BARE[None, None, :]) * patch[..., None]
    color = np.clip(color, 0, 255).astype(np.uint8)

    # --- Normal from height, wrapped so the tile stays seamless ------------
    dx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * NORMAL_STRENGTH
    dy = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * NORMAL_STRENGTH
    nz = np.ones_like(height)
    length = np.sqrt(dx * dx + dy * dy + nz * nz)
    normal = np.stack([-dx / length, -dy / length, nz / length], axis=-1)
    normal = ((normal * 0.5 + 0.5) * 255.0).clip(0, 255).astype(np.uint8)

    os.makedirs(OUT_DIR, exist_ok=True)
    Image.fromarray(color, "RGB").save(os.path.join(OUT_DIR, "ClimbLichen_basecolor.png"))
    Image.fromarray(normal, "RGB").save(os.path.join(OUT_DIR, "ClimbLichen_normal.png"))
    Image.fromarray((height * 255).clip(0, 255).astype(np.uint8), "L").save(
        os.path.join(OUT_DIR, "ClimbLichen_height.png")
    )
    print(f"Wrote 3 x {SIZE}px maps to {os.path.normpath(OUT_DIR)}")
    print(f"  coverage={patch.mean():.3f} height_mean={height.mean():.3f} crack={cracks.mean():.3f}")


if __name__ == "__main__":
    main()
