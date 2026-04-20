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
    "stone",         #  0 TILE_STONE
    "dirt",          #  1 TILE_DIRT
    "level1_1",      #  2 TILE_GRASS_TOP band 0 variant 0
    "level1_2",      #  3
    "level1_3",      #  4
    "level1_4",      #  5
    "level2_1",      #  6 band 1
    "level2_2",      #  7
    "level2_3",      #  8
    "level2_4",      #  9
    "level3_1",      # 10 band 2
    "level3_2",      # 11
    "level3_3",      # 12
    "level3_4",      # 13
    "level4_1",      # 14 band 3
    "level4_2",      # 15
    "level4_3",      # 16
    "level4_4",      # 17
    "grass_side",    # 18 TILE_GRASS_SIDE
    "sand",          # 19 TILE_SAND
    "wood_end",      # 20 TILE_WOOD_END
    "wood_side",     # 21 TILE_WOOD_SIDE
    "water",         # 22 TILE_WATER
    "cobblestone1",  # 23 TILE_COBBLESTONE variants
    "cobblestone2",  # 24
    "cobblestone3",  # 25
    "cobblestone4",  # 26
    "dirt1",         # 27 TILE_DIRT_OVERLAY variants
    "dirt2",         # 28
    "dirt3",         # 29
    "dirt4",         # 30
    "field",         # 31 TILE_FIELD_OVERLAY
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
