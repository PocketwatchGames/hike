using System;
using Godot;

// Spawns a Cactus.cs-backed hazard plant from a per-column scan. What the
// cactus does (spine count, damage, cooldown) lives on its scene's CactusData,
// so one entry type covers every variant.
[GlobalClass]
public partial class CactusSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    // Danger-zone radius mobs avoid while wandering and never spawn inside.
    [Export] public float hazardRadius = CactusSimState.DefaultHazardRadius;
    public override float HazardSpawnRadius => hazardRadius;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        ws.AddEntity(new CactusSimState(position, scene)
        {
            HazardRadius = hazardRadius,
        });
    }
}
