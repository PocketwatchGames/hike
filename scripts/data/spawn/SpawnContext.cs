using System;
using Godot;

// Surface-resolution helpers passed through SpawnEntryData.Spawn so a
// SpawnGroupData (or any future composite entry) can scatter sub-entries
// without baking WorldGen-internal closures into its own implementation.
//
// Each WorldGen pass that calls into a SpawnListData supplies the context
// matching its placement domain:
//   Surface pass → SurfaceYAt + IsValidColumn=IsGrassyAt (rejects non-grass).
//   Cave-pocket  → context is null. SpawnGroupData inside a cave list
//                  falls through to anchor-only placement (no scatter
//                  sampler available; cave-pocket cells are pre-validated
//                  by the loop, so there's no rejection to do anyway).
//   Future shore/water passes → their own samplers.
//
// Leaf entries (MobSpawnEntry, LootSpawnEntry, ...) accept and ignore the
// context — they spawn at the position they're given. Only composites
// that need to derive new positions read it.
public sealed class SpawnContext
{
    // Resolves the world Y of the topmost solid voxel under (wx, wz).
    public Func<int, int, int> SurfaceYAt;

    // Returns true if the column at (wx, wz) is a valid placement for the
    // current pass (grassy surface for the surface pass; future passes may
    // use shore-band or seabed predicates).
    public Func<int, int, bool> IsValidColumn;

    // Returns true if the column at (wx, wz) sits on a flat patch — the
    // column AND all 8 surrounding columns share the same surface height.
    // Surface pass populates this from the heightmap; cave-pocket pass
    // leaves it null (no slope concept inside caves). Consulted only when
    // an entry sets RequireFlatTerrain.
    public Func<int, int, bool> IsFlatColumn;

    // Per-chest zone loot for the zone this pass is placing into — appended to
    // any chest spawned this pass (cave-pocket or camp-group), on top of the
    // chest entry's own lootItems, rolled independently at each chest. Set per
    // column/cell from the resolved ZoneGenData.perChestLoot; every entry type
    // except ChestSpawnEntry ignores it. Null/empty = no per-chest zone drops.
    // (ZoneGenData.distributedLoot is handled separately in a post-gen pass, not
    // through this context.)
    public ItemCountRange[] ZonePerChestLoot;

    // True when the position was hand-authored (a subscene marker) rather than
    // sampled off a column. It turns OFF the placement heuristics that exist to
    // judge whether an AUTO-PICKED spot is sensible — chiefly the 4-neighbour
    // lateral air test, which rejects anything within a voxel of a wall and so
    // rejects most of any room worth standing an NPC in. The gates that answer
    // "can this body physically stand here" (navgrid walkability, entity
    // overlap) still run: the author picks the spot, the world still gets to
    // say whether a body fits in it.
    public bool AuthoredPosition;

    // Authored facing (radians about +Y) for the position being spawned at,
    // when the caller has one: a subscene marker carries the rotation it was
    // turned to in the editor, so an NPC stood on it faces the way the author
    // pointed. Null — every scan pass — leaves each entry rolling its own.
    // Honored by the mob entries; other types face as they always did.
    public float? FacingY;

    // Pick a position within `radius` of `anchor` that satisfies all the
    // entry's placement gates: column validity (IsValidColumn), flat
    // terrain if required (IsFlatColumn + entry.RequireFlatTerrain), and
    // no existing entity within entry.MinSpacing. Y resolves to the
    // ground top (SurfaceYAt + 1 — top face of the surface voxel). Returns
    // false if every attempt was rejected; caller skips the instance.
    public bool TryPickInRadius(SpawnEntryData entry, WorldState ws, Vector3 anchor,
        float radius, Random rng, int attempts, out Vector3 result)
    {
        if (radius <= 0f || SurfaceYAt == null || IsValidColumn == null)
        {
            result = anchor;
            return true;
        }
        for (int i = 0; i < attempts; i++)
        {
            float r = radius * Mathf.Sqrt((float)rng.NextDouble());
            float a = (float)(rng.NextDouble() * Mathf.Pi * 2.0);
            int wx = Mathf.FloorToInt(anchor.X + r * Mathf.Cos(a));
            int wz = Mathf.FloorToInt(anchor.Z + r * Mathf.Sin(a));
            if (!IsValidColumn(wx, wz))
            {
                continue;
            }
            if (entry.RequireFlatTerrain && IsFlatColumn != null && !IsFlatColumn(wx, wz))
            {
                continue;
            }
            int sy = SurfaceYAt(wx, wz);
            var candidate = new Vector3(wx + 0.5f, sy + 1f, wz + 0.5f);
            if (entry.minSpacing > 0f && ws.HasEntityWithinRadius(candidate, entry.minSpacing))
            {
                continue;
            }
            // Same symmetric hazard keep-out as SpawnEntryData.TrySpawn, so a
            // camp's scattered mobs don't land on its campfire (and a scattered
            // hazard sub-entry doesn't land on a mob already placed).
            if (entry.IsMobEntry && ws.HasHazardSpawnConflict(candidate))
            {
                continue;
            }
            if (entry.HazardSpawnRadius > 0f && ws.HasMobWithinRadius(candidate, entry.HazardSpawnRadius))
            {
                continue;
            }
            if (!entry.IsSpawnPositionWalkable(ws, candidate))
            {
                continue;
            }
            result = candidate;
            return true;
        }
        result = anchor;
        return false;
    }
}
