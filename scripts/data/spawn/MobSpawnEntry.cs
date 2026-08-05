using System;
using Godot;

[GlobalClass]
public partial class MobSpawnEntry : SpawnEntryData
{
    // The composed mob to spawn — base species + per-instance overrides
    // (palette, elite kind). Replaces the bare MobData this entry used to hold;
    // see MobDescriptor.
    [Export] public MobDescriptor descriptor;

    // Optional override for the brain's idleBehavior (e.g. "Wander"). Empty
    // means use the brain default. Combined with InitialBehaviorChance for
    // probabilistic overrides — set InitialBehaviorChance=0.25 to make a
    // quarter of spawned goblins start in Wander instead of the brain's
    // default Idle.
    [Export] public StringName initialBehavior;
    [Export(PropertyHint.Range, "0,1,0.01")] public float initialBehaviorChance = 1f;

    // When this entry is a sub-entry of a SpawnGroupData cluster, scatter
    // this many mobs around the anchor (e.g. 2..3 goblins per camp). Ignored
    // in per-column list contexts (one mob per scan hit).
    [Export] public int clusterCountMin = 1;
    [Export] public int clusterCountMax = 1;

    // Mobs require flat terrain to keep physics from knocking them off step
    // edges into the cliff face below. Water-bound mobs are exempt — they spawn
    // in the water column, where the dry-ground flatness test is meaningless
    // (and would reject every submerged cell).
    public override bool RequireFlatTerrain => descriptor?.mob?.CanTraverseLand != false;

    // Cave pockets pre-validate only the spawn column itself, so a wall-
    // adjacent column passes — and a mob whose hitbox is wider than the
    // column's lateral half-voxel margin can wind up clipped into the
    // wall. Forcing all 4 lateral neighbors air gives mobs a corridor
    // they can settle into. Water-bound mobs are exempt — their lateral
    // neighbors are water, not air, so this check would always reject them.
    public override bool RequireLateralClearance => descriptor?.mob?.CanTraverseLand != false;

    public override bool IsMobEntry => true;

    // Authoritative spawn gate: sample the navigation walkability column with
    // this mob's own traversal profile (body radius, step/headroom) and accept
    // only if it yields a walkable surface at the spawn height. Catches spots
    // the cheaper flat/lateral gates miss — a body too wide for the slot,
    // diagonal walls, insufficient headroom — so a mob never spawns somewhere
    // it then can't stand or navigate out of. world is null at worldgen, so
    // this is a pure voxel-grid test (no path-blocker awareness needed — no
    // entity nodes exist yet, and overlap is handled by MinSpacing).
    public override bool IsSpawnPositionWalkable(WorldState ws, Vector3 position)
    {
        MobData data = descriptor?.mob;
        if (data == null)
        {
            // No profile to test against — defer to the other gates.
            return true;
        }
        var profile = new TraversalProfile(data);
        int wx = Mathf.FloorToInt(position.X);
        int wz = Mathf.FloorToInt(position.Z);
        int anchorY = Mathf.FloorToInt(position.Y);
        var cells = new WalkabilityCell[WalkabilityGrid.MaxColumnLayers];
        WalkabilityGrid.SampleColumn(ws, null, profile, wx, anchorY, wz, cells, 0);
        // Layers are packed from slot 0 (highest surface) until the first
        // non-walkable slot; accept if any standable layer sits at the spawn
        // height (±1 voxel of float slack).
        for (int layer = 0; layer < WalkabilityGrid.MaxColumnLayers; layer++)
        {
            WalkabilityCell c = cells[layer];
            if (!c.Walkable)
            {
                break;
            }
            if (Mathf.Abs(c.surfaceY - anchorY) <= 1)
            {
                return true;
            }
        }
        return false;
    }

    public override int RollCount(Random rng)
    {
        return rng.Next(clusterCountMin, clusterCountMax + 1);
    }

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (descriptor == null)
        {
            return;
        }
        float rotationY = context?.FacingY ?? (float)(rng.NextDouble() * Mathf.Pi * 2f);
        // Layer the per-area worldgen level field (and underground bonus) onto the
        // descriptor's authored base level, then hand the final tier to CreateState
        // so the mob's vitals are scaled to it at construction (before this state is
        // baked into the .hike). The constructor forces non-dangerous mobs to 0, so
        // computing a tier here for prey / villagers is harmless.
        int level = WorldGen.ComputeMobLevel(ws, position, descriptor.level);
        MobSimState state = descriptor.CreateState(position, rotationY, levelOverride: level, levelScalePerLevel: ws.SimData?.levelScalePerLevel ?? 1.5f);
        if (state == null)
        {
            return;
        }
        state.SpawnConditions = spawnConditions;
        if (initialBehavior != null && (string)initialBehavior != ""
            && rng.NextDouble() < initialBehaviorChance)
        {
            state.InitialBehavior = initialBehavior;
        }
        ws.AddEntity(state);
    }
}
