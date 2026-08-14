using Godot;

// One layer of the stitched voxel terrain atlas: a single BlockSurfaceData paired with
// the source PBR maps baked into that surface's slot in voxel_tiles.png /
// voxel_tiles_nrm_height.png. Authored only inside a VoxelAtlasManifest — this
// is an editor-time authoring record, NEVER loaded by the running game (the game
// loads the baked Texture2DArray, not these source files).
//
// Every map may be null: a null Normal bakes a flat tangent normal, a null
// Height bakes zero displacement, and a null Color blanks the whole row. That
// last one is how a surface claims an atlas index while authoring no art at all
// — Water, which voxel_water draws without ever sampling the atlas.
[Tool]
[GlobalClass]
public partial class AtlasLayer : Resource
{
    // The surface this art belongs to. Its atlasBaseIndex alone decides which
    // strip row the art bakes into, so the manifest array carries no ordering
    // meaning — entries can be reordered or inserted freely. Required.
    [Export] public BlockSurfaceData surface;

    // Base color (sRGB). Null -> the row bakes blank (see above).
    [Export] public Texture2D color;

    // Tangent-space normal map. Null -> flat normal (0.5, 0.5, 1.0).
    [Export] public Texture2D normal;

    // Height / displacement (grayscale). Null -> zero height. Baked into the
    // alpha channel of voxel_tiles_nrm_height.png.
    [Export] public Texture2D height;
}
