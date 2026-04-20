"""Read-only: infers the layer order of the current voxel_tiles.png by
matching each 128px row against every candidate per-tile source PNG.
Prints the best-match source name per row. Does not write anything.
"""
import os
from PIL import Image

SLOT = 128
SRC_DIR = os.path.join(os.path.dirname(__file__), "..", "assets", "textures", "voxels")

candidates = [
    "stone", "dirt",
    "level1_1", "level1_2", "level1_3", "level1_4",
    "level2_1", "level2_2", "level2_3", "level2_4",
    "level3_1", "level3_2", "level3_3", "level3_4",
    "level4_1", "level4_2", "level4_3", "level4_4",
    "grass_side", "sand", "wood_end", "wood_side", "water",
    "cobblestone1", "cobblestone2", "cobblestone3", "cobblestone4",
    "dirt1", "dirt2", "dirt3", "dirt4",
    "field",
]


def load_128(name):
    img = Image.open(os.path.join(SRC_DIR, name + ".png")).convert("RGBA")
    if img.size != (SLOT, SLOT):
        img = img.resize((SLOT, SLOT), Image.NEAREST)
    return img


def sad(a, b):
    pa, pb = a.tobytes(), b.tobytes()
    return sum(abs(x - y) for x, y in zip(pa, pb))


cands = {n: load_128(n) for n in candidates}

atlas = Image.open(os.path.join(SRC_DIR, "voxel_tiles.png")).convert("RGBA")
assert atlas.size == (SLOT, SLOT * 32), atlas.size

for row in range(32):
    tile = atlas.crop((0, row * SLOT, SLOT, (row + 1) * SLOT))
    scored = sorted(((sad(tile, cands[n]), n) for n in candidates))
    best_score, best_name = scored[0]
    second_score, second_name = scored[1]
    exact = "EXACT" if best_score == 0 else f"diff={best_score}"
    print(f"row {row:2d}: {best_name:14s} {exact:15s} (next: {second_name} diff={second_score})")
