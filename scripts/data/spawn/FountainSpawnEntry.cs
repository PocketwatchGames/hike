using System;
using Godot;

// Places a single fountain (health or mana/lantern — the variant is carried by
// the scene). Wants flat, grassy ground so the basin doesn't tilt off a step
// edge. Scattered count-many times across the world by WorldGen.PlaceFountains.
[GlobalClass]
public partial class FountainSpawnEntry : SpawnEntryData
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
        ws.AddEntity(new FountainSimState(position, scene));
        ws.ClearDetailVoxelsWithin(position, detailSuppressionRadius);
    }
}
