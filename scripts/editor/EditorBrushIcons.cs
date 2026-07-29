using Godot;

// Resolves a voxel brush's palette icon to the SOURCE tile art behind its atlas
// layer, via the authoring-only VoxelAtlasManifest.
//
// The manifest and its heavy source PBR maps are deliberately NOT referenced by
// anything the running game loads — hence LoadManifest taking a path and being
// called only when the world editor opens. Do not turn that path into a typed
// [Export] resource reference: editor.tscn is an ext_resource of main.tscn, so a
// direct ref would pull the manifest and every source texture into memory at
// game startup, shipped build included.
public static class EditorBrushIcons
{
    public static VoxelAtlasManifest LoadManifest(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        var manifest = ResourceLoader.Load<VoxelAtlasManifest>(path);
        if (manifest == null)
        {
            GD.PushWarning($"EditorBrushIcons: could not load atlas manifest '{path}'; voxel brushes will show name labels instead of tiles.");
        }
        return manifest;
    }

    // Null when the type has no single representative tile (Barrier is invisible
    // collision), when the terrain kit is unresolved, or when the manifest isn't
    // loaded — callers fall back to the button's name label.
    public static Texture2D ForVoxelType(VoxelType type, TerrainKitData terrainKit, VoxelAtlasManifest manifest)
    {
        if (manifest?.layers == null || type == VoxelType.Barrier)
        {
            return null;
        }

        // Terrain carries no fixed tile (TILE_AUTO) — preview the flat tile of
        // the kit the Terrain brush actually stamps. A kit that leaves flatTile
        // unauthored renders the catalog default, so mirror that here or the
        // most-used brush is the one with no icon.
        int layer = type == VoxelType.Terrain
            ? BlockCatalog.Active.DefaultFlatTileIndex
            : VoxelTypeInfo.GetTileForFace(type, 0);
        if (type == VoxelType.Terrain && terrainKit?.terrain?.flatTile != null)
        {
            layer = terrainKit.terrain.flatTile.atlasBaseIndex;
        }

        if (layer < 0 || layer >= manifest.layers.Length)
        {
            return null;
        }
        return manifest.layers[layer]?.color;
    }
}
