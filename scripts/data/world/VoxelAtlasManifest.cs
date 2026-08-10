using Godot;

// Single editor-visible source of truth for the voxel terrain atlas mapping:
// which source PBR texture set is baked into each layer of voxel_tiles.png /
// voxel_tiles_nrm_height.png, in AtlasBaseIndex order. Open
// voxel_atlas_manifest.tres in the inspector to see every block's
// color/normal/height (with thumbnails), and press "Rebuild Atlas" to
// re-stitch the two strips.
//
// This resource is authoring-only and is NOT referenced by the running game —
// ChunkMesh loads the baked voxel_tiles.png Texture2DArray, never this manifest
// or the heavy source maps it points at. The headless mirror
// tools/stitch_voxel_atlas.py parses THIS .tres, so the Python/CI path and the
// editor button share one layer list.
//
// When adding a layer: append an AtlasLayer with the next AtlasBaseIndex, then
// bump slices/vertical in BOTH voxel_tiles.png.import and
// voxel_tiles_nrm_height.png.import to match the new layer count.
[Tool]
[GlobalClass]
public partial class VoxelAtlasManifest : Resource
{
    // Atlas slot size in px. Source art (1024/2048/4096) is downscaled to this.
    public const int Slot = 256;
    public const string ColorOutPath = "res://assets/textures/terrain/voxel_tiles.png";
    public const string NormalHeightOutPath = "res://assets/textures/terrain/voxel_tiles_nrm_height.png";

    // Encoded flat tangent normal (points straight out) for null-Normal slots.
    private static readonly Color FlatNormal = new Color(0.5f, 0.5f, 1.0f, 0.0f);

    // One entry per atlas layer, in AtlasBaseIndex order (entry i bakes into
    // layer i). Order is validated against each entry's Block.AtlasBaseIndex.
    [Export] public AtlasLayer[] layers;

    // Inspector button (Godot 4.4+). Re-stitches both strips from source art.
    [ExportToolButton("Rebuild Atlas")]
    public Callable RebuildButton => Callable.From(RebuildAtlas);

    // Re-stitch both atlas strips from the authored source maps and trigger a
    // filesystem rescan so Godot re-imports them. Editor / tool context only.
    public void RebuildAtlas()
    {
        if (!Validate())
        {
            return;
        }

        int n = layers.Length;
        Image colorStrip = Image.CreateEmpty(Slot, Slot * n, false, Image.Format.Rgb8);
        Image nhStrip = Image.CreateEmpty(Slot, Slot * n, false, Image.Format.Rgba8);

        for (int i = 0; i < n; i++)
        {
            AtlasLayer layer = layers[i];

            Image color = LoadSlot(layer.color, Image.Format.Rgb8);
            if (color == null)
            {
                GD.PushError($"VoxelAtlasManifest: layer {i} ('{LayerName(layer)}') failed to load its Color texture.");
                return;
            }
            colorStrip.BlitRect(color, new Rect2I(0, 0, Slot, Slot), new Vector2I(0, i * Slot));

            Image nrm = layer.normal != null ? LoadSlot(layer.normal, Image.Format.Rgb8) : null;
            Image hgt = layer.height != null ? LoadSlot(layer.height, Image.Format.L8) : null;
            for (int y = 0; y < Slot; y++)
            {
                for (int x = 0; x < Slot; x++)
                {
                    Color normalRgb = nrm == null ? FlatNormal : nrm.GetPixel(x, y);
                    float height = hgt == null ? 0.0f : hgt.GetPixel(x, y).R;
                    nhStrip.SetPixel(x, i * Slot + y, new Color(normalRgb.R, normalRgb.G, normalRgb.B, height));
                }
            }
        }

        Error err = colorStrip.SavePng(ProjectSettings.GlobalizePath(ColorOutPath));
        if (err == Error.Ok)
        {
            err = nhStrip.SavePng(ProjectSettings.GlobalizePath(NormalHeightOutPath));
        }
        if (err != Error.Ok)
        {
            GD.PushError($"VoxelAtlasManifest: save_png failed with error {err}.");
            return;
        }

        GD.Print($"VoxelAtlasManifest: wrote {n} layers to {ColorOutPath} + {NormalHeightOutPath}.");
        if (Engine.IsEditorHint())
        {
            EditorInterface.Singleton.GetResourceFilesystem().Scan();
        }
    }

    // True if the baked color atlas is missing or older than any source map.
    // Lets the editor plugin auto-rebuild when source art changes on disk.
    public bool IsStale()
    {
        ulong atlasMtime = FileAccess.GetModifiedTime(ColorOutPath);
        if (atlasMtime == 0 || layers == null)
        {
            return true;
        }
        foreach (AtlasLayer layer in layers)
        {
            if (layer == null)
            {
                continue;
            }
            foreach (Texture2D tex in new[] { layer.color, layer.normal, layer.height })
            {
                if (tex == null || string.IsNullOrEmpty(tex.ResourcePath))
                {
                    continue;
                }
                ulong srcMtime = FileAccess.GetModifiedTime(tex.ResourcePath);
                if (srcMtime != 0 && srcMtime > atlasMtime)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // Loads the ORIGINAL source file (not the VRAM-compressed import) so the
    // bake is free of block-compression artifacts, resized to one slot.
    private static Image LoadSlot(Texture2D tex, Image.Format format)
    {
        if (tex == null || string.IsNullOrEmpty(tex.ResourcePath))
        {
            return null;
        }
        Image img = Image.LoadFromFile(ProjectSettings.GlobalizePath(tex.ResourcePath));
        if (img == null)
        {
            return null;
        }
        if (img.GetSize() != new Vector2I(Slot, Slot))
        {
            img.Resize(Slot, Slot, Image.Interpolation.Lanczos);
        }
        if (img.GetFormat() != format)
        {
            img.Convert(format);
        }
        return img;
    }

    // Verifies the array is non-empty and each layer's position matches its
    // block's AtlasBaseIndex. Logs to GD.PushError; returns false to abort the
    // bake rather than write a misordered atlas.
    private bool Validate()
    {
        if (layers == null || layers.Length == 0)
        {
            GD.PushError("VoxelAtlasManifest: Layers is empty.");
            return false;
        }
        bool ok = true;
        for (int i = 0; i < layers.Length; i++)
        {
            AtlasLayer layer = layers[i];
            if (layer == null)
            {
                GD.PushError($"VoxelAtlasManifest: null layer at index {i}.");
                ok = false;
                continue;
            }
            if (layer.block != null && layer.block.atlasBaseIndex != i)
            {
                GD.PushError($"VoxelAtlasManifest: layer {i} is block '{layer.block.blockName}' with AtlasBaseIndex={layer.block.atlasBaseIndex}; array position must equal AtlasBaseIndex.");
                ok = false;
            }
        }
        return ok;
    }

    private static string LayerName(AtlasLayer layer)
    {
        return layer.block != null ? layer.block.blockName.ToString() : "<no block>";
    }
}
