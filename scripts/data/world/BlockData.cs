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

    // The atlas layer in voxel_tiles.png / voxel_tiles_nrm_height.png belonging
    // to this block. Each block is exactly one layer (the elevation-band /
    // variant system was removed). Stable wire id — the per-voxel OverlayId byte
    // is this index. When adding a block, append the next free index.
    [Export] public int AtlasBaseIndex;

    // Flat-shaded color for this block on the minimap.
    [Export] public Color MinimapColor = new Color(0.3f, 0.3f, 0.3f);

    // Cliff/rock material? Drives the terrain shader's height-blend ramps:
    // cliff↔ground blends tightly (sharp interlock), while cliff↔cliff and
    // ground↔ground blend softly. The shader routes each tile to a "cliff" or
    // "ground" accumulator by this flag (uploaded as tile_is_cliff[]).
    [Export] public bool IsCliff = false;

    // Wetness porosity in [0,1] — how absorbent the material is. LOW (rock,
    // cobble) = water beads on top and reads as reflective standing water; HIGH
    // (soil, mud, sand) = water soaks in, so the surface darkens/saturates with
    // little glint. The terrain shader's wet model splits its look by this
    // (uploaded as tile_porosity[]): albedo darkening scales with porosity,
    // glint/reflection scales with (1 - porosity).
    [Export(PropertyHint.Range, "0,1,0.01")] public float Porosity = 0.5f;

    // Logical ground category for footstep dispatch. GroundTypeResolver
    // resolves the voxel under the player's feet to a BlockData (overlay
    // wins over the voxel's flat tile) and reads this field. Multiple
    // blocks may share a category — DesertTop, DesertSand, DesertCave all
    // resolve to Sand. New categories should append to EGroundType.
    [Export] public EGroundType GroundType = EGroundType.Grass;

    // Material scooped up when the player digs a bare hole in this block —
    // i.e. the shovel finds no buried spot or burrowed mob (see World.TryDig).
    // Marsh yields mud; most blocks leave this null (digging bare ground comes
    // up empty). The item is dropped as loose loot at the dig point and the
    // shovel reports a Common find. Resolved through GroundTypeResolver, so
    // the overlay block wins over the base voxel just like footsteps.
    [Export] public ItemData DigItem;
}
