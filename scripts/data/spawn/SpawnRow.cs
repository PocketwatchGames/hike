using System;
using Godot;

// One row of a SpawnListData or a SpawnGroupData: a shared SpawnEntryData —
// "a goblin", "a well" — plus how THIS container uses it.
//
// The split is what lets a list read as a list of named things. What a goblin
// is, and how one lands on a column, is the same everywhere it appears and
// lives once in the entry's own .tres; whether it only comes out at night here,
// how densely a zone sprinkles it, and how many of it a camp holds are
// statements the CONTAINER makes and differ from one to the next. Authored the
// other way round (everything on the entry) each list had to embed its own copy
// of every entry, and the same well was re-authored in three files.
//
// What lives here is what BOTH containers ask. The two questions they do not
// share are asked by the subclasses — a list wants a rate per area, a group
// wants a count and a position within the cluster — because a field that cannot
// affect its container is worse than a missing one: it invites tuning that does
// nothing.
[GlobalClass]
public partial class SpawnRow : Resource
{
    // The thing this row places. Shared: several containers point at one file,
    // and editing it retunes every one that names it — which is the point.
    [Export] public SpawnEntryData entry;

    // Required circumstances for this row's entity to materialize. Honoured by
    // the mob and chest sim states, which defer the node spawn until their
    // chunk activates while the conditions hold; the entity then persists
    // across changing conditions (a one-way spawn gate, not a presence gate).
    // Other entity types ignore it. None = unconditional.
    //
    // Per ROW, because the same creature answers differently per container: the
    // mountain goblin is night-only on the surface and any-time in a cave, and
    // it is one goblin either way.
    [Export, CompactFlags] public ESpawnConditions spawnConditions;

    // The behaviour this row's mobs start in instead of their brain's idle, and
    // the FRACTION of them that do ("a quarter of these goblins start in
    // Wander"). A population rule, so it is the container's to state: a goblin
    // wandering the surface and the same goblin keeping to its camp are one
    // goblin. On the entry it cost a second file per creature that should stay
    // put. Read by the mob and npc entries; empty = the brain's default.
    [Export] public StringName initialBehavior;
    [Export(PropertyHint.Range, "0,1,0.01")] public float initialBehaviorChance = 1f;

    // Run the entry's placement gates and spawn it, with this row's statements
    // in force. They ride the context because Spawn is overridden by ~20 entry
    // types and only the mob, npc and chest ones care — see SpawnContext.
    public bool TrySpawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (entry == null)
        {
            return false;
        }
        Stamp previous = Apply(context);
        try
        {
            return entry.TrySpawn(ws, position, rng, context);
        }
        finally
        {
            previous.Restore(context);
        }
    }

    // Spawn without the gates — for callers that have already validated the
    // position (SpawnGroupData's rejection-sampled scatter).
    public void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (entry == null)
        {
            return;
        }
        Stamp previous = Apply(context);
        try
        {
            entry.Spawn(ws, position, rng, context);
        }
        finally
        {
            previous.Restore(context);
        }
    }

    // What the context said before this row stamped it, put back once the row's
    // spawn is done. Scoped rather than left behind because a context outlives
    // its rows: the painter's bake places its hand-placed entities on the same
    // context the scatter pass used, and they inherited whichever row happened to
    // run last — its day/night gate, and its behaviour, which an authored
    // position takes unconditionally. Nesting falls out: a group's rows stamp
    // over the list row that named the group and restore it on the way out.
    private readonly struct Stamp
    {
        private readonly ESpawnConditions _conditions;
        private readonly StringName _behavior;
        private readonly float _chance;

        public Stamp(SpawnContext context)
        {
            _conditions = context.SpawnConditions;
            _behavior = context.InitialBehavior;
            _chance = context.InitialBehaviorChance;
        }

        public void Restore(SpawnContext context)
        {
            if (context == null)
            {
                return;
            }
            context.SpawnConditions = _conditions;
            context.InitialBehavior = _behavior;
            context.InitialBehaviorChance = _chance;
        }
    }

    private Stamp Apply(SpawnContext context)
    {
        if (context == null)
        {
            return default;
        }
        var previous = new Stamp(context);
        context.SpawnConditions = spawnConditions;
        context.InitialBehavior = initialBehavior;
        context.InitialBehaviorChance = initialBehaviorChance;
        return previous;
    }
}
