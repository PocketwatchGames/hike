"""Stitches the per-tile source PNGs in assets/textures/voxels/ into the
vertical Texture2DArray strip voxel_tiles.png. Reads only; never overwrites
source PNGs. Order and count must match VoxelType.cs layer indices and
voxel_tiles.png.import slices/vertical.
"""
import os
from PIL import Image

SLOT = 128  # atlas slot size (px). Smaller source PNGs are nearest-neighbour upscaled.
SRC_DIR = os.path.join(os.path.dirname(__file__), "..", "assets", "textures", "voxels")

# Layer order — matches VoxelType.cs TILE_* base indices and their variant blocks.
LAYERS = [
    "stone",                   #  0 TILE_STONE
    "level1_1",                #  1 TILE_GRASS_TOP band 0 variant 0
    "level1_2",                #  2
    "level1_3",                #  3
    "level1_4",                #  4
    "level2_1",                #  5 band 1
    "level2_2",                #  6
    "level2_3",                #  7
    "level2_4",                #  8
    "level3_1",                #  9 band 2
    "level3_2",                # 10
    "level3_3",                # 11
    "level3_4",                # 12
    "level4_1",                # 13 band 3
    "level4_2",                # 14
    "level4_3",                # 15
    "level4_4",                # 16
    "water",                   # 17 TILE_WATER
    "cobblestone1",            # 18 TILE_COBBLESTONE variants
    "cobblestone2",            # 19
    "cobblestone3",            # 20
    "cobblestone4",            # 21
    "dirt1",                   # 22 TILE_DIRT_OVERLAY variants
    "dirt2",                   # 23
    "dirt3",                   # 24
    "dirt4",                   # 25
    "field",                   # 26 TILE_FIELD_OVERLAY
    "desert_elevation0grass",  # 27 TILE_DESERT_TOP band 0 (sea-level grass-tinted dune)
    "desert_level1_1",         # 28 TILE_DESERT_TOP band 1 / TILE_DESERT_SAND alias (shoreline)
    "desert_level4_1",         # 29 TILE_DESERT_WALL (cliff face)
    "desert_level2_1",         # 30 TILE_DESERT_CAVE (sandstone cave floor)
    "marsh",                   # 31 TILE_MARSH
]

strip = Image.new("RGBA", (SLOT, SLOT * len(LAYERS)))
for i, name in enumerate(LAYERS):
    path = os.path.join(SRC_DIR, name + ".png")
    img = Image.open(path).convert("RGBA")
    if img.size != (SLOT, SLOT):
        img = img.resize((SLOT, SLOT), Image.NEAREST)
    strip.paste(img, (0, i * SLOT))

out = os.path.join(SRC_DIR, "voxel_tiles.png")
strip.save(out)
print(f"Wrote {len(LAYERS)} layers to {out}")
