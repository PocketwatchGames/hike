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

    // Chance in [0, 1] that a spawned mob of this entry is an elite: 25% larger
    // and carrying one random signature status effect drawn from the spawn
    // zone's ZoneData.EliteStatusEffects pool. 0 (default) = never elite.
    [Export(PropertyHint.Range, "0,1,0.01")] public float EliteChance = 0f;

    // When this entry is a sub-entry of a SpawnGroupData cluster, scatter
    // this many mobs around the anchor (e.g. 2..3 goblins per camp). Ignored
    // in per-column list contexts (one mob per scan hit).
    [Export] public int ClusterCountMin = 1;
    [Export] public int ClusterCountMax = 1;

    // Mobs require flat terrain to keep physics from knocking them off step
    // edges into the cliff face below.
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
        state.SpawnConditions = spawnConditions;
        if (InitialBehavior != null && (string)InitialBehavior != ""
            && rng.NextDouble() < InitialBehaviorChance)
        {
            state.InitialBehavior = InitialBehavior;
        }
        if (EliteChance > 0f && rng.NextDouble() < EliteChance)
        {
            state.Elite = true;
            state.EliteStatusEffect = PickEliteStatusEffect(ws, position, rng);
        }
        ws.AddEntity(state);
    }

    // Draw one random signature effect from the elite pool of the zone owning
    // `position`. Resolves the zone via the chunk's stamped ZoneIndex — valid
    // here because worldgen stamps ZoneIndex and the zone table on WorldState
    // before the entity spawn passes run. Returns null when the chunk isn't
    // resident or the zone has no pool authored; an elite with a null effect is
    // still 25% larger, just without a status effect.
    private static StatusEffectData PickEliteStatusEffect(WorldState ws, Vector3 position, Random rng)
    {
        if (ws == null)
        {
            return null;
        }
        ChunkState chunk = ws.GetChunk(World.WorldToChunkCoord(position));
        if (chunk == null || ws.Zones == null || chunk.ZoneIndex >= ws.Zones.Length)
        {
            return null;
        }
        ZoneData zone = ws.Zones[chunk.ZoneIndex].Data;
        Godot.Collections.Array<StatusEffectData> pool = zone?.EliteStatusEffects;
        if (pool == null || pool.Count == 0)
        {
            return null;
        }
        return pool[rng.Next(pool.Count)];
    }
}
