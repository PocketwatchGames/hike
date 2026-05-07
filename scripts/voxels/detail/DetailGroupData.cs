using Godot;

// A palette of detail-sprite variants painted as a single brush. The per-voxel
// DetailGroup channel stores a 1-based index into the active world's detail
// palette (ZoneGenData.DetailGroups, uploaded via ChunkMesh.SetDetailGroups),
// so DetailGroup=0 means "no detail" and DetailGroup=1 references the first
// authored group. The DetailStrength channel (0..255) controls density within
// the painted zone.
//
// Adding a group: create the .tres, fill `Entries` with one or more
// DetailEntry sub-resources, and reference it from ZoneGenData.DetailGroups.
[GlobalClass]
public partial class DetailGroupData : Resource
{
    // Optional human-readable name for editor / debugging.
    [Export] public string GroupName = "";

    // Variants in this group. The scatter pass picks one per instance via
    // weight-proportional sampling. Empty arrays scatter nothing.
    [Export] public Godot.Collections.Array<DetailEntry> Entries = new();

    // Candidate slots rolled per painted voxel. Each slot rolls a hash against
    // DetailStrength/255 to decide whether to spawn an instance. With 4 slots
    // and strength 128, expected count per voxel is ~2.
    [Export] public int InstancesPerVoxel = 4;

    // Index into MinimapFoliageColors palette. 0 = no minimap stamp (the
    // surface terrain color shows through). Non-zero stamps the group's
    // authored color over the terrain pixel covering the painted voxel.
    [Export] public byte MinimapFoliageId = 0;
}
