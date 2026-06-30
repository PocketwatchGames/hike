using Godot;

// Worldgen-side wrapper around a named RegionData, mirroring how ZoneGenData
// wraps ZoneData: it pairs the runtime output identity (`Region`, copied into
// WorldState.Regions[i].Data) with worldgen-only inputs. WorldGenData.Regions[]
// holds one per region; the index becomes the ChunkState.RegionIndex stamped on
// each chunk it owns.
//
// `Fixtures` is a list of one-off, region-scoped placements (a signpost, a
// knowledge stone, ...). WorldGen places each entry exactly once on a rolled
// valid column inside the region's footprint — landmark-style placement, not
// the density scan that ZoneGenData.SurfaceEntities drives. Keeping these here
// (rather than on RegionData) leaves RegionData a pure runtime output with no
// worldgen inputs leaking into it.
[GlobalClass]
public partial class RegionGenData : Resource
{
    [Export] public RegionData region;
    [Export] public SpawnListData fixtures;
}
