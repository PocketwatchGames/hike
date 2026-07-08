using System;
using Godot;

// Places a single smithing forge. Its Level is stamped onto the spawned
// ForgeSimState, scaling the gear the forge mints. Wants flat, grassy ground so
// the station doesn't tilt off a step edge.
[GlobalClass]
public partial class ForgeSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;
    [Export] public int level = 1;
    // Radius (meters) around the forge where worldgen-painted detail sprites are
    // erased so scattered foliage doesn't share the station's footprint.
    [Export] public float detailSuppressionRadius = 2f;

    public override bool RequireFlatTerrain => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new ForgeSimState(position, scene, level));
        ws.ClearDetailVoxelsWithin(position, detailSuppressionRadius);
    }
}
