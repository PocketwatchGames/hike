using System.Collections.Generic;
using Godot;

// Resolves a block brush's palette icon to the tile the game draws for its top
// surface: that surface's layer of the baked voxel_tiles.png atlas.
public sealed class EditorBrushIcons
{
    private readonly TextureLayered _atlas;
    private readonly Dictionary<int, Texture2D> _byLayer = new();

    public EditorBrushIcons(TextureLayered atlas)
    {
        _atlas = atlas;
    }

    // Null when the block draws nothing from the atlas (Barrier is invisible
    // collision, Opening an invisible marker, water is drawn by voxel_water over a
    // blank row) or the layer can't be read back on the CPU (no atlas, or the
    // dummy renderer) — callers fall back to the button's name label.
    public Texture2D ForBlock(BlockData block)
    {
        if (_atlas == null || block == null || block.IsInvisible() || block.render == EBlockRender.Water)
        {
            return null;
        }
        BlockSurfaceData top = block.SurfaceFor(EBlockFace.Top);
        if (top == null)
        {
            return null;
        }
        int layer = top.atlasBaseIndex;
        if (layer < 0 || layer >= _atlas.GetLayers())
        {
            return null;
        }
        if (_byLayer.TryGetValue(layer, out Texture2D icon))
        {
            return icon;
        }
        Image image = _atlas.GetLayerData(layer);
        // The imported atlas is VRAM-compressed; an ImageTexture needs it decoded.
        if (image != null && image.IsCompressed() && image.Decompress() != Error.Ok)
        {
            image = null;
        }
        icon = image != null ? ImageTexture.CreateFromImage(image) : null;
        _byLayer[layer] = icon;
        return icon;
    }
}
