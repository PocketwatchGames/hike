using Godot;

// One entry in WorldGenData.Zones: pairs a reusable ZoneGenData template with
// the per-world ZoneBounds that says where it goes. The index of a PlacedZone in
// WorldGenData.Zones is the ChunkState.ZoneIndex stamped on every chunk it owns
// (and the slot in WorldState.Zones[]).
//
// Keeping the bounds here — rather than on ZoneGenData — lets the same zone
// template (e.g. swamp_gen.tres) be placed differently in different worlds
// without the template carrying any location.
[GlobalClass]
public partial class PlacedZone : Resource
{
    [Export] public ZoneGenData ZoneGen;
    [Export] public ZoneBounds Bounds;
}
