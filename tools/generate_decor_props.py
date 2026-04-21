#!/usr/bin/env python3
"""Generates PropInstance scene files and DetailEntry entries from decor atlas.

For each prop the authored rect is trimmed to the tight bounding box of
non-transparent pixels so the Sprite3D region matches what's actually drawn.
Collision footprint comes from the bottom 30 rows of the trimmed sprite:
radius = (max_x - min_x) / 2, and the sprite's horizontal anchor is moved
to the center of that same span so the cylinder sits directly under the
prop's base regardless of where the art sits within the original rect.
"""
import os
import sys
from PIL import Image

DECOR_ENTRIES = """barrier_a1 = 295 384 38 38
barrier_a2 = 491 210 38 38
barrier_a3 = 334 384 38 38
barrier_b1 = 646 81 38 38
barrier_b2 = 378 352 38 38
barrier_bcorner = 256 384 38 43
campfire1x1 = 565 339 32 32
campfire2x2 = 0 379 48 48
choppedwood1 = 203 0 25 25
choppedwood2 = 49 413 25 25
tent1 = 162 147 72 64
tent2 = 242 121 72 64
barrel1 = 315 162 20 20
barrel2 = 75 413 20 20
mapcraterifle = 573 129 30 30
supplies1x1_1 = 632 273 32 32
supplies1x1_2 = 602 306 32 32
supplies1x1_3 = 678 231 26 26
supplies2x2_1 = 110 396 42 40
supplies2x2_2 = 315 121 47 40
supplytent_empty = 363 0 70 70
supplytent1 = 363 71 64 68
supplytent2 = 434 0 64 68
supplytent3 = 304 186 64 68
bonepile1x1_1 = 528 366 36 30
bonepile1x1_2 = 484 405 36 30
bonepile2x2_1 = 235 186 68 48
loosebones1 = 678 258 26 26
loosebones2 = 635 306 26 26
loosebones3 = 665 285 26 26
loosebones4 = 628 339 26 26
loosebones5 = 561 405 26 26
singlebone1 = 336 162 17 17
singlebone2 = 272 235 17 17
singlebone3 = 685 98 17 17
singlebone4 = 684 200 17 17
singlebone5 = 662 312 17 17
deadpine1 = 687 0 18 48
deadpine2 = 687 49 18 48
deadpine3 = 684 151 18 48
deadhouse2x2_1 = 0 147 80 80
deadhouse2x2_2 = 81 147 80 80
deadhouse3x3_1 = 0 26 120 120
deadhouse3x3_2 = 121 26 120 120
deadhouse3x3_3 = 242 0 120 120
deadplank1 = 598 382 17 17
deadplank2 = 662 330 17 17
deadplank3 = 680 312 17 17
deadplank4 = 588 405 17 17
flower_elev0_1 = 229 0 12 12
flower_elev0_2 = 229 13 12 12
flower_elev0_3 = 96 413 12 12
flower_elev0_4 = 354 162 12 12
flower_elev0_5 = 96 426 12 12
flower_elev0_6 = 290 235 12 12
flower_elev1_1 = 256 364 12 12
flower_elev1_2 = 240 409 12 12
flower_elev1_3 = 240 422 12 12
flower_elev1_4 = 295 423 12 12
flower_elev1_5 = 308 423 12 12
flower_elev1_6 = 321 423 12 12
grass0_01 = 334 423 12 12
grass0_02 = 347 423 12 12
grass0_03 = 360 423 12 12
grass0_04 = 447 426 12 12
grass0_05 = 460 426 12 12
grass0_06 = 684 218 12 12
grass0b_01 = 692 285 12 12
grass0b_02 = 692 298 12 12
grass0b_03 = 588 423 12 12
grass0b_04 = 601 423 12 12
grass0b_05 = 623 404 12 12
grass0b_06 = 662 367 12 12
grass1_01 = 672 349 12 12
grass1_02 = 640 386 12 12
grass1_03 = 685 349 12 12
grass1_04 = 614 419 12 12
grass1_05 = 640 399 12 12
grass1_06 = 653 386 12 12
grass1b_01 = 675 362 12 12
grass1b_02 = 627 417 12 12
grass1b_03 = 675 375 12 12
grass1b_04 = 688 362 12 12
grass1b_05 = 653 399 12 12
grass1b_06 = 640 412 12 12
marsh_grass1 = 628 366 16 18
marsh_grass2 = 680 330 16 18
marsh_grass3 = 655 348 16 18
marsh_grass4 = 606 400 16 18
marsh_grass5 = 623 385 16 18
marsh_grass6 = 645 367 16 18
marsh_treea1 = 428 71 60 75
marsh_treea2 = 369 147 60 75
marsh_treea3 = 430 147 60 75
marsh_treeb1 = 605 0 40 80
marsh_treeb2 = 491 129 40 80
marsh_treeb3 = 646 0 40 80
marsh_treeb4 = 532 129 40 80
marsh_treeb5 = 605 81 40 80
rock1 = 646 120 36 36
rock2 = 573 162 36 36
rock3 = 373 391 36 36
shrub_01 = 166 341 44 44
shrub_02 = 158 386 44 44
shrub_03 = 211 364 44 44
stump1 = 203 409 36 28
stump2 = 565 310 36 28
treebirch_01 = 57 284 52 128
treebirch_02 = 113 228 52 128
treebirch_03 = 166 212 52 128
treemaple_01 = 219 235 52 128
treemaple_02 = 272 255 52 128
treemaple_03 = 325 255 52 128
treepine_01 = 378 223 52 128
treepine_02 = 431 223 52 128
treepine_03 = 499 0 52 128
treepine_04 = 552 0 52 128
underbrush_01 = 417 352 36 36
underbrush_02 = 573 199 36 36
underbrush_03 = 610 162 36 36
underbrush_04 = 484 298 36 36
fence_east1 = 521 259 36 36
fence_east2 = 410 391 36 36
fence_east3 = 454 352 36 36
fence_north1 = 647 157 36 36
fence_north2 = 610 199 36 36
fence_north3 = 567 236 36 36
fence_south1 = 521 296 36 36
fence_south2 = 447 389 36 36
fence_south3 = 558 273 36 36
fence_west1 = 604 236 36 36
fence_west2 = 647 194 36 36
fence_west3 = 491 335 36 36
butcherknife1 = 0 428 10 7
butcherknife2 = 11 428 10 7
flag = 0 0 175 25
floorhatch = 176 0 26 19
grate = 219 212 14 12
shed1 = 528 397 32 32
shed2 = 598 339 29 42
stovea1 = 484 249 36 48
stovea2 = 530 210 36 48
t1warrens = 110 357 47 38
t2warrens = 0 285 56 38
t3warrens = 0 228 56 56
table_a1 = 491 372 36 32
table_a2 = 528 333 36 32
table_b1 = 595 273 36 32
table_b2 = 641 236 36 32
tunnel1 = 0 324 54 54
tunnel2 = 57 228 55 55
waterpump = 565 372 32 32
well = 683 120 22 30"""

PIXEL_SIZE = 0.0738
DECOR_UID = "uid://c4v4ybksgte7p"
REPO = r"c:\Users\andy\source\hike"
PROPS_DIR = os.path.join(REPO, "scenes", "game", "props")
DETAIL_TRES = os.path.join(REPO, "resources", "data", "detail_grass.tres")
DECOR_PNG = os.path.join(REPO, "assets", "textures", "decor.png")
BASE_ROWS = 25
ALPHA_THRESHOLD = 1

PROP_TEMPLATE = """[gd_scene load_steps=5 format=3]

[ext_resource type="Script" uid="uid://c4uhw2jno8omi" path="res://scripts/gameplay/PropInstance.cs" id="1_prop"]
[ext_resource type="Texture2D" uid="{decor_uid}" path="res://assets/textures/decor.png" id="2_decor"]
[ext_resource type="Script" uid="uid://bb5jy6ebt0p15" path="res://scripts/gameplay/LitSprite.cs" id="3_litsprite"]
[ext_resource type="Material" path="res://resources/materials/sprite_lit.tres" id="4_spritemat"]

[sub_resource type="CylinderShape3D" id="CylinderShape3D_body"]
height = {cyl_height}
radius = {cyl_radius}

[node name="PropInstance" type="Node3D"]
script = ExtResource("1_prop")

[node name="Body" type="StaticBody3D" parent="."]

[node name="CollisionShape3D" type="CollisionShape3D" parent="Body"]
transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, {cyl_y}, 0)
shape = SubResource("CylinderShape3D_body")

[node name="Sprite" type="Sprite3D" parent="."]
centered = false
offset = Vector2({sprite_offset_x}, 0)
pixel_size = {pixel_size}
texture = ExtResource("2_decor")
region_enabled = true
region_rect = Rect2({rx}, {ry}, {rw}, {rh})
script = ExtResource("3_litsprite")
Mirror = true
CenteredAtBase = false
ForwardOffset = {forward_offset}
MaterialTemplate = ExtResource("4_spritemat")
"""


def fnum(v):
    """Format a float for Godot .tscn output (strip trailing zeros like Godot)."""
    if v == int(v):
        return f"{int(v)}.0"
    s = f"{v:.6f}".rstrip("0").rstrip(".")
    return s if "." in s else s + ".0"


def parse_entries():
    out = []
    for line in DECOR_ENTRIES.strip().splitlines():
        name, rhs = line.split(" = ")
        x, y, w, h = [int(v) for v in rhs.split()]
        out.append((name.strip(), x, y, w, h))
    return out


def trim_rect(img, x, y, w, h):
    """Returns the tight (x, y, w, h) of opaque pixels within the authored rect,
    or None if the region is fully transparent."""
    px = img.load()
    xmin, ymin = w, h
    xmax, ymax = -1, -1
    for iy in range(h):
        for ix in range(w):
            a = px[x + ix, y + iy][3]
            if a >= ALPHA_THRESHOLD:
                if ix < xmin: xmin = ix
                if iy < ymin: ymin = iy
                if ix > xmax: xmax = ix
                if iy > ymax: ymax = iy
    if xmax < 0:
        return None
    return (x + xmin, y + ymin, xmax - xmin + 1, ymax - ymin + 1)


def base_span(img, x, y, w, h, rows=BASE_ROWS):
    """min_x, max_x (relative to the trimmed rect's left edge) of opaque
    pixels in the bottom `rows` rows. Falls back to the full width if no
    opaque pixels are found (shouldn't happen for trimmed rects)."""
    px = img.load()
    start_y = max(0, h - rows)
    xmin, xmax = w, -1
    for iy in range(start_y, h):
        for ix in range(w):
            a = px[x + ix, y + iy][3]
            if a >= ALPHA_THRESHOLD:
                if ix < xmin: xmin = ix
                if ix > xmax: xmax = ix
    if xmax < 0:
        return 0, w - 1
    return xmin, xmax


def write_prop(img, name, ax, ay, aw, ah):
    trimmed = trim_rect(img, ax, ay, aw, ah)
    if trimmed is None:
        print(f"  skip {name} (fully transparent)")
        return
    tx, ty, tw, th = trimmed
    bx_min, bx_max = base_span(img, tx, ty, tw, th)
    base_w_px = bx_max - bx_min + 1
    base_cx_px = (bx_min + bx_max + 1) / 2.0  # +1 because we want pixel-center
    cyl_h = th * PIXEL_SIZE
    cyl_r = (base_w_px * PIXEL_SIZE) / 2.0
    cyl_y = cyl_h / 2.0
    content = PROP_TEMPLATE.format(
        decor_uid=DECOR_UID,
        cyl_height=fnum(cyl_h),
        cyl_radius=fnum(cyl_r),
        cyl_y=fnum(cyl_y),
        sprite_offset_x=fnum(-base_cx_px),
        pixel_size=fnum(PIXEL_SIZE),
        rx=tx, ry=ty, rw=tw, rh=th,
        forward_offset=fnum(cyl_r),
    )
    path = os.path.join(PROPS_DIR, f"{name}.tscn")
    with open(path, "w", newline="\n") as f:
        f.write(content)


def build_detail_block(entries):
    """Build atlas sub-resources and DetailEntry sub-resources for short entries.
    Returns (ext_resources_block, sub_resources_block, entry_ids_list)."""
    atlas_subs = []
    entry_subs = []
    entry_ids = []
    for (name, x, y, w, h) in entries:
        atlas_id = f"Atlas_{name}"
        entry_id = f"Entry_{name}"
        atlas_subs.append(
            f'[sub_resource type="AtlasTexture" id="{atlas_id}"]\n'
            f'atlas = ExtResource("decor_tex")\n'
            f'region = Rect2({x}, {y}, {w}, {h})\n'
        )
        entry_subs.append(
            f'[sub_resource type="Resource" id="{entry_id}"]\n'
            f'script = ExtResource("entry_script")\n'
            f'Texture = SubResource("{atlas_id}")\n'
            f'Weight = 0.1\n'
            f'ScaleMin = 0.7\n'
            f'ScaleMax = 1.3\n'
            f'metadata/_custom_type_script = "uid://c8h7yqpjyorr3"\n'
        )
        entry_ids.append(entry_id)
    return "\n".join(atlas_subs), "\n".join(entry_subs), entry_ids


def write_detail_tres(detail_entries):
    atlas_block, entry_block, entry_ids = build_detail_block(detail_entries)
    existing_entries = [
        "Resource_grass_entry",
        "Resource_a6s4p",
        "Resource_0mhvy",
        "Resource_hrmu7",
    ]
    all_ids = existing_entries + entry_ids
    entries_array = ", ".join(f'SubResource("{i}")' for i in all_ids)

    header = (
        '[gd_resource type="Resource" script_class="DetailGroupData" format=3 uid="uid://hv046le0b30u"]\n\n'
        '[ext_resource type="Texture2D" uid="uid://cc6as7l6ipqvo" path="res://assets/textures/grass_01.png" id="2_mxn6u"]\n'
        '[ext_resource type="Texture2D" uid="uid://bx5qkc3he2is3" path="res://assets/textures/grass_02.png" id="3_05b5i"]\n'
        '[ext_resource type="Script" uid="uid://c8h7yqpjyorr3" path="res://scripts/voxels/detail/DetailEntry.cs" id="entry_script"]\n'
        '[ext_resource type="Texture2D" uid="uid://dn5dksty0sags" path="res://assets/textures/grass_03.png" id="4_a6s4p"]\n'
        '[ext_resource type="Script" uid="uid://bxmmuij45e6re" path="res://scripts/voxels/detail/DetailGroupData.cs" id="4_group_script"]\n'
        '[ext_resource type="Texture2D" uid="uid://bm0nc52ekfjhy" path="res://assets/textures/grass_04.png" id="5_8sc02"]\n'
        f'[ext_resource type="Texture2D" uid="{DECOR_UID}" path="res://assets/textures/decor.png" id="decor_tex"]\n'
    )

    existing_sub = (
        '\n[sub_resource type="Resource" id="Resource_grass_entry"]\n'
        'script = ExtResource("entry_script")\n'
        'Texture = ExtResource("2_mxn6u")\n'
        'ScaleMin = 0.7\n'
        'ScaleMax = 1.3\n'
        '\n[sub_resource type="Resource" id="Resource_a6s4p"]\n'
        'script = ExtResource("entry_script")\n'
        'Texture = ExtResource("3_05b5i")\n'
        'ScaleMin = 0.7\n'
        'ScaleMax = 1.3\n'
        'metadata/_custom_type_script = "uid://c8h7yqpjyorr3"\n'
        '\n[sub_resource type="Resource" id="Resource_0mhvy"]\n'
        'script = ExtResource("entry_script")\n'
        'Texture = ExtResource("4_a6s4p")\n'
        'Weight = 0.1\n'
        'ScaleMin = 0.7\n'
        'ScaleMax = 1.3\n'
        'metadata/_custom_type_script = "uid://c8h7yqpjyorr3"\n'
        '\n[sub_resource type="Resource" id="Resource_hrmu7"]\n'
        'script = ExtResource("entry_script")\n'
        'Texture = ExtResource("5_8sc02")\n'
        'Weight = 0.1\n'
        'ScaleMin = 0.7\n'
        'ScaleMax = 1.3\n'
        'metadata/_custom_type_script = "uid://c8h7yqpjyorr3"\n'
    )

    footer = (
        '\n[resource]\n'
        'script = ExtResource("4_group_script")\n'
        'GroupName = "grass"\n'
        f'Entries = Array[ExtResource("entry_script")]([{entries_array}])\n'
    )

    content = header + existing_sub + "\n" + atlas_block + "\n" + entry_block + footer
    with open(DETAIL_TRES, "w", newline="\n") as f:
        f.write(content)


def main():
    os.makedirs(PROPS_DIR, exist_ok=True)
    img = Image.open(DECOR_PNG).convert("RGBA")
    entries = parse_entries()
    props = [e for e in entries if e[4] >= 19]
    for e in props:
        write_prop(img, *e)
    print(f"Wrote {len(props)} prop scenes to {PROPS_DIR}")
    print("(detail_grass.tres left untouched; pass --regen-details to rebuild)")

    if "--regen-details" in sys.argv:
        details = [e for e in entries if e[4] < 19]
        write_detail_tres(details)
        print(f"Added {len(details)} detail entries to {DETAIL_TRES}")


if __name__ == "__main__":
    main()
