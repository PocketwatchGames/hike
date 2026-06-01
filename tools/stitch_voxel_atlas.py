"""Stitches the PBR terrain material sets in assets/textures/terrain/ into two
vertical Texture2DArray strips:

  voxel_tiles.png            - base color (sRGB)
  voxel_tiles_nrm_height.png - RGB = tangent-space normal, A = height/displacement

Reads only; never overwrites source art. Layer order and count must match the
AtlasBaseIndex authored on each BlockData (resources/data/blocks/) and the
slices/vertical count in both .import files. See scripts/voxels/VoxelType.cs and
scripts/data/BlockData.cs.
"""
import os
from PIL import Image

SLOT = 256  # atlas slot size (px). Source art (1024/2048) is downscaled to this.
ROOT = os.path.join(os.path.dirname(__file__), "..")
TERRAIN_DIR = os.path.join(ROOT, "assets", "textures", "terrain")
VOXEL_DIR = os.path.join(ROOT, "assets", "textures", "voxels")

# One entry per atlas layer, in AtlasBaseIndex order. Filenames are inconsistent
# across the source sets (COLOR/basecolor/BaseColor, NORM/normal/Normal/NRM,
# DISP/height/Height), so each map is spelled out explicitly. `color` paths are
# relative to TERRAIN_DIR unless absolute. Water (layer 2) is a placeholder — it
# is rendered by the separate water shader, not the tile array.
def _terrain(folder, color, normal, height):
    base = os.path.join(TERRAIN_DIR, folder)
    return {
        "color": os.path.join(base, color),
        "normal": os.path.join(base, normal),
        "height": os.path.join(base, height),
    }


LAYERS = [
    # 0 Stone -> stylized rocks (general cliff faces)
    _terrain("Stylized_Rocks_002_4K",
             "Stylized_Rocks_002_basecolor.png",
             "Stylized_Rocks_002_normal.png",
             "Stylized_Rocks_002_height.png"),
    # 1 GrassTop -> forest/mountain ground
    _terrain("Stylized_Grass_002_SD",
             "Stylized_Grass_002_basecolor.jpg",
             "Stylized_Grass_002_normal.jpg",
             "Stylized_Grass_002_height.png"),
    # 2 Water (placeholder; rendered by water shader)
    {"color": os.path.join(VOXEL_DIR, "water.png"), "normal": None, "height": None},
    # 3 Cobblestone
    _terrain("Cobblestone_Irregular_Floor_001",
             "Cobblestone_Irregular_Floor_001_basecolor.png",
             "Cobblestone_Irregular_Floor_001_normal.png",
             "Cobblestone_Irregular_Floor_001_height.png"),
    # 4 DirtOverlay -> dry mud
    _terrain("Stylized_Dry_Mud_001_SD",
             "Stylized_Dry_Mud_001_basecolor.jpg",
             "Stylized_Dry_Mud_001_normal.jpg",
             "Stylized_Dry_Mud_001_height.png"),
    # 5 FieldOverlay -> same stylized grass as the base ground (matches mountain)
    _terrain("Stylized_Grass_002_SD",
             "Stylized_Grass_002_basecolor.jpg",
             "Stylized_Grass_002_normal.jpg",
             "Stylized_Grass_002_height.png"),
    # 6 DesertTop -> stylized sand dune
    _terrain("Stylized_Sand_002_SD",
             "Stylized_Sand_002_basecolor.png",
             "Stylized_Sand_002_normal.png",
             "Stylized_Sand_002_height.png"),
    # 7 DesertSand -> realistic sand
    _terrain("Sand 001",
             "Sand_001_COLOR.png",
             "Sand_001_NRM.png",
             "Sand_001_DISP.png"),
    # 8 DesertWall -> stylized cliff rock (desert cliffs)
    _terrain("Stylized_Cliff_Rock_006_SD",
             "Stylized_Cliff_Rock_006_basecolor.png",
             "Stylized_Cliff_Rock_006_normal.png",
             "Stylized_Cliff_Rock_006_height.png"),
    # 9 DesertCave -> dry mud (sandstone cave floor)
    _terrain("Stylized_Dry_Mud_001_SD",
             "Stylized_Dry_Mud_001_basecolor.jpg",
             "Stylized_Dry_Mud_001_normal.jpg",
             "Stylized_Dry_Mud_001_height.png"),
    # 10 Marsh -> wet ground (swamp)
    _terrain("Ground_Wet_002_SD",
             "Ground_Wet_002_basecolor.jpg",
             "Ground_Wet_002_normal.jpg",
             "Ground_Wet_002_height.png"),
    # 11 CaveFloor -> stylized stone floor (limestone cave floor)
    _terrain("Stylized_Stone_Floor_002_4K",
             "Stylized_Stone_Floor_002_basecolor.png",
             "Stylized_Stone_Floor_002_normal.png",
             "Stylized_Stone_Floor_002_height.png"),
]

# Flat tangent-space normal (points straight out: 0.5,0.5,1.0 encoded) and zero
# height, used for the water placeholder slot.
FLAT_NORMAL_RGB = (128, 128, 255)


def _load_slot(path, mode):
    img = Image.open(path).convert(mode)
    if img.size != (SLOT, SLOT):
        img = img.resize((SLOT, SLOT), Image.LANCZOS)
    return img


def main():
    color_strip = Image.new("RGB", (SLOT, SLOT * len(LAYERS)))
    nh_strip = Image.new("RGBA", (SLOT, SLOT * len(LAYERS)))

    for i, layer in enumerate(LAYERS):
        # Base color.
        color = _load_slot(layer["color"], "RGB")
        color_strip.paste(color, (0, i * SLOT))

        # Packed normal (RGB) + height (A).
        if layer["normal"]:
            normal = _load_slot(layer["normal"], "RGB")
        else:
            normal = Image.new("RGB", (SLOT, SLOT), FLAT_NORMAL_RGB)
        if layer["height"]:
            height = _load_slot(layer["height"], "L")
        else:
            height = Image.new("L", (SLOT, SLOT), 0)
        nh = normal.convert("RGBA")
        nh.putalpha(height)
        nh_strip.paste(nh, (0, i * SLOT))

    color_out = os.path.join(VOXEL_DIR, "voxel_tiles.png")
    nh_out = os.path.join(VOXEL_DIR, "voxel_tiles_nrm_height.png")
    color_strip.save(color_out)
    nh_strip.save(nh_out)
    print(f"Wrote {len(LAYERS)} layers to {color_out}")
    print(f"Wrote {len(LAYERS)} layers to {nh_out}")


if __name__ == "__main__":
    main()
