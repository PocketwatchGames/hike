using Godot;
using Godot.Collections;

// Authored list of things to place, used by the per-zone WorldGen scan passes
// (ZoneGenData.surfaceEntities, caveEntities, shoreEntities, waterEntities),
// by the world-map painter's ambient sets, and by the fixture / POI / subscene
// placements.
//
// A SpawnListRow is a shared SpawnEntryData plus this list's own rate and
// conditions for it — so the file reads as a list of named things, and one well.tres is named
// by every list that wants a well rather than re-authored in each. Wrapping the
// array in a resource lets several zones share the whole list too (all four
// biomes pointing at one cave_entities_default.tres).
//
// Differs from SpawnGroupData (cluster around an anchor) in that the WorldGen
// loop scans candidate columns / cave cells, rolls each row's
// squareMetersPerSpawn area chance once per candidate, and spawns on a hit.
[GlobalClass]
public partial class SpawnListData : Resource
{
    [Export] public Array<SpawnListRow> rows = new();
}
