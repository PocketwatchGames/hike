using System;
using Godot;

[GlobalClass]
public partial class FireTrapSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;

    // Per-instance random phase offset for the fire-column trap's first idle
    // window — keeps neighbouring traps out of lockstep so a swamp full of
    // them feels like the Princess Bride fire swamp rather than a metronome.
    [Export] public float MaxPhaseOffsetSeconds = 8f;

    // Danger-zone radius mobs avoid while wandering and never spawn inside
    // (the fire column's damage cylinder is ~0.7m; pad it so mobs don't clip
    // the edge). Attack pathing ignores it so the player can lure mobs in.
    [Export] public float HazardRadius = FireTrapSimState.DefaultHazardRadius;
    public override float HazardSpawnRadius => HazardRadius;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Scene == null)
        {
            return;
        }
        var fireTrap = new FireTrapSimState(position, Scene);
        fireTrap.PhaseOffsetSeconds = (float)(rng.NextDouble() * MaxPhaseOffsetSeconds);
        fireTrap.HazardRadius = HazardRadius;
        ws.AddEntity(fireTrap);
    }
}
