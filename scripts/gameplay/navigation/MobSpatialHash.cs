using System.Collections.Generic;
using Godot;

// Sparse 2D spatial hash over mobs, indexed in XZ on a fixed cell size.
// Lookup ("give me mobs within R of this point") visits the cells that
// could possibly contain hits and walks their member lists. At swarm
// densities (100+ mobs) this is the difference between O(N) per-mob
// neighbor queries — what World.GetEntities<Mob>() would give us — and
// effectively O(K) where K is the average mob density per cell.
//
// The hash is XZ-only because separation, encircle, and most gameplay
// queries care about horizontal distance. Vertical scoping (don't push
// against a mob 30m below me on a different floor) is filtered by the
// caller after the cell walk; cheap enough since K is small.
//
// Cell size is the single tuning knob. Pick it close to the largest
// "interesting" radius callers will use (separation kernel ≈ clearance
// radius × ~3, encircle slot ring ≈ standoff distance). At cell=4m, a
// query of radius 4m visits up to 9 cells; radius 8m visits up to 25.
// For 100 mobs over a 32m³ play area that's ~12 mobs/cell — plenty fast.
public class MobSpatialHash
{
    private const float DefaultCellSize = 4f;

    private readonly float _cellSize;
    private readonly float _invCellSize;
    private readonly Dictionary<long, List<Mob>> _cells = new();
    // Each registered mob's current cell key, so an update knows the OLD
    // cell to remove from. Faster than searching all buckets on update.
    private readonly Dictionary<Mob, long> _mobCells = new();

    public MobSpatialHash() : this(DefaultCellSize) { }
    public MobSpatialHash(float cellSize)
    {
        _cellSize = cellSize;
        _invCellSize = 1f / cellSize;
    }

    public void Add(Mob mob)
    {
        if (mob == null)
        {
            return;
        }
        long key = KeyOf(mob.GlobalPosition);
        if (!_cells.TryGetValue(key, out List<Mob> bucket))
        {
            bucket = new List<Mob>();
            _cells[key] = bucket;
        }
        bucket.Add(mob);
        _mobCells[mob] = key;
    }

    public void Remove(Mob mob)
    {
        if (mob == null)
        {
            return;
        }
        if (!_mobCells.TryGetValue(mob, out long key))
        {
            return;
        }
        if (_cells.TryGetValue(key, out List<Mob> bucket))
        {
            bucket.Remove(mob);
            if (bucket.Count == 0)
            {
                _cells.Remove(key);
            }
        }
        _mobCells.Remove(mob);
    }

    // Update on movement. Cheap if the mob hasn't crossed a cell boundary;
    // bucket move otherwise. Call once per physics tick from Mob — the
    // caller doesn't need to gate on "did I move" because this method does.
    public void Update(Mob mob)
    {
        if (mob == null)
        {
            return;
        }
        long newKey = KeyOf(mob.GlobalPosition);
        if (!_mobCells.TryGetValue(mob, out long oldKey))
        {
            // Not registered yet — treat as Add. Lets Mob skip a separate
            // first-frame Add call.
            Add(mob);
            return;
        }
        if (oldKey == newKey)
        {
            return;
        }
        if (_cells.TryGetValue(oldKey, out List<Mob> oldBucket))
        {
            oldBucket.Remove(mob);
            if (oldBucket.Count == 0)
            {
                _cells.Remove(oldKey);
            }
        }
        if (!_cells.TryGetValue(newKey, out List<Mob> newBucket))
        {
            newBucket = new List<Mob>();
            _cells[newKey] = newBucket;
        }
        newBucket.Add(mob);
        _mobCells[mob] = newKey;
    }

    // Walk all mobs whose XZ position is within `radius` of `center`,
    // appending into `result`. Caller owns the list (we don't allocate).
    // Excludes `exclude` if non-null — convenient for "neighbors of me".
    public void QueryRadius(Vector3 center, float radius, List<Mob> result, Mob exclude = null)
    {
        if (result == null || radius <= 0f)
        {
            return;
        }
        float r2 = radius * radius;
        int minI = Mathf.FloorToInt((center.X - radius) * _invCellSize);
        int maxI = Mathf.FloorToInt((center.X + radius) * _invCellSize);
        int minJ = Mathf.FloorToInt((center.Z - radius) * _invCellSize);
        int maxJ = Mathf.FloorToInt((center.Z + radius) * _invCellSize);

        for (int j = minJ; j <= maxJ; j++)
        {
            for (int i = minI; i <= maxI; i++)
            {
                long key = PackKey(i, j);
                if (!_cells.TryGetValue(key, out List<Mob> bucket))
                {
                    continue;
                }
                for (int m = 0; m < bucket.Count; m++)
                {
                    Mob mob = bucket[m];
                    if (mob == exclude || mob == null)
                    {
                        continue;
                    }
                    Vector3 p = mob.GlobalPosition;
                    float dx = p.X - center.X;
                    float dz = p.Z - center.Z;
                    if (dx * dx + dz * dz > r2)
                    {
                        continue;
                    }
                    result.Add(mob);
                }
            }
        }
    }

    public void Clear()
    {
        _cells.Clear();
        _mobCells.Clear();
    }

    private long KeyOf(Vector3 worldPos)
    {
        int i = Mathf.FloorToInt(worldPos.X * _invCellSize);
        int j = Mathf.FloorToInt(worldPos.Z * _invCellSize);
        return PackKey(i, j);
    }

    // Pack (i, j) into a 64-bit key. Each int gets the low 32 bits; a
    // hash key collision would require a coordinate of magnitude > 2^31
    // voxels which we'll never hit even at the streaming target world
    // size.
    private static long PackKey(int i, int j)
    {
        return ((long)(uint)i << 32) | (uint)j;
    }
}
