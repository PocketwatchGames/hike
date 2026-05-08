using Godot;
using Godot.Collections;

// Authored list of entity entries used by the per-zone WorldGen scan passes
// (ZoneGenData.SurfaceEntities, CaveEntities, ShoreEntities, WaterEntities).
// Wrapping the array in a resource lets multiple zones share the same list
// asset (e.g. all four biomes pointing at one default_cave_entities.tres),
// so common cave/loot/chest content is authored once and reused.
//
// Differs from SpawnGroupData (cluster-around-anchor) in that the WorldGen
// loop scans candidate columns / cave cells, rolls each entry's Chance
// once per candidate, and calls Spawn on hit (one Spawn per scan hit).
// Subclasses that need their own count parameter (chest loot count, berry
// tree berry count) declare a purpose-named field and roll inside Spawn.
[GlobalClass]
public partial class SpawnListData : Resource
{
    [Export] public Array<SpawnEntryData> Entries = new();
}
