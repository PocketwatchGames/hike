using System;
using Godot;

[GlobalClass]
public partial class FireTrapSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    // Per-instance random phase offset for the fire-column trap's first idle
    // window — keeps neighbouring traps out of lockstep so a swamp full of
    // them feels like the Princess Bride fire swamp rather than a metronome.
    [Export] public float maxPhaseOffsetSeconds = 8f;

    // Danger-zone radius mobs avoid while wandering and never spawn inside
    // (the fire column's damage cylinder is ~0.7m; pad it so mobs don't clip
    // the edge). Attack pathing ignores it so the player can lure mobs in.
    [Export] public float hazardRadius = FireTrapSimState.DefaultHazardRadius;
    public override float HazardSpawnRadius => hazardRadius;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        var fireTrap = new FireTrapSimState(position, scene);
        fireTrap.PhaseOffsetSeconds = (float)(rng.NextDouble() * maxPhaseOffsetSeconds);
        fireTrap.HazardRadius = hazardRadius;
        ws.AddEntity(fireTrap);
    }
}
