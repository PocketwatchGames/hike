using System;
using Godot;
using Godot.Collections;

// Composite spawn entry: a cluster of heterogeneous members (mobs, chests,
// traps, loot, even nested groups) scattered around a single anchor point.
// Subclasses SpawnEntryData so a group can sit inline in any SpawnListData /
// SpawnGroupData row — authors mix leaf entries and groups without a separate
// "group entry" wrapper.
//
// Spawn semantics:
//   - For each member row, RollCount(rng) gives the instance count (its
//     countMin..countMax).
//   - For each instance, ask the SpawnContext to pick a position within
//     scatterRadius of the anchor (rejection-sampled against the pass's
//     IsValidColumn predicate). With no context or scatterRadius == 0, every
//     instance lands at the anchor.
//
// Members are SpawnGroupRows for the same reason a list's are rows: a camp's goblin is the
// same goblin the surface scan places, and "two or three of them, at night,
// here" is what the CAMP says about it. The group's own row gates whether it
// fires at all from a per-zone scan.
[GlobalClass]
public partial class SpawnGroupData : SpawnEntryData
{
    [Export] public float scatterRadius = 3f;
    [Export] public Array<SpawnGroupRow> rows = new();

    private const int ScatterAttemptsPerInstance = 6;

    // The group's own anchor is just a scatter center — nothing sits at
    // exactly the anchor — so the standard overlap rejection radius is
    // meaningless here. Sub-entries enforce their own MinSpacing per pick.
    public SpawnGroupData()
    {
        minSpacing = 0f;
    }

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (rows == null)
        {
            return;
        }
        foreach (SpawnGroupRow row in rows)
        {
            if (row?.entry == null)
            {
                continue;
            }
            int count = row.RollCount(rng);
            for (int i = 0; i < count; i++)
            {
                if (row.placeAtAnchor)
                {
                    // Centerpiece: pin to the cluster anchor (no scatter), but
                    // still run the entry's gates.
                    row.TrySpawn(ws, position, rng, context);
                }
                else if (row.entry.SelfPlaces)
                {
                    // The entry derives its own position from the anchor (e.g.
                    // a boat ring-scanning for water) — the grassy scatter
                    // sampler would wrongly reject it, so bypass it.
                    row.Spawn(ws, position, rng, context);
                }
                else if (context != null && scatterRadius > 0f)
                {
                    // TryPickInRadius runs the entry's flat-terrain + overlap
                    // checks inside its rejection loop, so the surviving pick
                    // is already validated — spawn directly to avoid a
                    // redundant second pass.
                    if (!context.TryPickInRadius(row.entry, ws, position, scatterRadius, rng,
                        ScatterAttemptsPerInstance, out Vector3 instancePos))
                    {
                        continue;
                    }
                    row.Spawn(ws, instancePos, rng, context);
                }
                else
                {
                    // No scatter (radius=0 or no context): drop on the anchor,
                    // but still run the entry's gates via TrySpawn.
                    row.TrySpawn(ws, position, rng, context);
                }
            }
        }
    }
}
