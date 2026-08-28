"""Headless mirror of the in-editor "Rebuild Atlas" button on
resources/data/voxels/surfaces/voxel_atlas_manifest.tres (VoxelAtlasManifest.cs).

Stitches the PBR terrain source maps into two vertical Texture2DArray strips:

  voxel_tiles.png            - base color (sRGB)
  voxel_tiles_nrm_height.png - RGB = tangent-space normal, A = height/displacement

The layer list is NOT duplicated here — it is parsed from the manifest .tres so
the editor button and this CLI/CI path stay in lockstep. To change which source
texture a block uses, edit the manifest in the Godot inspector (or the .tres),
not this script. Each layer bakes into the row named by its own surface's
atlasBaseIndex, so the manifest's array order is meaningless. The
slices/vertical count in both .import files is rewritten automatically to match
the row count.

Reads only; never overwrites source art.
"""
import os
import re
from PIL import Image

SLOT = 256  # atlas slot size (px). Source art (1024/2048/4096) is downscaled to this.
ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), ".."))
MANIFEST = os.path.join(ROOT, "resources", "data", "surfaces", "voxel_atlas_manifest.tres")
VOXEL_DIR = os.path.join(ROOT, "assets", "textures", "terrain")

# Flat tangent-space normal (points straight out: 0.5,0.5,1.0 encoded) and zero
# height, used for slots with a null normal/height (e.g. the water placeholder).
FLAT_NORMAL_RGB = (128, 128, 255)


def _res_to_path(res):
    """res://foo/bar.png -> <ROOT>/foo/bar.png"""
    assert res.startswith("res://"), res
    return os.path.join(ROOT, *res[len("res://"):].split("/"))


def _atlas_base_index(surface_path):
    """Reads atlasBaseIndex out of a BlockSurfaceData .tres.

    Godot omits fields sitting at their default, and the default is Unassigned
    (-1), so an absent line means the surface has never been assigned a row.
    Only the editor's Rebuild Atlas mints those (this script reads only), so
    that is a hard error here rather than something to guess at.
    """
    with open(surface_path, encoding="utf-8") as fh:
        m = re.search(r"^atlasBaseIndex = (-?\d+)$", fh.read(), re.M)
    index = int(m.group(1)) if m else -1
    if index < 0:
        raise SystemExit(
            f"stitch_voxel_atlas: {os.path.relpath(surface_path, ROOT)} has no atlasBaseIndex; "
            "run Rebuild Atlas in the Godot editor once to assign it"
        )
    return index


def _parse_manifest(path):
    """Returns [{color, normal, height} or None] indexed by atlas row.

    A None entry is a row no surface claims; it bakes black + flat normal so the
    rows above keep their indices (atlasBaseIndex is a wire id, stored in the
    per-voxel OverlayId byte of every .hike). normal/height are None when the
    manifest leaves them unset.
    """
    with open(path, encoding="utf-8") as f:
        text = f.read()

    # ext_resource id -> res:// path
    ext = {}
    for m in re.finditer(r'\[ext_resource\b[^\]]*\bpath="([^"]+)"[^\]]*\bid="([^"]+)"\]', text):
        ext[m.group(2)] = m.group(1)

    # sub_resource id -> {surface/color/normal/height ext-id}
    subs = {}
    for m in re.finditer(r'\[sub_resource type="Resource" id="([^"]+)"\]\n(.*?)(?=\n\[|\Z)', text, re.S):
        body = m.group(2)
        fields = {}
        for fm in re.finditer(r'(\w+) = ExtResource\("([^"]+)"\)', body):
            fields[fm.group(1)] = fm.group(2)
        subs[m.group(1)] = fields

    # The layers array is an unordered set; typed (Array[T]([...])) or plain ([...]).
    lm = re.search(r'layers = Array\[[^\]]*\]\(\[(.*?)\]\)', text, re.S)
    if lm is None:
        lm = re.search(r'layers = \[(.*?)\]', text, re.S)
    if lm is None:
        raise SystemExit("stitch_voxel_atlas: no layers array found in manifest")

    by_index = {}
    for sid in re.findall(r'SubResource\("([^"]+)"\)', lm.group(1)):
        fields = subs[sid]

        def resolve(key):
            ext_id = fields.get(key)
            return _res_to_path(ext[ext_id]) if ext_id else None

        surface = resolve("surface")
        if surface is None:
            raise SystemExit(
                f"stitch_voxel_atlas: layer {sid} has no Surface, so there is no row to bake it into"
            )
        index = _atlas_base_index(surface)
        if index in by_index:
            raise SystemExit(
                f"stitch_voxel_atlas: two layers both claim atlasBaseIndex={index}"
            )
        by_index[index] = {
            "color": resolve("color"),
            "normal": resolve("normal"),
            "height": resolve("height"),
        }

    rows = [by_index.get(i) for i in range(max(by_index) + 1)]
    for i, row in enumerate(rows):
        if row is None:
            print(f"  warning: no surface claims layer {i}; baking it black")
    return rows


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
            lines[i] = f"slices/vertical={layer_count}\n"
            found = True
    if not found:
        print(f"  warning: {import_path} has no slices/vertical line")
        return
    with open(import_path, "w", encoding="utf-8", newline="") as fh:
        fh.writelines(lines)


def main():
    rows = _parse_manifest(MANIFEST)
    color_strip = Image.new("RGB", (SLOT, SLOT * len(rows)))
    nh_strip = Image.new("RGBA", (SLOT, SLOT * len(rows)))

    for i, layer in enumerate(rows):
        # A row with no art — unclaimed, or claimed by a surface that authors
        # none (Water). Keeps the strip's black fill; only the normal needs
        # writing. Mirrors VoxelAtlasManifest.FillBlankRow on the C# side.
        if layer is None or layer["color"] is None:
            nh_strip.paste(Image.new("RGBA", (SLOT, SLOT), FLAT_NORMAL_RGB + (0,)), (0, i * SLOT))
            continue
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
    sync_import_slice_count(color_out, len(rows))
    sync_import_slice_count(nh_out, len(rows))
    print(f"Wrote {len(rows)} layers to {color_out}")
    print(f"Wrote {len(rows)} layers to {nh_out}")


if __name__ == "__main__":
    main()
