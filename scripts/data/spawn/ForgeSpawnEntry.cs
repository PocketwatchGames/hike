using System;
using Godot;

// Places a single smithing forge. Its Level (rolled in [levelMin, levelMax]) is
// stamped onto the spawned ForgeSimState, scaling the upgrades the forge grants
// and the star pips on its HUD / map marker. Wants flat, grassy ground so the
// station doesn't tilt off a step edge.
[GlobalClass]
public partial class ForgeSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;
    // Forge level range, inclusive. Rolled per forge from the deterministic
    // worldgen rng so worlds stay reproducible. Levels drive the pip stars (1-5).
    [Export] public int levelMin = 1;
    [Export] public int levelMax = 5;
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
        int level = rng.Next(levelMin, levelMax + 1);
        ws.AddEntity(new ForgeSimState(position, scene, level));
        ws.ClearDetailVoxelsWithin(position, detailSuppressionRadius);
    }
}
