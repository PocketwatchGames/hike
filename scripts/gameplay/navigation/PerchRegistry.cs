using System.Collections.Generic;
using Godot;

// Sparse XZ spatial hash over Perch markers, mirroring MobSpatialHash but
// without the per-frame Update — perches are static once placed, so a perch
// only enters on Add (tree-enter) and leaves on Remove (tree-exit / chunk
// evict). The one gameplay query is FindFleePerch: "give me the best free
// perch roughly in this direction" for a fleeing bird picking where to land.
public class PerchRegistry
{
    private const float DefaultCellSize = 8f;

    private readonly float _invCellSize;
    private readonly Dictionary<long, List<Perch>> _cells = new();
    private readonly Dictionary<Perch, long> _perchCells = new();

    public PerchRegistry() : this(DefaultCellSize) { }
    public PerchRegistry(float cellSize)
    {
        _invCellSize = 1f / cellSize;
    }

    public void Add(Perch perch)
    {
        if (perch == null || _perchCells.ContainsKey(perch))
        {
            return;
        }
        long key = KeyOf(perch.WorldPosition);
        if (!_cells.TryGetValue(key, out List<Perch> bucket))
        {
            bucket = new List<Perch>();
            _cells[key] = bucket;
        }
        bucket.Add(perch);
        _perchCells[perch] = key;
    }

    public void Remove(Perch perch)
    {
        if (perch == null || !_perchCells.TryGetValue(perch, out long key))
        {
            return;
        }
        if (_cells.TryGetValue(key, out List<Perch> bucket))
        {
            bucket.Remove(perch);
            if (bucket.Count == 0)
            {
                _cells.Remove(key);
            }
        }
        _perchCells.Remove(perch);
    }

    public void Clear()
    {
        _cells.Clear();
        _perchCells.Clear();
    }

    // Pick the best free perch for a bird fleeing from `from` in direction
    // `fleeDir`. Candidates lie within [minRange, maxRange] of `from` (3D
    // distance) and within the cone whose half-angle is acos(coneDot) around
    // the XZ flee direction. Score favors alignment with the flee direction,
    // lightly preferring nearer perches so the bird doesn't overshoot to a
    // marginally-better-aligned but distant spot. Returns null if nothing
    // qualifies (caller falls back to free flight + ground landing).
    public Perch FindFleePerch(Vector3 from, Vector3 fleeDir, float minRange, float maxRange, float coneDot)
    {
        Vector2 flee2 = new Vector2(fleeDir.X, fleeDir.Z);
        if (flee2.LengthSquared() < 1e-6f)
        {
            return null;
        }
        flee2 = flee2.Normalized();

        float min2 = minRange * minRange;
        float max2 = maxRange * maxRange;
        int minI = Mathf.FloorToInt((from.X - maxRange) * _invCellSize);
        int maxI = Mathf.FloorToInt((from.X + maxRange) * _invCellSize);
        int minJ = Mathf.FloorToInt((from.Z - maxRange) * _invCellSize);
        int maxJ = Mathf.FloorToInt((from.Z + maxRange) * _invCellSize);

        Perch best = null;
        float bestScore = float.NegativeInfinity;
        for (int j = minJ; j <= maxJ; j++)
        {
            for (int i = minI; i <= maxI; i++)
            {
                if (!_cells.TryGetValue(PackKey(i, j), out List<Perch> bucket))
                {
                    continue;
                }
                for (int p = 0; p < bucket.Count; p++)
                {
                    Perch perch = bucket[p];
                    if (perch == null || !perch.IsFree)
                    {
                        continue;
                    }
                    Vector3 to = perch.WorldPosition - from;
                    float dist2 = to.LengthSquared();
                    if (dist2 < min2 || dist2 > max2)
                    {
                        continue;
                    }
                    Vector2 toXZ = new Vector2(to.X, to.Z);
                    float lenXZ = toXZ.Length();
                    if (lenXZ < 1e-4f)
                    {
                        continue;
                    }
                    float align = toXZ.Dot(flee2) / lenXZ;
                    if (align < coneDot)
                    {
                        continue;
                    }
                    // Alignment dominates; subtract a gentle normalized
                    // distance penalty so ties break toward the nearer perch.
                    float score = align - 0.25f * (lenXZ / maxRange);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = perch;
                    }
                }
            }
        }
        return best;
    }

    private long KeyOf(Vector3 worldPos)
    {
        int i = Mathf.FloorToInt(worldPos.X * _invCellSize);
        int j = Mathf.FloorToInt(worldPos.Z * _invCellSize);
        return PackKey(i, j);
    }

    private static long PackKey(int i, int j)
    {
        return ((long)(uint)i << 32) | (uint)j;
    }
}
