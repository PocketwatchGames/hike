// Per-region runtime state. WorldState.Regions[] holds one of these per
// region in the loaded world; ChunkState.RegionIndex picks one per chunk.
//
// `Data` is the authored RegionData (display name today; banner/music/loot
// hooks later). Null entries are border chunks — they keep CurrentRegion
// sticky in GameClient.UpdateRegion rather than firing a region-entry pulse.
public struct RegionState
{
    public RegionData Data;
}
