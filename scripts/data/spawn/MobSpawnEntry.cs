using System;
using Godot;

[GlobalClass]
public partial class MobSpawnEntry : SpawnEntryData
{
    // The composed mob to spawn — base species + per-instance overrides
    // (palette, elite kind). Replaces the bare MobData this entry used to hold;
    // see MobDescriptor.
    [Export] public MobDescriptor descriptor;

    // The descriptors THIS entry may be set to — the biome variants, elites and
    // torchbearers that are all the same creature. One palette entry per family
    // ("goblin"), with the member picked per placement, so selecting it on the
    // map highlights every goblin rather than one biome's.
    //
    // Authored rather than derived: grouping by SpeciesData is per-BIOME (the
    // plain, elite and torchbearer swamp goblins share one species, but the
    // forest goblin does not), and a filename prefix would make a naming rule
    // load-bearing with nothing enforcing it. It also lets the author decide
    // where a family's edges are — whether a cube and a sphere slime are one.
    //
    // Empty leaves the entry a single-variant one, which is what every worldgen
    // spawn list is: those name a descriptor outright and never offer a choice.
    [Export] public MobDescriptor[] variants = System.Array.Empty<MobDescriptor>();

    // Difficulty tier for THIS placement, overriding the descriptor's authored
    // base. Negative = use the descriptor's.
    //
    // It has to live here rather than being edited through the descriptor: the
    // descriptor is SHARED (every placement of a variant, and worldgen's own
    // spawns, point at one .tres) and EntityPlacement's fork is shallow, so
    // editing `descriptor.level` through the panel would retune every one of
    // them at once.
    //
    // Semantics match the field it replaces — a FLOOR, not a final answer. The
    // painted difficulty layer still adds on top via SpawnContext.MobLevel, so
    // this raises a mob above its area rather than pinning it.
    [Export(PropertyHint.Range, "-1,4,1")] public int levelOverride = -1;

    // Optional override for the brain's idleBehavior (e.g. "Wander"). Empty
    // means use the brain default. Combined with InitialBehaviorChance for
    // probabilistic overrides — set InitialBehaviorChance=0.25 to make a
    // quarter of spawned goblins start in Wander instead of the brain's
    // default Idle.
    [Export] public StringName initialBehavior;
    [Export(PropertyHint.Range, "0,1,0.01")] public float initialBehaviorChance = 1f;

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

    // Which descriptor of its family this one is, so an entry covering a whole
    // family still names the individual in the hover readout and the panel title.
    public override string VariantName()
        => descriptor != null ? descriptor.ResourcePath.GetFile().GetBaseName() : null;

    // Constrained to the family, which is what makes the descriptor row safe to
    // show: a goblin entry offers only goblins, so a fork can never become a
    // spider while still being named — and highlighted — as a goblin.
    public override Resource[] ResourceCandidates(StringName property)
    {
        if (property != PropertyName.descriptor || variants == null || variants.Length == 0)
        {
            return base.ResourceCandidates(property);
        }
        return variants;
    }

    // The behaviour nodes of the brain THIS entry's species runs — transitions
    // already reference each other by BehaviorNode.name, so that is the exact
    // set a valid initialBehavior can come from.
    public override string[] NameCandidates(StringName property)
    {
        if (property != PropertyName.initialBehavior)
        {
            return base.NameCandidates(property);
        }
        Godot.Collections.Array<BehaviorNode> nodes = descriptor?.mob?.brain?.behaviors;
        if (nodes == null)
        {
            return null;
        }
        var names = new System.Collections.Generic.List<string>();
        // Count hoisted: this is a Godot.Collections.Array, so .Count is a
        // native call per iteration.
        int count = nodes.Count;
        for (int i = 0; i < count; i++)
        {
            BehaviorNode node = nodes[i];
            if (node?.name != null && !node.name.IsEmpty)
            {
                names.Add(node.name.ToString());
            }
        }
        names.Sort();
        return names.ToArray();
    }

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
        int baseLevel = levelOverride >= 0 ? levelOverride : descriptor.level;
        int level = context != null
            ? context.MobLevel(position, baseLevel)
            : Math.Max(0, baseLevel);
        MobSimState state = descriptor.CreateState(position, rotationY, levelOverride: level, levelScalePerLevel: ws.SimData?.levelScalePerLevel ?? 1.5f);
        if (state == null)
        {
            return;
        }
        state.SpawnConditions = context?.SpawnConditions ?? ESpawnConditions.None;
        // The chance is a POPULATION fraction ("a quarter of spawned goblins
        // start in Wander"), so it has nothing to be a fraction of when someone
        // placed this one by hand — an authored placement always takes the
        // behaviour it names.
        if (initialBehavior != null && (string)initialBehavior != ""
            && (context?.AuthoredPosition == true
                || rng.NextDouble() < initialBehaviorChance))
        {
            state.InitialBehavior = initialBehavior;
        }
        ws.AddEntity(state);
    }
}
