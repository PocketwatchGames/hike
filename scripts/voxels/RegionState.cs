// Per-region runtime state. WorldState.Regions[] holds one of these per
// region in the loaded world; ChunkState.RegionIndex picks one per chunk.
//
// `Data` is the authored RegionData (display name today; banner/music/loot
// hooks later). Null entries are border chunks — they keep CurrentRegion
// sticky in GameClient.UpdateRegion rather than firing a region-entry pulse.
//
// Mirrors ZoneState's shape so future per-world region-runtime fields (e.g.
// dynamic discovery state, per-region weather overrides) have an obvious
// home. No runtime-only fields exist yet, so this is a thin wrapper.
public struct RegionState
{
    public RegionData Data;
}
