using System;
using Godot;
using Godot.Collections;

// Composite spawn entry: a cluster of heterogeneous sub-entries (mobs,
// chests, traps, loot, even nested groups) scattered around a single
// anchor point. Subclasses SpawnEntryData so a group can sit inline in
// any SpawnListData / SpawnGroupData entries array — authors mix leaf
// entries and groups without a separate "group entry" wrapper.
//
// Spawn semantics:
//   - For each sub-entry, call RollCount(rng) for the instance count
//     (default 1; subclasses like MobSpawnEntry override with their own
//     ClusterCountMin/Max).
//   - For each instance, ask the SpawnContext to pick a position within
//     ScatterRadius of the anchor (rejection-sampled against the pass's
//     IsValidColumn predicate). With no context (e.g. cave-pocket pass)
//     or ScatterRadius == 0, every instance lands at the anchor.
//
// Empty groups (Entries==null/empty) are no-ops. The group's own
// SquareMetersPerSpawn (inherited from SpawnEntryData) gates whether the
// group fires at all when invoked from the per-zone scan loop.
[GlobalClass]
public partial class SpawnGroupData : SpawnEntryData
{
    [Export] public float ScatterRadius = 3f;
    [Export] public Array<SpawnEntryData> Entries = new();

    private const int ScatterAttemptsPerInstance = 6;

    // The group's own anchor is just a scatter center — nothing sits at
    // exactly the anchor — so the standard overlap rejection radius is
    // meaningless here. Sub-entries enforce their own MinSpacing per pick.
    public SpawnGroupData()
    {
        MinSpacing = 0f;
    }

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (Entries == null)
        {
            return;
        }
        foreach (SpawnEntryData entry in Entries)
        {
            if (entry == null)
            {
                continue;
            }
            int count = entry.RollCount(rng);
            for (int i = 0; i < count; i++)
            {
                if (context != null && ScatterRadius > 0f)
                {
                    // TryPickInRadius runs the entry's flat-terrain + overlap
                    // checks inside its rejection loop, so the surviving pick
                    // is already validated — call Spawn directly to avoid a
                    // redundant second pass.
                    if (!context.TryPickInRadius(entry, ws, position, ScatterRadius, rng,
                        ScatterAttemptsPerInstance, out Vector3 instancePos))
                    {
                        continue;
                    }
                    entry.Spawn(ws, instancePos, rng, context);
                }
                else
                {
                    // No scatter (radius=0 or no context): drop on the anchor,
                    // but still run the entry's gates via TrySpawn.
                    entry.TrySpawn(ws, position, rng, context);
                }
            }
        }
    }
}
