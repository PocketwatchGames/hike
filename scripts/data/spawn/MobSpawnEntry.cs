using System;
using Godot;

[GlobalClass]
public partial class MobSpawnEntry : SpawnEntryData
{
    [Export] public MobData Data;

    // Optional override for the brain's idleBehavior (e.g. "Wander"). Empty
    // means use the brain default. Combined with InitialBehaviorChance for
    // probabilistic overrides — set InitialBehaviorChance=0.25 to make a
    // quarter of spawned goblins start in Wander instead of the brain's
    // default Idle.
    [Export] public StringName InitialBehavior;
    [Export(PropertyHint.Range, "0,1,0.01")] public float InitialBehaviorChance = 1f;

    // When this entry is a sub-entry of a SpawnGroupData cluster, scatter
    // this many mobs around the anchor (e.g. 2..3 goblins per camp). Ignored
    // in per-column list contexts (one mob per scan hit).
    [Export] public int ClusterCountMin = 1;
    [Export] public int ClusterCountMax = 1;

    // Mobs require flat terrain to keep physics from knocking them off step
    // edges into the cliff face below — currently a diagnostic to test
    // whether step-edge spawns are the source of "mob ended up inside a
    // voxel"; if flat-only spawn doesn't fix the symptom, the cause is
    // elsewhere (e.g. chunk collision not yet registered).
    public override bool RequireFlatTerrain => true;

    // Cave pockets pre-validate only the spawn column itself, so a wall-
    // adjacent column passes — and a mob whose hitbox is wider than the
    // column's lateral half-voxel margin can wind up clipped into the
    // wall. Forcing all 4 lateral neighbors air gives mobs a corridor
    // they can settle into.
    public override bool RequireLateralClearance => true;

    public override int RollCount(Random rng)
    {
        return rng.Next(ClusterCountMin, ClusterCountMax + 1);
    }

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Data == null || Data.MobScene == null)
        {
            return;
        }
        var state = new MobSimState(
            position,
            (float)(rng.NextDouble() * Mathf.Pi * 2f),
            Data.MobScene,
            Data);
        state.SpawnAtNight = SpawnAtNight;
        if (InitialBehavior != null && (string)InitialBehavior != ""
            && rng.NextDouble() < InitialBehaviorChance)
        {
            state.InitialBehavior = InitialBehavior;
        }
        ws.AddEntity(state);
    }
}
