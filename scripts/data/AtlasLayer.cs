using Godot;

// One layer of the stitched voxel terrain atlas: a single BlockSurfaceData paired with
// the source PBR maps baked into that surface's slot in voxel_tiles.png /
// voxel_tiles_nrm_height.png. Authored only inside a VoxelAtlasManifest — this
// is an editor-time authoring record, NEVER loaded by the running game (the game
// loads the baked Texture2DArray, not these source files).
//
// The maps are paths relative to asset_src/ (AssetSource), not Texture2D refs:
// the sources sit outside Godot's import graph. Every map may be empty: an empty
// Normal bakes a flat tangent normal, an empty Height bakes zero displacement,
// and an empty Color blanks the whole row. That last one is how a surface claims
// an atlas index while authoring no art at all — Water, which voxel_water draws
// without ever sampling the atlas.
[Tool]
[GlobalClass]
public partial class AtlasLayer : Resource
{
    private const string SOURCE_FILTER = "*.png,*.jpg,*.jpeg,*.tga,*.exr";

    // The surface this art belongs to. Its atlasBaseIndex alone decides which
    // strip row the art bakes into, so the manifest array carries no ordering
    // meaning — entries can be reordered or inserted freely. Required.
    [Export] public BlockSurfaceData surface;

    // Base color (sRGB). Empty -> the row bakes blank (see above).
    [Export(PropertyHint.GlobalFile, SOURCE_FILTER)]
    public string color
    {
        get => _color;
        set => _color = AssetSource.Relativize(value);
    }

    // Tangent-space normal map. Empty -> flat normal (0.5, 0.5, 1.0).
    [Export(PropertyHint.GlobalFile, SOURCE_FILTER)]
    public string normal
    {
        get => _normal;
        set => _normal = AssetSource.Relativize(value);
    }

    // Height / displacement (grayscale). Empty -> zero height. Baked into the
    // alpha channel of voxel_tiles_nrm_height.png.
    [Export(PropertyHint.GlobalFile, SOURCE_FILTER)]
    public string height
    {
        get => _height;
        set => _height = AssetSource.Relativize(value);
    }

    private string _color;
    private string _normal;
    private string _height;
}
