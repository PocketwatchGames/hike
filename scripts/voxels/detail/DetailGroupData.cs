using Godot;

// A palette of detail-sprite variants painted as a single brush. The per-
// voxel DetailGroup channel stores a 1-based index into the active world's
// detail palette (built from each kit's DefaultDetail and uploaded via
// ChunkMesh.SetDetailGroups), so DetailGroup=0 means "no detail" and
// DetailGroup=1 references the first authored group. The DetailStrength
// channel (0..255) controls density within the painted zone.
//
// Adding a group: create the .tres, fill `Entries` with one or more
// DetailEntry sub-resources, and reference it from one or more
// TerrainKitData's DefaultDetail field.
[GlobalClass]
public partial class DetailGroupData : Resource
{
    // Optional human-readable name for editor / debugging.
    [Export] public string groupName = "";

    // Variants in this group. The scatter pass picks one per instance via
    // weight-proportional sampling. Empty arrays scatter nothing.
    [Export] public Godot.Collections.Array<DetailEntry> entries = new();

    // Candidate slots rolled per painted voxel. Each slot rolls a hash against
    // DetailStrength/255 to decide whether to spawn an instance. With 4 slots
    // and strength 128, expected count per voxel is ~2.
    [Export] public int instancesPerVoxel = 4;

    // Cliff filtering. Sprites are up to a metre wide and billboard, so one
    // scattered on a cliff lip reads as hanging out over the drop, and a 1m
    // shelf poking out of a cliff face sprouts a lone tuft.
    //   CliffStepVoxels     : how far a neighbouring column's surface must sit
    //                         below this one to count as a drop (or above it to
    //                         count as a wall). Ordinary bumpy terrain steps by
    //                         1, so 2 is the smallest value that reads as a
    //                         cliff. 0 disables both filters for this group.
    //   EdgeSetbackVoxels   : clearance kept from a lip. Density ramps from
    //                         none on the lip column to full this many voxels
    //                         inland, so the fringe thins instead of ending in
    //                         a bald stripe. 0 = scatter right up to the edge.
    //   MinLedgeWidthVoxels : narrowest continuous ground (measured through
    //                         this column on both horizontal axes) that still
    //                         takes detail. 1 = no ledge test. Only ground with
    //                         a DROP bounding it is judged — a passage pinched
    //                         between two walls keeps its detail.
    [Export] public int cliffStepVoxels = 2;
    [Export] public int edgeSetbackVoxels = 1;
    [Export] public int minLedgeWidthVoxels = 2;

    // Index into MinimapFoliageColors palette. 0 = no minimap stamp (the
    // surface terrain color shows through). Non-zero stamps the group's
    // authored color over the terrain pixel covering the painted voxel.
    [Export] public byte minimapFoliageId = 0;
}
