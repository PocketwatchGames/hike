using System;
using Godot;

[GlobalClass]
public partial class MobSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene Scene;
    [Export] public MobData Data;

    // When true, the spawned MobSimState is marked SpawnAtNight so its node
    // only appears when its chunk activates after dark. Use this for groups
    // anchored to night-only encounters (e.g. mobs around a campfire).
    [Export] public bool SpawnAtNight;

    // Optional override for the brain's idleBehavior (e.g. "Wander"). Empty
    // means use the brain default.
    [Export] public StringName InitialBehavior;

    public override void Spawn(WorldState ws, Vector3 position, Random rng)
    {
        if (Scene == null || Data == null)
        {
            return;
        }
        var state = new MobSimState(
            position,
            (float)(rng.NextDouble() * Mathf.Pi * 2f),
            Scene,
            Data);
        state.SpawnAtNight = SpawnAtNight;
        if (InitialBehavior != null && (string)InitialBehavior != "")
        {
            state.InitialBehavior = InitialBehavior;
        }
        ws.AddEntity(state);
    }
}
