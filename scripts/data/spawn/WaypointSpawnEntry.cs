using System;
using Godot;

// A named spot and nothing else: places no entity, so the only thing it leaves
// in the world is the point of interest the bake registers for any NAMED
// placement. That is what a script sends a mob to (WorldScriptApi.SendTo,
// Reappear) and what `tp` reaches. Unnamed it does nothing at all, which
// worldmap_check reports.
[GlobalClass]
public partial class WaypointSpawnEntry : SpawnEntryData
{
    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
    }
}
