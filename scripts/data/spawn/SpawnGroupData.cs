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
//   - For each instance, roll Chance (default 1.0 = always spawn).
//   - For each surviving instance, ask the SpawnContext to pick a position
//     within ScatterRadius of the anchor (rejection-sampled against the
//     pass's IsValidColumn predicate). With no context (e.g. cave-pocket
//     pass) or ScatterRadius == 0, every instance lands at the anchor.
//
// Empty groups (Entries==null/empty) are no-ops. The group's own Chance
// (inherited from SpawnEntryData) gates whether the group fires at all
// when invoked from the per-zone scan loop.
[GlobalClass]
public partial class SpawnGroupData : SpawnEntryData
{
    [Export] public float ScatterRadius = 3f;
    [Export] public Array<SpawnEntryData> Entries = new();

    private const int ScatterAttemptsPerInstance = 6;

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
                if (rng.NextDouble() >= entry.Chance)
                {
                    continue;
                }
                Vector3 instancePos;
                if (context != null && ScatterRadius > 0f)
                {
                    if (!context.TryPickInRadius(position, ScatterRadius, rng,
                        ScatterAttemptsPerInstance, out instancePos))
                    {
                        continue;
                    }
                }
                else
                {
                    instancePos = position;
                }
                entry.Spawn(ws, instancePos, rng, context);
            }
        }
    }
}
