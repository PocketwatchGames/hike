using Godot;

// Resolves a block brush's palette icon to the SOURCE tile art behind its top
// surface, via the authoring-only VoxelAtlasManifest.
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

    // Null when the block draws nothing (Barrier is invisible collision, Opening
    // an invisible doorway/window marker) or the manifest isn't loaded — callers
    // fall back to the button's name label.
    public static Texture2D ForBlock(BlockData block, VoxelAtlasManifest manifest)
    {
        if (manifest?.layers == null || block == null || block.IsInvisible())
        {
            return null;
        }
        BlockSurfaceData top = block.SurfaceFor(EBlockFace.Top);
        if (top == null)
        {
            return null;
        }
        int layer = top.atlasBaseIndex;
        if (layer < 0 || layer >= manifest.layers.Length)
        {
            return null;
        }
        return manifest.layers[layer]?.color;
    }
}
