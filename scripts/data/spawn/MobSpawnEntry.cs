using System;
using Godot;

[GlobalClass]
public partial class MobSpawnEntry : SpawnEntryData
{
    // The creature this entry spawns. See SpeciesData.
    [Export] public SpeciesData species;

    public override Texture2D PaletteIcon => species?.mob?.bestiaryPortrait;

    // The elite signature this spawn wears, or null for an ordinary mob. It
    // lives HERE rather than on the species because being elite is a property of
    // one spawn, not of a creature: an elite goblin is the same bestiary row,
    // discovery and kill-quest target as a plain one, and pairing the field with
    // `species` spares the species x loadout x elite crossproduct a file each.
    // See EliteData.
    [Export] public EliteData elite;

    // The species THIS entry may be set to — the biome and loadout variants that
    // are all the same creature. One palette entry per family ("goblin"), with
    // the member picked per placement, so selecting it on the map highlights
    // every goblin rather than one biome's.
    //
    // Authored rather than derived: a filename prefix would make a naming rule
    // load-bearing with nothing enforcing it, and this lets the author decide
    // where a family's edges are — whether a cube and a sphere slime are one.
    //
    // Empty leaves the entry a single-variant one, which is what every worldgen
    // spawn list is: those name a species outright and never offer a choice.
    [Export] public SpeciesData[] variants = System.Array.Empty<SpeciesData>();

    // Difficulty tier floor for THIS placement — a FLOOR, not a final answer.
    // The painted difficulty layer adds on top via SpawnContext.MobLevel, so
    // this raises a mob above its area rather than pinning it. 0 = base.
    [Export(PropertyHint.Range, "0,4,1")] public int level = 0;

    // The behaviour THIS individual starts in instead of its brain's idle (e.g.
    // "Wander"), always. Set on a placement's own copy; a shared entry leaves it
    // empty, because how many of a creature wander is a population rule and
    // belongs to the row that places them (SpawnRow.initialBehavior). Empty
    // defers to that row, then to the brain.
    [Export] public StringName initialBehavior;

    // Mobs require flat terrain to keep physics from knocking them off step
    // edges into the cliff face below. Water-bound mobs are exempt — they spawn
    // in the water column, where the dry-ground flatness test is meaningless
    // (and would reject every submerged cell).
    public override bool RequireFlatTerrain => species?.mob?.CanTraverseLand != false;

    // Cave pockets pre-validate only the spawn column itself, so a wall-
    // adjacent column passes — and a mob whose hitbox is wider than the
    // column's lateral half-voxel margin can wind up clipped into the
    // wall. Forcing all 4 lateral neighbors air gives mobs a corridor
    // they can settle into. Water-bound mobs are exempt — their lateral
    // neighbors are water, not air, so this check would always reject them.
    public override bool RequireLateralClearance => species?.mob?.CanTraverseLand != false;

    public override bool IsMobEntry => true;

    public override StringName VariantProperty => PropertyName.species;

    // Which species of its family this one is, so an entry covering a whole
    // family still names the individual in the hover readout and the panel title.
    public override string VariantName()
        => species != null ? species.ResourcePath.GetFile().GetBaseName() : null;

    // Constrained to the family, which is what makes the species row safe to
    // show: a goblin entry offers only goblins, so a fork can never become a
    // spider while still being named — and highlighted — as a goblin.
    public override Resource[] ResourceCandidates(StringName property)
    {
        if (property != PropertyName.species || variants == null || variants.Length == 0)
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
        Godot.Collections.Array<BehaviorNode> nodes = species?.mob?.brain?.behaviors;
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
        MobData data = species?.mob;
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
        if (species == null)
        {
            return;
        }
        // Written out rather than through SpawnEntryData.FacingY because the
        // fallback must stay LAZY: a crowd nobody aimed wants a random yaw each,
        // and a call would burn a draw on the aimed ones too and shift every roll
        // behind it.
        float rotationY = context?.FacingY ?? (float)(rng.NextDouble() * Mathf.Pi * 2f);
        // Layer the per-area worldgen level field (and underground bonus) onto this
        // placement's authored floor, then hand the final tier to CreateState so the
        // mob's vitals are scaled to it at construction (before this state is baked
        // into the .hike). The constructor forces non-dangerous mobs to 0, so
        // computing a tier here for prey / villagers is harmless.
        int spawnLevel = context != null
            ? context.MobLevel(position, level)
            : Math.Max(0, level);
        MobSimState state = species.CreateState(position, rotationY, elite,
            level: spawnLevel, levelScalePerLevel: ws.SimData?.levelScalePerLevel ?? 1.5f);
        if (state == null)
        {
            return;
        }
        state.SpawnConditions = context?.SpawnConditions ?? ESpawnConditions.None;
        StringName behavior = InitialBehaviorFor(rng, context);
        if (behavior != null)
        {
            state.InitialBehavior = behavior;
        }
        ws.AddEntity(state);
    }

    // Which behaviour a spawn of this entry starts in, or null for the brain's
    // own. The individual's (this entry's field) wins outright; otherwise the
    // row's population rule rolls its fraction. The fraction has nothing to be a
    // fraction of at an authored position, so there the row's behaviour is
    // taken as named.
    //
    // The draw happens only when a row names a behaviour and the position was
    // not authored — exactly when it did while the rule sat on the entry, so no
    // roll behind it moved.
    protected StringName InitialBehaviorFor(Random rng, SpawnContext context)
    {
        if (initialBehavior is not null && !initialBehavior.IsEmpty)
        {
            return initialBehavior;
        }
        StringName rowBehavior = context?.InitialBehavior;
        if (rowBehavior is null || rowBehavior.IsEmpty)
        {
            return null;
        }
        return context.AuthoredPosition || rng.NextDouble() < context.InitialBehaviorChance
            ? rowBehavior
            : null;
    }
}
