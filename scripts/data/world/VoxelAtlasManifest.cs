using Godot;

// Single editor-visible source of truth for the voxel terrain atlas mapping:
// which source PBR texture set is baked into each layer of voxel_tiles.png /
// voxel_tiles_nrm_height.png. Open voxel_atlas_manifest.tres in the inspector to
// see every surface's color/normal/height (with thumbnails), and press "Rebuild
// Atlas" to re-stitch the two strips.
//
// Each layer bakes into the row named by its own surface's AtlasBaseIndex, so
// the Layers array is an unordered set — reorder or insert entries freely. A row
// no surface claims is still baked (black + flat normal), because the rows above
// it have to keep their numbers: AtlasBaseIndex is a wire id, stored in the
// per-voxel OverlayId byte of every .hike.
//
// This resource is authoring-only and is NOT referenced by the running game —
// ChunkMesh loads the baked voxel_tiles.png Texture2DArray, never this manifest
// or the heavy source maps it points at. The headless mirror
// tools/stitch_voxel_atlas.py parses THIS .tres, so the Python/CI path and the
// editor button share one layer list.
//
// When adding a layer: add an AtlasLayer pointing at the new BlockSurfaceData
// and rebuild. The rebuild mints its AtlasBaseIndex and saves it back to the
// surface — never type one. RebuildAtlas also rewrites slices/vertical in both
// .import files to match the row count; that used to be a manual step, and
// missing it silently mis-slices EVERY tile (a 16-layer strip read as 12 gives
// 341px slabs), which looks like corrupted art rather than a stale number.
[Tool]
[GlobalClass]
public partial class VoxelAtlasManifest : Resource
{
    public const string ManifestResourcePath = "res://resources/data/surfaces/voxel_atlas_manifest.tres";

    // Atlas slot size in px. Source art (1024/2048/4096) is downscaled to this.
    public const int Slot = 256;
    public const string ColorOutPath = "res://assets/textures/terrain/voxel_tiles.png";
    public const string NormalHeightOutPath = "res://assets/textures/terrain/voxel_tiles_nrm_height.png";

    // Encoded flat tangent normal (points straight out) for null-Normal slots.
    private static readonly Color FlatNormal = new Color(0.5f, 0.5f, 1.0f, 0.0f);

    // One entry per authored surface, in any order — each bakes into the strip
    // row named by its own Surface.AtlasBaseIndex.
    [Export] public AtlasLayer[] layers;

    // Inspector button (Godot 4.4+). Re-stitches both strips from source art.
    [ExportToolButton("Rebuild Atlas")]
    public Callable RebuildButton => Callable.From(RebuildAtlas);

    // Re-stitch both atlas strips from the authored source maps and trigger a
    // filesystem rescan so Godot re-imports them. Editor / tool context only.
    public void RebuildAtlas()
    {
        MintMissingIndices();

        AtlasLayer[] rows = ResolveRows();
        if (rows == null)
        {
            return;
        }

        int n = rows.Length;
        Image colorStrip = Image.CreateEmpty(Slot, Slot * n, false, Image.Format.Rgb8);
        Image nhStrip = Image.CreateEmpty(Slot, Slot * n, false, Image.Format.Rgba8);

        for (int i = 0; i < n; i++)
        {
            AtlasLayer layer = rows[i];
            // No layer claims this row, or the surface that does authors no art
            // (Water: voxel_water draws it and never samples the atlas).
            if (layer == null || layer.color == null)
            {
                if (layer != null && (layer.normal != null || layer.height != null))
                {
                    GD.PushWarning($"VoxelAtlasManifest: row {i} ('{LayerName(layer)}') has a Normal/Height but no Color; baking the whole row blank.");
                }
                FillBlankRow(colorStrip, nhStrip, i);
                continue;
            }

            Image color = LoadSlot(layer.color, Image.Format.Rgb8);
            if (color == null)
            {
                GD.PushError($"VoxelAtlasManifest: row {i} ('{LayerName(layer)}') failed to load its Color texture.");
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

        SyncImportSliceCount(ColorOutPath, n);
        SyncImportSliceCount(NormalHeightOutPath, n);

        GD.Print($"VoxelAtlasManifest: wrote {n} layers to {ColorOutPath} + {NormalHeightOutPath}.");
#if TOOLS
        // EditorInterface lives in the editor-only assembly, so an export build
        // (ExportDebug / ExportRelease) cannot see it at all.
        if (Engine.IsEditorHint())
        {
            EditorInterface.Singleton.GetResourceFilesystem().Scan();
        }
#endif
    }

    // Point the texture's .import at the layer count we just baked. The importer
    // slices the strip by this number, so a stale value corrupts every tile.
    private static void SyncImportSliceCount(string texturePath, int layerCount)
    {
        string importPath = ProjectSettings.GlobalizePath(texturePath + ".import");
        if (!System.IO.File.Exists(importPath))
        {
            GD.PushWarning($"VoxelAtlasManifest: no .import beside {texturePath}; slices/vertical not updated.");
            return;
        }
        string[] lines = System.IO.File.ReadAllLines(importPath);
        bool found = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("slices/vertical="))
            {
                lines[i] = $"slices/vertical={layerCount}";
                found = true;
            }
        }
        if (!found)
        {
            GD.PushWarning($"VoxelAtlasManifest: {texturePath}.import has no slices/vertical line.");
            return;
        }
        System.IO.File.WriteAllLines(importPath, lines);
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

    // Hands every surface still at Unassigned the next free index and writes it
    // back to its own .tres, so an author adding a surface never types a layer
    // number. Indices already assigned are LEFT ALONE — they are wire ids, and
    // renumbering one re-textures the OverlayId bytes of every saved world.
    //
    // Saves only the surfaces it actually changed: the stitcher plugin rebuilds
    // on filesystem_changed, so saving unconditionally would loop.
    private void MintMissingIndices()
    {
        if (layers == null)
        {
            return;
        }

        int next = BlockSurfaceData.Unassigned;
        foreach (AtlasLayer layer in layers)
        {
            if (layer?.surface != null)
            {
                next = Mathf.Max(next, layer.surface.atlasBaseIndex);
            }
        }
        next += 1;

        foreach (AtlasLayer layer in layers)
        {
            BlockSurfaceData surface = layer?.surface;
            if (surface == null || surface.atlasBaseIndex != BlockSurfaceData.Unassigned)
            {
                continue;
            }
            if (string.IsNullOrEmpty(surface.ResourcePath))
            {
                GD.PushError($"VoxelAtlasManifest: surface '{surface.surfaceName}' needs an AtlasBaseIndex but has no file to save it to.");
                continue;
            }
            surface.atlasBaseIndex = next;
            next += 1;
            Error err = ResourceSaver.Save(surface, surface.ResourcePath);
            if (err != Error.Ok)
            {
                GD.PushError($"VoxelAtlasManifest: could not save AtlasBaseIndex={surface.atlasBaseIndex} to {surface.ResourcePath} (error {err}).");
                continue;
            }
            GD.Print($"VoxelAtlasManifest: assigned AtlasBaseIndex={surface.atlasBaseIndex} to '{surface.surfaceName}'.");
        }
    }

    // Scatters the authored layers into strip rows by their surface's
    // AtlasBaseIndex, sizing the strip to the highest one claimed. Logs to
    // GD.PushError and returns null to abort the bake rather than write an
    // ambiguous atlas.
    private AtlasLayer[] ResolveRows()
    {
        if (layers == null || layers.Length == 0)
        {
            GD.PushError("VoxelAtlasManifest: Layers is empty.");
            return null;
        }

        bool ok = true;
        int maxIndex = -1;
        foreach (AtlasLayer layer in layers)
        {
            if (layer == null)
            {
                GD.PushError("VoxelAtlasManifest: null entry in Layers.");
                ok = false;
                continue;
            }
            if (layer.surface == null)
            {
                GD.PushError("VoxelAtlasManifest: a layer has no Surface, so there is no row to bake it into.");
                ok = false;
                continue;
            }
            int index = layer.surface.atlasBaseIndex;
            if (index == BlockSurfaceData.Unassigned)
            {
                GD.PushError($"VoxelAtlasManifest: surface '{layer.surface.surfaceName}' has no AtlasBaseIndex and could not be assigned one.");
                ok = false;
                continue;
            }
            if (index < 0 || index >= BlockCatalog.MAX_ATLAS_LAYERS)
            {
                GD.PushError($"VoxelAtlasManifest: surface '{layer.surface.surfaceName}' has AtlasBaseIndex={index}, outside 0..{BlockCatalog.MAX_ATLAS_LAYERS - 1}.");
                ok = false;
                continue;
            }
            maxIndex = Mathf.Max(maxIndex, index);
        }
        if (!ok)
        {
            return null;
        }

        AtlasLayer[] rows = new AtlasLayer[maxIndex + 1];
        foreach (AtlasLayer layer in layers)
        {
            int index = layer.surface.atlasBaseIndex;
            if (rows[index] != null)
            {
                GD.PushError($"VoxelAtlasManifest: surfaces '{rows[index].surface.surfaceName}' and '{layer.surface.surfaceName}' both claim AtlasBaseIndex={index}.");
                ok = false;
                continue;
            }
            rows[index] = layer;
        }
        if (!ok)
        {
            return null;
        }

        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] == null)
            {
                GD.PushWarning($"VoxelAtlasManifest: no surface claims layer {i}; baking it black. Do NOT renumber to close the gap — AtlasBaseIndex is the OverlayId wire id, so it would re-texture saved worlds.");
            }
        }
        return rows;
    }

    // A row with no art: either no surface claims it, or the one that does
    // authors none. It still has to exist so the rows above keep their indices.
    private static void FillBlankRow(Image colorStrip, Image nhStrip, int row)
    {
        var rect = new Rect2I(0, row * Slot, Slot, Slot);
        colorStrip.FillRect(rect, Colors.Black);
        nhStrip.FillRect(rect, FlatNormal);
    }

    private static string LayerName(AtlasLayer layer)
    {
        return layer.surface != null ? layer.surface.surfaceName.ToString() : "<no surface>";
    }
}
