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

    // Run the entry's placement gates and spawn it, with this row's conditions
    // in force. The conditions ride the context because Spawn is overridden by
    // ~20 entry types and only three of them care — see SpawnContext.
    public bool TrySpawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (entry == null)
        {
            return false;
        }
        Apply(context);
        return entry.TrySpawn(ws, position, rng, context);
    }

    // Spawn without the gates — for callers that have already validated the
    // position (SpawnGroupData's rejection-sampled scatter).
    public void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (entry == null)
        {
            return;
        }
        Apply(context);
        entry.Spawn(ws, position, rng, context);
    }

    // Stamp this row's conditions onto the context the entry will read. Set
    // immediately before each spawn and never cleared: every caller sets it for
    // the row it is about to place, so a stale value cannot outlive its row.
    private void Apply(SpawnContext context)
    {
        if (context != null)
        {
            context.SpawnConditions = spawnConditions;
        }
    }
}
