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
//
// [Tool] because VoxelAtlasManifest is [Tool] and AtlasLayer holds one of these:
// the editor can only hand a C# scripted resource to a strongly-typed [Tool]
// setter if the class itself is [Tool]. Without it the reference silently
// arrives as a base Godot.Resource, the setter throws, the field reads empty in
// the inspector, and the next save drops it.
[Tool]
[GlobalClass]
public partial class BlockSurfaceData : Resource
{
    // Logical name — the key for catalog lookup-by-name, so layers can be
    // renumbered freely. Renaming one is the breaking change, not re-indexing.
    [Export] public StringName surfaceName;

    // Never authored by hand — read-only in the inspector. VoxelAtlasManifest
    // mints it on Rebuild Atlas for any surface still at Unassigned and saves it
    // back here; an index it already has is never touched, because it is a wire
    // id (see above) and renumbering would re-texture saved worlds.
    public const int Unassigned = -1;

    // This surface's layer in voxel_tiles.png / voxel_tiles_nrm_height.png.
    [Export] public int atlasBaseIndex = Unassigned;

    // Wetness porosity in [0,1] — how absorbent the material is. LOW (rock,
    // cobble) = water beads on top and reads as reflective standing water; HIGH
    // (soil, mud, sand) = water soaks in, so the surface darkens/saturates with
    // little glint. The terrain shader's wet model splits its look by this
    // (uploaded as tile_porosity[]): albedo darkening scales with porosity,
    // glint/reflection scales with (1 - porosity).
    [Export(PropertyHint.Range, "0,1,0.01")] public float porosity = 0.5f;

    // When this surface is used as an OVERLAY, whether it also creeps onto cliff
    // faces instead of stopping at flat ground. Moss wants this (it breaks up a
    // bare wall); a dirt or road tread does not, and would read as mud running
    // up a vertical rock face. Uploaded per layer as tile_overlay_cliff[].
    [Export] public bool overlayOnCliffs = false;

    // A wall face wearing this can be climbed. Lives here rather than only on
    // BlockData because climbability travels with the TEXTURE — lichen is
    // climbable wherever it grows — which is the same test porosity passes.
    // Without it an overlay could only confer this through a proxy block whose
    // top surface happened to be this one.
    [Export] public bool climbable = false;

    // Minimap colour for this surface when it is drawn as an OVERLAY. The
    // minimap indexes its lookup by atlas layer and an overlay wins outright
    // over the block under it, so a layer worn by no block has no colour to
    // fall back on and paints the unauthored magenta.
    [Export] public Color minimapColor = new Color(0.3f, 0.3f, 0.3f);

    // Show atlasBaseIndex but forbid editing it. Kept visible rather than hidden
    // because it is the wire id you need when reading OverlayId bytes out of a
    // .hike or chasing a mis-textured row.
    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        if (property["name"].AsStringName() != nameof(atlasBaseIndex))
        {
            return;
        }
        PropertyUsageFlags usage = property["usage"].As<PropertyUsageFlags>() | PropertyUsageFlags.ReadOnly;
        property["usage"] = (int)usage;
    }
}
