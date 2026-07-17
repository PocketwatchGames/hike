using System;
using Godot;

// Places one standalone safety zone (around a starting area). The zone marks
// overlapping players safe (Player.IsSafe) so aggressive mobs disengage — see
// SafetyZone. Independent of any campfire: a starting area stays safe even after
// the spawn campfire is doused by lighting another fire elsewhere.
//
// Authored on the hub / village fixture group with placeAtAnchor so the zone
// centers on the spawn point; the footprint is the scene's CollisionShape3D.
[GlobalClass]
public partial class SafetyZoneSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new SafetyZoneSimState(position, scene));
    }
}
