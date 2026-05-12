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

    // Pick a position within `radius` of `anchor` whose column passes
    // IsValidColumn, resolving Y to the ground top (SurfaceYAt + 1 — top
    // face of the surface voxel). Matches the unified anchor convention
    // both WorldGen passes use. Returns false if all attempts were
    // rejected — caller skips the spawn instance.
    public bool TryPickInRadius(Vector3 anchor, float radius, Random rng,
        int attempts, out Vector3 result)
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
            int sy = SurfaceYAt(wx, wz);
            result = new Vector3(wx + 0.5f, sy + 1f, wz + 0.5f);
            return true;
        }
        result = anchor;
        return false;
    }
}
