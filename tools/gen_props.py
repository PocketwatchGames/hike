#!/usr/bin/env python3
"""Generate LitSprite prop .tscn files from decor atlas key files.

Reads pairs of (texture_path, key_file) and emits one .tscn per key whose
sprite is at least MIN_SIZE pixels in either dimension. Smaller entries are
expected to be wired in later as DetailEntries.
"""

from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
PROPS_DIR = REPO / "scenes" / "props"
PIXEL_SIZE = 0.0738
MIN_SIZE = 20

ATLASES = [
    ("res://assets/textures/decor_desert.png", "uid://bmexg1iyjueyk", REPO / "tools" / "decor_desert.txt"),
    ("res://assets/textures/decor_snow.png",   "uid://cojfqwmkajlpq", REPO / "tools" / "decor_snow.txt"),
]

TEMPLATE = """[gd_scene load_steps=5 format=3]

[ext_resource type="Script" uid="uid://c4uhw2jno8omi" path="res://scripts/gameplay/PropInstance.cs" id="1_prop"]
[ext_resource type="Texture2D" uid="{tex_uid}" path="{tex_path}" id="2_decor"]
[ext_resource type="Script" uid="uid://bb5jy6ebt0p15" path="res://scripts/gameplay/LitSprite.cs" id="3_litsprite"]
[ext_resource type="Material" path="res://resources/materials/sprite_lit.tres" id="4_spritemat"]

[sub_resource type="CylinderShape3D" id="CylinderShape3D_body"]
height = {height}
radius = {radius}

[node name="PropInstance" type="Node3D"]
script = ExtResource("1_prop")

[node name="Body" type="StaticBody3D" parent="."]

[node name="CollisionShape3D" type="CollisionShape3D" parent="Body"]
transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, {coll_y}, 0)
shape = SubResource("CylinderShape3D_body")

[node name="Sprite" type="Sprite3D" parent="."]
centered = false
offset = Vector2({offset_x}, 0)
pixel_size = {pixel_size}
texture = ExtResource("2_decor")
region_enabled = true
region_rect = Rect2({rx}, {ry}, {rw}, {rh})
script = ExtResource("3_litsprite")
Mirror = true
CenteredAtBase = false
ForwardOffset = {radius}
MaterialTemplate = ExtResource("4_spritemat")
alpha_cut = 1
"""


def fmt(value: float) -> str:
    """Trim trailing zeros but keep a decimal point so Godot reads as float."""
    text = f"{value:.6f}".rstrip("0").rstrip(".")
    return text if "." in text else text + ".0"


def gen_one(name: str, rect: tuple[int, int, int, int], tex_path: str, tex_uid: str) -> str:
    rx, ry, rw, rh = rect
    height = rh * PIXEL_SIZE
    radius = rw * PIXEL_SIZE / 2.0
    offset_x = -rw / 2.0
    return TEMPLATE.format(
        tex_path=tex_path,
        tex_uid=tex_uid,
        height=fmt(height),
        radius=fmt(radius),
        coll_y=fmt(height / 2.0),
        offset_x=fmt(offset_x),
        pixel_size=fmt(PIXEL_SIZE),
        rx=rx, ry=ry, rw=rw, rh=rh,
    )


def main() -> None:
    written = 0
    skipped = 0
    for tex_path, tex_uid, key_file in ATLASES:
        for line in key_file.read_text().splitlines():
            line = line.strip()
            if not line or "=" not in line:
                continue
            name, _, rhs = line.partition("=")
            name = name.strip()
            parts = rhs.split()
            if len(parts) != 4:
                continue
            rx, ry, rw, rh = (int(p) for p in parts)
            if rw < MIN_SIZE or rh < MIN_SIZE:
                skipped += 1
                continue
            out_path = PROPS_DIR / f"{name}.tscn"
            out_path.write_text(gen_one(name, (rx, ry, rw, rh), tex_path, tex_uid))
            written += 1
    print(f"wrote {written}, skipped {skipped} (under {MIN_SIZE}px)")


if __name__ == "__main__":
    main()
