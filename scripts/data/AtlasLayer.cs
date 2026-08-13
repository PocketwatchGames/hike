using Godot;

// One layer of the stitched voxel terrain atlas: a single BlockSurfaceData paired with
// the source PBR maps baked into that block's slot in voxel_tiles.png /
// voxel_tiles_nrm_height.png. Authored only inside a VoxelAtlasManifest — this
// is an editor-time authoring record, NEVER loaded by the running game (the game
// loads the baked Texture2DArray, not these source files).
//
// Normal/Height may be null (e.g. the Water placeholder slot, which the water
// shader renders): a null Normal bakes a flat tangent normal, a null Height
// bakes zero displacement.
[Tool]
[GlobalClass]
public partial class AtlasLayer : Resource
{
    // The block that occupies this atlas slot. This layer's position in the
    // manifest's Layers array must equal Block.AtlasBaseIndex; the manifest
    // validates this before stitching so the PNG layer order can never silently
    // drift from the authored wire ids.
    [Export] public BlockSurfaceData block;

    // Base color (sRGB). Required.
    [Export] public Texture2D color;

    // Tangent-space normal map. Null -> flat normal (0.5, 0.5, 1.0).
    [Export] public Texture2D normal;

    // Height / displacement (grayscale). Null -> zero height. Baked into the
    // alpha channel of voxel_tiles_nrm_height.png.
    [Export] public Texture2D height;
}
