"""Headless mirror of the in-editor "Rebuild Atlas" button on
resources/data/surfaces/voxel_atlas_manifest.tres (VoxelAtlasManifest.cs).

Stitches the PBR terrain source maps into two vertical Texture2DArray strips:

  voxel_tiles.png            - base color (sRGB)
  voxel_tiles_nrm_height.png - RGB = tangent-space normal, A = height/displacement

The layer list is NOT duplicated here — it is parsed from the manifest .tres so
the editor button and this CLI/CI path stay in lockstep. To change which source
texture a block uses, edit the manifest in the Godot inspector (or the .tres),
not this script. Layer order in the manifest must match the atlasBaseIndex on
each BlockSurfaceData. The slices/vertical count in both .import files is
rewritten automatically to match the layer count.

Reads only; never overwrites source art.
"""
import os
import re
from PIL import Image

SLOT = 256  # atlas slot size (px). Source art (1024/2048/4096) is downscaled to this.
ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), ".."))
MANIFEST = os.path.join(ROOT, "resources", "data", "blocks", "voxel_atlas_manifest.tres")
VOXEL_DIR = os.path.join(ROOT, "assets", "textures", "terrain")

# Flat tangent-space normal (points straight out: 0.5,0.5,1.0 encoded) and zero
# height, used for slots with a null normal/height (e.g. the water placeholder).
FLAT_NORMAL_RGB = (128, 128, 255)


def _res_to_path(res):
    """res://foo/bar.png -> <ROOT>/foo/bar.png"""
    assert res.startswith("res://"), res
    return os.path.join(ROOT, *res[len("res://"):].split("/"))


def _parse_manifest(path):
    """Returns [{color, normal, height}] in layer order, paths absolute on disk.
    normal/height are None when the manifest leaves them unset."""
    with open(path, encoding="utf-8") as f:
        text = f.read()

    # ext_resource id -> res:// path
    ext = {}
    for m in re.finditer(r'\[ext_resource\b[^\]]*\bpath="([^"]+)"[^\]]*\bid="([^"]+)"\]', text):
        ext[m.group(2)] = m.group(1)

    # sub_resource id -> {color/normal/height ext-id}
    subs = {}
    for m in re.finditer(r'\[sub_resource type="Resource" id="([^"]+)"\]\n(.*?)(?=\n\[|\Z)', text, re.S):
        body = m.group(2)
        fields = {}
        for fm in re.finditer(r'(\w+) = ExtResource\("([^"]+)"\)', body):
            fields[fm.group(1)] = fm.group(2)
        subs[m.group(1)] = fields

    # layers array order (list of SubResource ids); typed (Array[T]([...])) or plain ([...])
    lm = re.search(r'layers = Array\[[^\]]*\]\(\[(.*?)\]\)', text, re.S)
    if lm is None:
        lm = re.search(r'layers = \[(.*?)\]', text, re.S)
    if lm is None:
        raise SystemExit("stitch_voxel_atlas: no layers array found in manifest")
    order = re.findall(r'SubResource\("([^"]+)"\)', lm.group(1))

    layers = []
    for sid in order:
        fields = subs[sid]

        def resolve(key):
            ext_id = fields.get(key)
            return _res_to_path(ext[ext_id]) if ext_id else None

        layers.append({
            "color": resolve("color"),
            "normal": resolve("normal"),
            "height": resolve("height"),
        })
    return layers


def _load_slot(path, mode):
    img = Image.open(path).convert(mode)
    if img.size != (SLOT, SLOT):
        img = img.resize((SLOT, SLOT), Image.LANCZOS)
    return img


def sync_import_slice_count(texture_path, layer_count):
    """Point the texture's .import at the layer count just baked.

    The importer slices the strip by this number, so a stale value silently
    mis-slices every tile (a 16-layer strip read as 12 gives 341px slabs).
    Mirrors VoxelAtlasManifest.SyncImportSliceCount on the C# side.
    """
    import_path = texture_path + ".import"
    if not os.path.exists(import_path):
        print(f"  warning: no .import beside {texture_path}; slices/vertical not updated")
        return
    with open(import_path, encoding="utf-8") as fh:
        lines = fh.readlines()
    found = False
    for i, line in enumerate(lines):
        if line.startswith("slices/vertical="):
            lines[i] = f"slices/vertical={layer_count}
"
            found = True
    if not found:
        print(f"  warning: {import_path} has no slices/vertical line")
        return
    with open(import_path, "w", encoding="utf-8", newline="") as fh:
        fh.writelines(lines)


def main():
    layers = _parse_manifest(MANIFEST)
    color_strip = Image.new("RGB", (SLOT, SLOT * len(layers)))
    nh_strip = Image.new("RGBA", (SLOT, SLOT * len(layers)))

    for i, layer in enumerate(layers):
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
    sync_import_slice_count(color_out, len(layers))
    sync_import_slice_count(nh_out, len(layers))
    print(f"Wrote {len(layers)} layers to {color_out}")
    print(f"Wrote {len(layers)} layers to {nh_out}")


if __name__ == "__main__":
    main()
