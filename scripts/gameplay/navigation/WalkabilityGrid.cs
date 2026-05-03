using Godot;

// A local 2.5D walkability sample, built on demand around a query origin.
// Each (x,z) cell is sampled by scanning the column for the nearest standable
// surface within a vertical search window; the result encodes the surface Y,
// a flag set, and a per-cell traversal cost given a TraversalProfile.
//
// This is the shared substrate the A* pathfinder, flow field, brownian
// wander, and goal pickers all consult. It does NOT cache between calls in
// phase 1 — every query rebuilds the requested window. A persistent cache
// per chunk is straightforward to add later but isn't free in a streaming
// world (chunk eviction has to invalidate cells), so we defer that until
// profiling demands it.
//
// Coordinate convention: cell (i,j) covers world XZ in
//   [originX + i, originX + i + 1) x [originZ + j, originZ + j + 1)
// where (originX, originZ) is the integer floor of the requested origin
// minus half the grid extent. SurfaceY is the Y of the air voxel a mob
// would stand IN (its feet on the solid below).
public struct WalkabilityCell
{
    public short surfaceY;     // Y of the air voxel mob stands in; valid only if Walkable
    public CellFlags flags;
    public float cost;         // movement-cost multiplier; 1 = neutral, >1 penalized

    public bool Walkable => (flags & CellFlags.Walkable) != 0;
    public bool IsWater => (flags & CellFlags.Water) != 0;
    public bool OutOfBounds => (flags & CellFlags.OutOfBounds) != 0;
}

[System.Flags]
public enum CellFlags : byte
{
    None = 0,
    Walkable = 1 << 0,    // Mob can stand here given its profile
    Water = 1 << 1,       // The standable surface is a water column (wading/swimming)
    OutOfBounds = 1 << 2, // Column is in an unloaded chunk; pathfinder must not cross
}

// Per-mob movement traits, derived from MobData. Held as a struct so it can
// be passed by value into the pathfinder without per-call allocation.
public readonly struct TraversalProfile : System.IEquatable<TraversalProfile>
{
    public readonly int maxStepHeight;
    public readonly int maxFallHeight;
    public readonly bool canClimb;
    public readonly bool canSwim;
    public readonly float waterCost;
    public readonly bool canFly;
    public readonly float clearanceRadius;
    // Vertical clearance the mob needs above the surface to fit, in voxels.
    // Hardcoded to 2 for now (most mobs are ≤2 voxels tall); breaking this
    // out lets a future tall mob declare 3 without changing pathfinder code.
    public readonly int verticalClearance;

    public TraversalProfile(MobData data)
    {
        maxStepHeight = data?.maxStepHeight ?? 1;
        maxFallHeight = data?.maxFallHeight ?? 4;
        canClimb = data?.canClimb ?? false;
        canSwim = data?.canSwim ?? true;
        waterCost = data?.waterCost ?? 5f;
        canFly = data?.canFly ?? false;
        clearanceRadius = data?.clearanceRadius ?? 0.4f;
        verticalClearance = 2;
    }

    // IEquatable so SharedWalkabilityCache can key on the profile without
    // boxing through default object.Equals. Most mobs share a small handful
    // of profiles, so structural equality means a swarm of the same species
    // collapses onto a single cache entry per quantized origin.
    public bool Equals(TraversalProfile o)
    {
        return maxStepHeight == o.maxStepHeight
            && maxFallHeight == o.maxFallHeight
            && canClimb == o.canClimb
            && canSwim == o.canSwim
            && waterCost == o.waterCost
            && canFly == o.canFly
            && clearanceRadius == o.clearanceRadius
            && verticalClearance == o.verticalClearance;
    }
    public override bool Equals(object obj) => obj is TraversalProfile o && Equals(o);
    public override int GetHashCode()
    {
        return System.HashCode.Combine(
            maxStepHeight, maxFallHeight, canClimb, canSwim,
            waterCost, canFly, clearanceRadius, verticalClearance);
    }
}

// On-demand sampler. Allocate once per nav query, fill the window via
// Sample(), then read cells through Get(i,j) or GetWorld(wx,wz). Reuse the
// same instance across calls to amortize the array allocation.
public class WalkabilityGrid
{
    // Vertical scan window above/below the requested origin Y when looking
    // for a standable surface. Sized to cover a reasonable cliff drop
    // (chase-down-a-cliff scenarios) plus a couple voxels of slack so the
    // pathfinder sees the surface and can decide per-call whether the mob
    // is allowed to take that drop. Cells beyond this window read as
    // unwalkable — set this to the largest fall the gameplay should ever
    // path through, not a per-mob cap.
    public const int SurfaceSearchRadius = 12;

    private WalkabilityCell[] _cells;
    private int _size;
    private int _originX;
    private int _originZ;
    private int _originY;

    public int Size => _size;
    public int OriginX => _originX;
    public int OriginZ => _originZ;

    // Build a sizeXsize grid centered on (worldX, worldZ) at the surface
    // height search anchor of worldY, sampling against the given world state
    // and traversal profile. size must be odd so the center cell is the
    // origin column. Pass `world` to also reject cells occupied by
    // path-blocking entities (trees, chests); pass null to skip that check
    // for callers that only care about the voxel grid.
    public void Sample(WorldState ws, World world, in TraversalProfile profile, int worldX, int worldY, int worldZ, int size)
    {
        using var _profSample = Profiler.Sample("WalkabilityGrid.Sample");
        if ((size & 1) == 0)
        {
            size += 1;
        }
        int half = size / 2;
        _size = size;
        _originX = worldX - half;
        _originZ = worldZ - half;
        _originY = worldY;

        int total = size * size;
        if (_cells == null || _cells.Length < total)
        {
            _cells = new WalkabilityCell[total];
        }

        // Pull the requested 33×33 window out of the shared cache. The
        // cache stores enlarged windows aligned to a coarse quantum so
        // multiple mobs of the same profile within the quantum share a
        // single sample. On a fresh quantum the cache populates itself
        // by calling SampleColumn for every cell of the enlarged window;
        // every later mob in the quantum just memcpy's a sub-window into
        // its own _cells. Net win at swarm density: one full sample per
        // quantum per profile per chunk eviction, instead of one per mob
        // per repath.
        SharedWalkabilityCache.Entry entry = SharedWalkabilityCache.GetOrSample(
            ws, world, profile, worldX, worldY, worldZ, half);
        int offsetI = _originX - entry.OriginX;
        int offsetJ = _originZ - entry.OriginZ;
        int srcSize = entry.Size;
        for (int j = 0; j < size; j++)
        {
            int srcRow = (offsetJ + j) * srcSize + offsetI;
            int dstRow = j * size;
            System.Array.Copy(entry.Cells, srcRow, _cells, dstRow, size);
        }
    }

    public bool TryGetWorld(int wx, int wz, out WalkabilityCell cell)
    {
        int i = wx - _originX;
        int j = wz - _originZ;
        if (i < 0 || i >= _size || j < 0 || j >= _size)
        {
            cell = default;
            return false;
        }
        cell = _cells[j * _size + i];
        return true;
    }

    public WalkabilityCell Get(int i, int j)
    {
        return _cells[j * _size + i];
    }

    // Convert a cell index back to a world-space point at the cell's surface,
    // centered horizontally in the cell.
    public Vector3 CellToWorld(int i, int j)
    {
        WalkabilityCell c = _cells[j * _size + i];
        return new Vector3(_originX + i + 0.5f, c.surfaceY, _originZ + j + 0.5f);
    }

    // Walks a column at (wx, wz) looking for the highest standable air voxel
    // within SurfaceSearchRadius of anchorY. Returns a fully-populated cell:
    // OutOfBounds if any sampled column voxel is outside the loaded window,
    // Walkable+surfaceY if a surface is found, default (unwalkable) otherwise.
    internal static WalkabilityCell SampleColumn(WorldState ws, World world, in TraversalProfile profile, int wx, int anchorY, int wz)
    {
        using var _profCol = Profiler.Sample("WalkabilityGrid.SampleColumn");
        WalkabilityCell cell = default;

        // Search top-down so the highest surface within the window wins —
        // a mob already on top of a wall pathfinds along the wall instead
        // of dropping into the trench beside it.
        for (int dy = SurfaceSearchRadius; dy >= -SurfaceSearchRadius; dy--)
        {
            int wy = anchorY + dy;
            if (!ws.IsInBounds(wx, wy, wz))
            {
                // Treat any unloaded column as a hard "do not enter" — the
                // pathfinder must never route a mob into space we don't know.
                cell.flags = CellFlags.OutOfBounds;
                return cell;
            }
            VoxelType here = ws.GetVoxelWorld(wx, wy, wz);
            if (VoxelTypeInfo.IsSolid(here))
            {
                continue;
            }

            // We're in air or water. Need solid (or floor of bounds) below.
            if (!ws.IsInBounds(wx, wy - 1, wz))
            {
                cell.flags = CellFlags.OutOfBounds;
                return cell;
            }
            VoxelType below = ws.GetVoxelWorld(wx, wy - 1, wz);

            bool standsOnSolid = VoxelTypeInfo.IsSolid(below);
            bool inWater = here == VoxelType.Water;

            // Water surface: the cell is "walkable" only if the mob can swim.
            // Cost is multiplied so non-amphibious mobs detour.
            if (inWater)
            {
                if (!profile.canSwim)
                {
                    return cell;
                }
                cell.surfaceY = (short)wy;
                cell.flags = CellFlags.Walkable | CellFlags.Water;
                cell.cost = profile.waterCost;
                return cell;
            }

            if (!standsOnSolid)
            {
                continue;
            }

            // Verify vertical clearance above the surface — a mob can't stand
            // in a 1-voxel slot if it needs 2 voxels of headroom.
            for (int h = 1; h < profile.verticalClearance; h++)
            {
                if (!ws.IsInBounds(wx, wy + h, wz))
                {
                    cell.flags = CellFlags.OutOfBounds;
                    return cell;
                }
                if (VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(wx, wy + h, wz)))
                {
                    // Headroom blocked. Keep scanning lower — there might be
                    // a deeper surface that does have clearance.
                    standsOnSolid = false;
                    break;
                }
            }
            if (!standsOnSolid)
            {
                continue;
            }

            // Reject this surface if a path-blocking entity (e.g. tree, chest)
            // occupies any cell the mob would stand in. Same continue-and-keep-
            // scanning behavior as a headroom-blocked column so the mob can
            // still find a deeper standable surface beneath an overhead prop.
            if (world != null)
            {
                bool entityBlocked = false;
                for (int h = 0; h < profile.verticalClearance; h++)
                {
                    if (world.IsPathBlocked(wx, wy + h, wz))
                    {
                        entityBlocked = true;
                        break;
                    }
                }
                if (entityBlocked)
                {
                    continue;
                }
            }

            cell.surfaceY = (short)wy;
            cell.flags = CellFlags.Walkable;
            cell.cost = 1f;
            return cell;
        }

        return cell;
    }
}

// Process-wide cache of walkability samples shared across all mobs.
//
// Why: WalkabilityGrid.Sample on a 33×33 window costs ~3 µs/cell × 1089 cells
// ≈ 3.3 ms per mob per repath. At swarm density (40+ mobs repathing every
// 0.4s) this dominates _PhysicsProcess. But the result is a deterministic
// function of (ws, world, profile, anchor) — two mobs of the same species
// in the same area compute the same cells. This cache exploits that.
//
// How: cache key quantizes the requested center to a coarse quantum
// (Quantum voxels per axis). The cached entry is sized to cover the
// quantum extent + the mob's halfExtent, so any mob whose anchor lands
// inside the quantum can read its full window from a single shared
// sample. On a hit, a mob's per-instance Sample() degenerates to a
// strip-by-strip Array.Copy of ~33 cells per row × 33 rows — vastly
// cheaper than re-running SampleColumn 1089 times.
//
// Ownership: entries live forever unless evicted by TTL. Eviction is
// keyed on last-use time, swept opportunistically on insert. World is
// hand-authored and static — no voxel-mutation invalidation hook needed
// today; if mining lands, ChunkManager would need to call
// InvalidateChunk(coord) on a mutation.
public static class SharedWalkabilityCache
{
    // Voxels per axis per cache quantum. Larger = more sharing but each
    // cache entry costs more to populate (Quantum² extra cells per axis
    // beyond the bare halfExtent window). 4 picks a balance: any two mobs
    // within 4 voxels XYZ of each other share, and the enlargement is
    // modest (37×37 vs 33×33).
    private const int Quantum = 4;

    // How long an unused entry survives. Refreshed on every hit, so a
    // chunk staying populated keeps its samples alive indefinitely; once
    // the swarm migrates the entries drain naturally without invalidation.
    private const long TtlMs = 10_000;

    // Hard cap to prevent unbounded growth as the player roams. Chosen
    // generously — a 41-cell entry of WalkabilityCell (~16 bytes) is
    // ~27 KB; 2048 entries = ~55 MB worst case. Eviction sweeps the
    // oldest 25% when this trips so we don't thrash on the cap.
    private const int MaxEntries = 2048;

    public sealed class Entry
    {
        public WalkabilityCell[] Cells;
        public int OriginX;
        public int OriginY;
        public int OriginZ;
        public int Size;
        public ulong LastUsedMs;
    }

    private struct Key : System.IEquatable<Key>
    {
        public int Qx;
        public int Qy;
        public int Qz;
        public TraversalProfile Profile;
        public bool Equals(Key o) => Qx == o.Qx && Qy == o.Qy && Qz == o.Qz && Profile.Equals(o.Profile);
        public override bool Equals(object obj) => obj is Key k && Equals(k);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = Qx * 73856093;
                h ^= Qy * 19349663;
                h ^= Qz * 83492791;
                h ^= Profile.GetHashCode();
                return h;
            }
        }
    }

    private static readonly System.Collections.Generic.Dictionary<Key, Entry> _cache = new();
    private static int _hitsThisWindow;
    private static int _missesThisWindow;
    private static int _hitsLatched;
    private static int _missesLatched;
    public static int HitsLatched => _hitsLatched;
    public static int MissesLatched => _missesLatched;
    public static int EntryCount => _cache.Count;
    public static void LatchCounters()
    {
        _hitsLatched = _hitsThisWindow;
        _missesLatched = _missesThisWindow;
        _hitsThisWindow = 0;
        _missesThisWindow = 0;
    }

    public static Entry GetOrSample(WorldState ws, World world, in TraversalProfile profile,
        int centerX, int centerY, int centerZ, int requestedHalfExtent)
    {
        // Quantize using arithmetic shift (handles negative coords correctly).
        // C#'s >> on int is arithmetic, so (-1) >> 2 = -1 not 0x3FFF_FFFF —
        // good for world coords that may go negative.
        int qx = centerX >> 2;
        int qy = centerY >> 2;
        int qz = centerZ >> 2;
        var key = new Key { Qx = qx, Qy = qy, Qz = qz, Profile = profile };
        ulong now = Godot.Time.GetTicksMsec();
        if (_cache.TryGetValue(key, out Entry hit))
        {
            hit.LastUsedMs = now;
            _hitsThisWindow++;
            return hit;
        }
        _missesThisWindow++;

        // Compute the enlarged window that satisfies any mob within the
        // quantum. The quantum's XZ extent is [qx*Quantum .. qx*Quantum+Quantum),
        // so the worst-case mob position is one of the corners; we need
        // the mob's full halfExtent visible from there.
        int qOriginXz = qx * Quantum;
        int qOriginY = qy * Quantum;
        int qOriginZ = qz * Quantum;
        int cacheHalfExtent = requestedHalfExtent + Quantum - 1;
        int cacheSize = cacheHalfExtent * 2 + 1;
        Entry entry = new Entry
        {
            Cells = new WalkabilityCell[cacheSize * cacheSize],
            // Center the cache on the quantum center so the worst-case
            // mob (at the quantum's far corner) still has halfExtent on
            // every side.
            OriginX = qOriginXz + Quantum / 2 - cacheHalfExtent,
            OriginY = qOriginY + Quantum / 2,
            OriginZ = qOriginZ + Quantum / 2 - cacheHalfExtent,
            Size = cacheSize,
            LastUsedMs = now,
        };
        // Anchor Y for the column scan: the quantum's center Y. Mobs
        // within the quantum can be up to Quantum/2 voxels from this
        // anchor, so the scan's ±SurfaceSearchRadius window still
        // comfortably covers them.
        int anchorY = entry.OriginY;
        for (int j = 0; j < cacheSize; j++)
        {
            for (int i = 0; i < cacheSize; i++)
            {
                int wx = entry.OriginX + i;
                int wz = entry.OriginZ + j;
                entry.Cells[j * cacheSize + i] = WalkabilityGrid.SampleColumn(ws, world, profile, wx, anchorY, wz);
            }
        }
        _cache[key] = entry;
        if (_cache.Count > MaxEntries)
        {
            EvictOldest(now);
        }
        return entry;
    }

    // Drop the oldest 25% of entries. Cheaper than per-insert TTL sweep
    // and only fires when the cap trips — at steady state the cache
    // tends to either stay under the cap or churn at the eviction
    // boundary, which is fine.
    private static void EvictOldest(ulong now)
    {
        int target = MaxEntries * 3 / 4;
        var entries = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Key, Entry>>(_cache);
        entries.Sort((a, b) => a.Value.LastUsedMs.CompareTo(b.Value.LastUsedMs));
        int toRemove = _cache.Count - target;
        for (int i = 0; i < toRemove; i++)
        {
            _cache.Remove(entries[i].Key);
        }
    }

    // Drops every entry whose enlarged XZ window touches the chunk at
    // chunkCoord (chunk coordinates, not voxel coordinates). Hooked up
    // when voxel mutation actually lands. Today, no caller invokes this.
    public static void InvalidateChunk(Godot.Vector3I chunkCoord)
    {
        const int ChunkSize = 16;
        int chunkMinX = chunkCoord.X * ChunkSize;
        int chunkMinZ = chunkCoord.Z * ChunkSize;
        int chunkMaxX = chunkMinX + ChunkSize - 1;
        int chunkMaxZ = chunkMinZ + ChunkSize - 1;
        var toRemove = new System.Collections.Generic.List<Key>();
        foreach (var kv in _cache)
        {
            Entry e = kv.Value;
            int eMaxX = e.OriginX + e.Size - 1;
            int eMaxZ = e.OriginZ + e.Size - 1;
            if (eMaxX < chunkMinX || e.OriginX > chunkMaxX) continue;
            if (eMaxZ < chunkMinZ || e.OriginZ > chunkMaxZ) continue;
            toRemove.Add(kv.Key);
        }
        for (int i = 0; i < toRemove.Count; i++)
        {
            _cache.Remove(toRemove[i]);
        }
    }

    // Drop entries that haven't been touched in a while. Called from
    // Profiler.Tick on each window latch — opportunistic, no fixed
    // cadence, but catches drift as the player explores.
    public static void SweepStale()
    {
        ulong now = Godot.Time.GetTicksMsec();
        var toRemove = new System.Collections.Generic.List<Key>();
        foreach (var kv in _cache)
        {
            if (now - kv.Value.LastUsedMs > (ulong)TtlMs)
            {
                toRemove.Add(kv.Key);
            }
        }
        for (int i = 0; i < toRemove.Count; i++)
        {
            _cache.Remove(toRemove[i]);
        }
    }
}
