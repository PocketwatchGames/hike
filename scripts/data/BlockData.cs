using Godot;

// Per-block authored data: one BlockData represents one named tile group in
// voxel_tiles.png (e.g. "Stone", "GrassTop", "DesertSand"). Replaces the flat
// VoxelTypeInfo.TILE_* int constants + the parallel TileVariants dict +
// MinimapTileColors named fields, so all per-block knobs live on one
// inspector-pickable resource.
//
// AtlasBaseIndex is still the wire id at the storage/shader seam — the
// per-voxel OverlayId byte and the shader's tile_variants[64] uniform are
// both indexed by it. BlockData carries it explicitly rather than hiding it
// so authors see the contract with the PNG layer order.
//
// Two BlockData entries may share physical layers in the PNG (e.g.
// DesertTop[bands=2] occupies layers 27..28 and DesertSand[bands=1] points at
// layer 28 as a non-banded alias for shore kits). The catalog allows this:
// uniqueness is enforced on AtlasBaseIndex, not on the layer range.
[GlobalClass]
public partial class BlockData : Resource
{
    // Logical name. Used for catalog lookup-by-name and for the boot-time
    // assertion that "Stone" maps to atlas 0 and "GrassTop" to atlas 1, since
    // the shader hardcodes those two ids as float constants.
    [Export] public StringName BlockName;

    // First layer in voxel_tiles.png belonging to this block. Stable wire id
    // — never reassign after the block is referenced from disk (kit .tres,
    // serialized chunk OverlayId bytes). When adding a new block, append to
    // the end of the PNG and pick the next free index.
    [Export] public int AtlasBaseIndex;

    // Elevation bands. Shader cycles bands by world Y at TILE_BAND_HEIGHT
    // intervals. Default 1 = no banding.
    [Export] public int Bands = 1;

    // Random per-voxel variants within each band. Shader picks via hash of
    // voxel position. Default 1 = no variation.
    [Export] public int VariantsPerBand = 1;

    // Minimap color, one entry per band. Length should equal Bands; the
    // catalog validator warns on mismatch. Variants within a band aren't
    // differentiated on the minimap (one color paints all variants).
    [Export] public Color[] MinimapColorPerBand = new Color[] { new Color(0.3f, 0.3f, 0.3f) };

    // Logical ground category for footstep dispatch. GroundTypeResolver
    // resolves the voxel under the player's feet to a BlockData (overlay
    // wins over the voxel's flat tile) and reads this field. Multiple
    // blocks may share a category — DesertTop, DesertSand, DesertCave all
    // resolve to Sand. New categories should append to EGroundType.
    [Export] public EGroundType GroundType = EGroundType.Grass;

    public int LayerCount => Bands * VariantsPerBand;

    public Color GetMinimapColor(int bandIndex)
    {
        if (MinimapColorPerBand == null || MinimapColorPerBand.Length == 0)
        {
            return new Color(0.3f, 0.3f, 0.3f);
        }
        if (bandIndex < 0)
        {
            bandIndex = 0;
        }
        if (bandIndex >= MinimapColorPerBand.Length)
        {
            bandIndex = MinimapColorPerBand.Length - 1;
        }
        return MinimapColorPerBand[bandIndex];
    }
}
