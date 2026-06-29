using Godot;

// Binds authored spawn content to a named point of interest so WorldGen places
// it AT that POI's resolved position instead of on a random column. This is the
// generic "spawn things at a named place" hook — signposts today (the same
// SignpostSpawnEntry that RegionGenData.Fixtures used to carry), bosses /
// important loot / village fixtures later — all reusing the existing
// SpawnEntryData machinery via Content's entries.
[GlobalClass]
public partial class PoiPlacement : Resource
{
    // The POI to anchor on (a name from ZoneData.PointsOfInterest). A placement
    // whose name doesn't resolve is skipped.
    [Export] public string PoiName = "";

    // Entries spawned at the POI position. Each entry's TrySpawn is invoked
    // there, so a placement can be a single signpost or a small cluster.
    [Export] public SpawnListData Content;
}
