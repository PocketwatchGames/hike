using System;
using Godot;

// Places a single healing fountain. Wants flat, grassy ground so the basin
// doesn't tilt off a step edge. Placed count-many times across the world by
// WorldGen.PlaceHealingFountains.
[GlobalClass]
public partial class HealingFountainSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;
    // Radius (meters) around the fountain where worldgen-painted detail sprites
    // are erased so scattered foliage doesn't share the station's footprint.
    [Export] public float detailSuppressionRadius = 2f;

    public override bool RequireFlatTerrain => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new HealingFountainSimState(position, scene));
        ws.ClearDetailVoxelsWithin(position, detailSuppressionRadius);
    }
}
