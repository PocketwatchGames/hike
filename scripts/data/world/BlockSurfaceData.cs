using Godot;

// One dressed surface: a named layer of voxel_tiles.png plus the handful of
// properties that are genuinely per-TEXTURE rather than per-voxel. A BlockData
// wears up to three of these (top / side / bottom).
//
// Everything a voxel has rather than a texture — footsteps, dig yield, speed,
// minimap colour, edge roughness — lives on BlockData. The test is how the
// shader consumes it: porosity is uploaded as tile_porosity[], indexed by atlas
// layer and blended per fragment across neighbouring tiles, so it has to be
// per-layer. Nothing else is.
//
// AtlasBaseIndex is a wire id: the per-voxel OverlayId byte is one of these.
[GlobalClass]
public partial class BlockSurfaceData : Resource
{
    // Logical name — the key for catalog lookup-by-name, so layers can be
    // renumbered freely. Renaming one is the breaking change, not re-indexing.
    [Export] public StringName surfaceName;

    // This surface's layer in voxel_tiles.png / voxel_tiles_nrm_height.png.
    // Append the next free index when adding one.
    [Export] public int atlasBaseIndex;

    // Wetness porosity in [0,1] — how absorbent the material is. LOW (rock,
    // cobble) = water beads on top and reads as reflective standing water; HIGH
    // (soil, mud, sand) = water soaks in, so the surface darkens/saturates with
    // little glint. The terrain shader's wet model splits its look by this
    // (uploaded as tile_porosity[]): albedo darkening scales with porosity,
    // glint/reflection scales with (1 - porosity).
    [Export(PropertyHint.Range, "0,1,0.01")] public float porosity = 0.5f;
}
