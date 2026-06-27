using Godot;

// A local 2.5D walkability sample, built on demand around a query origin.
// Each (x,z) cell is sampled by scanning the column for the nearest standable
// surface within a vertical search window; the result encodes the surface Y,
// a flag set, and a per-cell traversal cost given a TraversalProfile.
//
// This is the shared substrate the A* pathfinder, flow field, brownian
// wander, and goal pickers all consult. Per-call samples are served from
// the process-wide SharedWalkabilityCache (below).
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
    // The cell sits inside a damaging prop's danger zone (fire trap, campfire,
    // spike trap). Purely informational on the cell — whether it's avoided is
    // a per-query decision the pathfinder makes via its avoidHazards flag, so
    // wander routes around it while a mob chasing the player still walks in.
    public bool IsHazard => (flags & CellFlags.Hazard) != 0;
}

[System.Flags]
public enum CellFlags : byte
{
    None = 0,
    Walkable = 1 << 0,    // Mob can stand here given its profile
    Water = 1 << 1,       // The standable surface is a water column (wading/swimming)
    OutOfBounds = 1 << 2, // Column is in an unloaded chunk; pathfinder must not cross
    Hazard = 1 << 3,      // Inside a damaging prop's danger zone (see World hazard grid)
}

// Per-mob movement traits, derived from MobData. Held as a struct so it can
// be passed by value into the pathfinder without per-call allocation.
public readonly struct TraversalProfile : System.IEquatable<TraversalProfile>
{
    public readonly int maxStepHeight;
    public readonly int maxFallHeight;
    public readonly bool canClimb;
    public readonly bool canSwim;
    // True if the mob can ONLY traverse water — dry land is impassable. Water
    // cells stay walkable (priced via waterCost / swimCost); dry surfaces are
    // never stored, so A* confines the mob to the water body. Mirrors
    // MobData.aquatic.
    public readonly bool aquatic;
    public readonly float waterCost;
    // Higher pathfinder cost charged when the water column is at least
    // swimDepthThreshold voxels deep (the mob would be swimming there
    // rather than wading). Routes prefer wading detours over swim legs.
    public readonly float swimCost;
    // Water column depth (voxels) at which a water cell flips from wade to
    // swim cost. Mirrors MobData.swimDepthThreshold; SampleColumn floors
    // it to an int for the voxel-grid check.
    public readonly float swimDepthThreshold;
    public readonly bool canFly;
    public readonly float clearanceRadius;
    // Vertical clearance the mob needs above the surface to fit, in voxels.
    // From MobData.verticalClearance (default 2); a short mob declares 1 to
    // duck into low slots, a tall one 3+. In the cache key so heights don't
    // share standability samples.
    public readonly int verticalClearance;

    public TraversalProfile(MobData data)
    {
        maxStepHeight = data?.maxStepHeight ?? 1;
        maxFallHeight = data?.maxFallHeight ?? 4;
        canClimb = data?.canClimb ?? false;
        canSwim = data?.canSwim ?? true;
        aquatic = data?.aquatic ?? false;
        waterCost = data?.waterCost ?? 5f;
        swimCost = data?.swimCost ?? 15f;
        swimDepthThreshold = data?.swimDepthThreshold ?? 2f;
        canFly = data?.canFly ?? false;
        clearanceRadius = data?.clearanceRadius ?? 0.4f;
        verticalClearance = data?.verticalClearance ?? 2;
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
            && aquatic == o.aquatic
            && waterCost == o.waterCost
            && swimCost == o.swimCost
            && swimDepthThreshold == o.swimDepthThreshold
            && canFly == o.canFly
            && clearanceRadius == o.clearanceRadius
            && verticalClearance == o.verticalClearance;
    }
    public override bool Equals(object obj) => obj is TraversalProfile o && Equals(o);
    public override int GetHashCode()
    {
        return System.HashCode.Combine(
            System.HashCode.Combine(maxStepHeight, maxFallHeight, canClimb, canSwim, aquatic),
            waterCost, swimCost, swimDepthThreshold,
            canFly, clearanceRadius, verticalClearance);
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

    // Stacked walkable surfaces stored per (x,z) column. A flat-terrain column
    // has one; an overhang/cave column has the floor AND the surface on the
    // roof above it, etc. A* nodes are (i, j, layer) so a mob can path between
    // these — follow the player down into a cave, across a bridge, under an
    // arch. Bounds memory and A* node count; the sampler keeps the top-down
    // highest this many per column (deeper extras dropped). 4 covers
    // outdoor + cave-floor + a sub-level with room to spare in practice.
    public const int MaxColumnLayers = 4;

    // Minimum vertical voxel gap between two stored layers in one column;
    // a candidate surface closer than this to the layer already stored above
    // it is dropped (kept: the higher one). Collapses near-duplicate surfaces
    // and bounds layer count per the "≤1 walkable surface per N-voxel span"
    // budget. Real stand-in caves clear this easily (a 2-tall cave + roof puts
    // its floor ≥3 below the outdoor surface).
    public const int MinLayerSeparation = 2;

    // Soft wall-avoidance band beyond the mob's body radius. A candidate
    // surface whose nearest wall is within (clearanceRadius + WallAvoidMargin)
    // is still walkable but charged up to WallProximityCost extra, so A*
    // prefers cells away from walls — this pulls a route onto a tunnel's
    // centerline instead of scraping the wall (where separation jitter then
    // jams the body into it). A HARD reject only happens when the body disk
    // actually overlaps a wall (mob physically can't stand there). Global
    // feel tuning shared across mobs, like the other consts here; the per-mob
    // body size that drives the hard reject lives on MobData.clearanceRadius.
    public const float WallAvoidMargin = 0.5f;
    public const float WallProximityCost = 4f;

    // Layered storage: column (i,j)'s layer L lives at
    // (j*_size + i)*MaxColumnLayers + L. Walkable layers are packed from L=0
    // (highest surface) downward; unused trailing slots are default (None).
    // An out-of-bounds column tags slot 0 with CellFlags.OutOfBounds.
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

        int total = size * size * MaxColumnLayers;
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
        int rowCells = size * MaxColumnLayers;
        for (int j = 0; j < size; j++)
        {
            int srcRow = ((offsetJ + j) * srcSize + offsetI) * MaxColumnLayers;
            int dstRow = (j * size) * MaxColumnLayers;
            System.Array.Copy(entry.Cells, srcRow, _cells, dstRow, rowCells);
        }
    }

    // Number of walkable surfaces stacked in column (i,j). Layers are packed
    // from 0 (highest), so this is the count of leading Walkable slots.
    public int LayerCount(int i, int j)
    {
        int baseIdx = (j * _size + i) * MaxColumnLayers;
        int count = 0;
        while (count < MaxColumnLayers && (_cells[baseIdx + count].flags & CellFlags.Walkable) != 0)
        {
            count++;
        }
        return count;
    }

    public WalkabilityCell GetLayer(int i, int j, int layer)
    {
        return _cells[(j * _size + i) * MaxColumnLayers + layer];
    }

    // True if the column sits in an unloaded chunk — the pathfinder must not
    // route through it. Distinct from "no walkable layer" (in-bounds but no
    // standable surface in the search window).
    public bool IsColumnOutOfBounds(int i, int j)
    {
        return (_cells[(j * _size + i) * MaxColumnLayers].flags & CellFlags.OutOfBounds) != 0;
    }

    // Index of the walkable layer whose surface Y is nearest worldY, or -1 if
    // the column has no walkable layer. Used to bind a world-space query point
    // (the mob's feet, a goal) to the specific stacked surface it belongs to.
    public int NearestLayer(int i, int j, float worldY)
    {
        int baseIdx = (j * _size + i) * MaxColumnLayers;
        int best = -1;
        float bestDist = float.MaxValue;
        for (int layer = 0; layer < MaxColumnLayers; layer++)
        {
            WalkabilityCell c = _cells[baseIdx + layer];
            if ((c.flags & CellFlags.Walkable) == 0)
            {
                break;
            }
            float d = Mathf.Abs(c.surfaceY - worldY);
            if (d < bestDist)
            {
                bestDist = d;
                best = layer;
            }
        }
        return best;
    }

    // Convert a (cell, layer) back to a world-space point at that layer's
    // surface, centered horizontally in the cell.
    public Vector3 CellToWorld(int i, int j, int layer)
    {
        WalkabilityCell c = _cells[(j * _size + i) * MaxColumnLayers + layer];
        return new Vector3(_originX + i + 0.5f, c.surfaceY, _originZ + j + 0.5f);
    }

    // Walks a column at (wx, wz) and fills up to MaxColumnLayers stacked
    // walkable surfaces into cells[baseIdx .. baseIdx+MaxColumnLayers), packed
    // from slot 0 (highest surface) downward. Surfaces closer than
    // MinLayerSeparation collapse onto the higher one. If the column is in an
    // unloaded chunk with no surface found above the gap, slot 0 is tagged
    // OutOfBounds (hard "do not enter"); a column with no standable surface at
    // all leaves every slot default (unwalkable).
    //
    // Top-down so the highest surface wins slot 0 — a mob on top of a wall
    // resolves to the wall top, not the trench beside it — while lower slots
    // capture the cave floor / underside of an overhang so A* can path onto
    // them.
    internal static void SampleColumn(WorldState ws, World world, in TraversalProfile profile, int wx, int anchorY, int wz, WalkabilityCell[] cells, int baseIdx)
    {
        using var _profCol = Profiler.Sample("WalkabilityGrid.SampleColumn");

        for (int layer = 0; layer < MaxColumnLayers; layer++)
        {
            cells[baseIdx + layer] = default;
        }

        int found = 0;
        int lastSurfaceY = int.MaxValue; // highest stored surface (for separation dedup)
        int floorY = anchorY - SurfaceSearchRadius;
        int wy = anchorY + SurfaceSearchRadius;

        while (wy >= floorY)
        {
            if (!ws.IsInBounds(wx, wy, wz))
            {
                // Unloaded voxel: everything below in this column is unknown.
                // Keep whatever surfaces we found above it; if none, the whole
                // column is do-not-enter.
                if (found == 0)
                {
                    cells[baseIdx].flags = CellFlags.OutOfBounds;
                }
                return;
            }
            VoxelType here = ws.GetVoxelWorld(wx, wy, wz);
            if (VoxelTypeInfo.IsSolid(here))
            {
                wy--;
                continue;
            }

            // Air or water. Need solid (or the floor of a water body) below.
            if (!ws.IsInBounds(wx, wy - 1, wz))
            {
                if (found == 0)
                {
                    cells[baseIdx].flags = CellFlags.OutOfBounds;
                }
                return;
            }

            if (here == VoxelType.Water)
            {
                // Only the top voxel of a water body is a surface; we hit it
                // first scanning top-down. Find the body's bottom so we can
                // skip past it (and price wade vs swim by depth).
                int waterBottom = wy;
                while (waterBottom - 1 >= floorY && ws.IsInBounds(wx, waterBottom - 1, wz)
                    && ws.GetVoxelWorld(wx, waterBottom - 1, wz) == VoxelType.Water)
                {
                    waterBottom--;
                }
                if (profile.canSwim && (found == 0 || lastSurfaceY - wy >= MinLayerSeparation)
                    && ColumnFits(ws, profile, wx, wy, wz, out float waterWallCost))
                {
                    int thresholdVoxels = Mathf.Max(1, Mathf.FloorToInt(profile.swimDepthThreshold));
                    int probeY = wy - (thresholdVoxels - 1);
                    bool swimming = ws.IsInBounds(wx, probeY, wz) && ws.GetVoxelWorld(wx, probeY, wz) == VoxelType.Water;
                    WalkabilityCell wc = default;
                    wc.surfaceY = (short)wy;
                    wc.flags = CellFlags.Walkable | CellFlags.Water;
                    wc.cost = (swimming ? profile.swimCost : profile.waterCost) * waterWallCost;
                    cells[baseIdx + found] = wc;
                    lastSurfaceY = wy;
                    found++;
                    if (found >= MaxColumnLayers)
                    {
                        return;
                    }
                }
                // Jump below the water body so we don't re-enter it per voxel.
                wy = waterBottom - 1;
                continue;
            }

            // Dry surface: air over solid.
            if (!VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(wx, wy - 1, wz)))
            {
                wy--;
                continue;
            }

            // Aquatic mobs can't stand on dry land — skip dry surfaces entirely
            // so A* confines them to the water body. (Water cells above were
            // already stored by the water branch and gated on canSwim.)
            if (profile.aquatic)
            {
                wy--;
                continue;
            }

            // Vertical clearance above the surface — a mob can't stand in a
            // slot shorter than the headroom it needs.
            bool blocked = false;
            for (int h = 1; h < profile.verticalClearance; h++)
            {
                if (!ws.IsInBounds(wx, wy + h, wz) || VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(wx, wy + h, wz)))
                {
                    blocked = true;
                    break;
                }
            }
            // Reject if a path-blocking entity (tree, chest) occupies a cell
            // the mob would stand in.
            if (!blocked && world != null)
            {
                for (int h = 0; h < profile.verticalClearance; h++)
                {
                    if (world.IsPathBlocked(wx, wy + h, wz))
                    {
                        blocked = true;
                        break;
                    }
                }
            }
            if (blocked)
            {
                wy--;
                continue;
            }

            if ((found == 0 || lastSurfaceY - wy >= MinLayerSeparation)
                && ColumnFits(ws, profile, wx, wy, wz, out float wallCost))
            {
                // Tag (don't reject) cells inside a hazard's danger zone — the
                // surface is still walkable, just flagged so wander/normal
                // pathing can route around it. Same band as the blocker check.
                CellFlags hazardFlag = CellFlags.None;
                if (world != null)
                {
                    for (int h = 0; h < profile.verticalClearance; h++)
                    {
                        if (world.IsHazard(wx, wy + h, wz))
                        {
                            hazardFlag = CellFlags.Hazard;
                            break;
                        }
                    }
                }

                WalkabilityCell wc = default;
                wc.surfaceY = (short)wy;
                wc.flags = CellFlags.Walkable | hazardFlag;
                wc.cost = wallCost;
                cells[baseIdx + found] = wc;
                lastSurfaceY = wy;
                found++;
                if (found >= MaxColumnLayers)
                {
                    return;
                }
            }
            wy--;
        }
    }

    // Horizontal body-fit + wall-proximity test for a candidate surface whose
    // standing voxel is (wx, wy, wz). The mob is modeled as a disk of radius
    // profile.clearanceRadius centered in the cell; we scan the surrounding
    // solid voxels across the mob's standing band [wy, wy+verticalClearance)
    // and find the nearest one. Returns false (do-not-stand) if the disk
    // overlaps any solid — the body physically can't fit. Otherwise returns
    // true and sets wallCostMultiplier to a soft penalty (>=1) that grows as
    // the nearest wall approaches within WallAvoidMargin of the body edge, so
    // A* steers toward open centerlines without sealing narrow corridors.
    //
    // Distances are exact closest-point disk-vs-voxel-footprint tests, so the
    // rule is self-tuning by body size: a 0.4 mob clears a wall-flush 1m cell
    // (0.1m gap) but a 0.6 mob does not, and a 1-wide tunnel stays passable
    // for the former because every cell is penalized equally.
    private static bool ColumnFits(WorldState ws, in TraversalProfile profile, int wx, int wy, int wz, out float wallCostMultiplier)
    {
        wallCostMultiplier = 1f;
        float radius = profile.clearanceRadius;
        // Solids past this Chebyshev ring can't reach the disk or its margin.
        int ring = Mathf.CeilToInt(radius + WallAvoidMargin);
        int band = Mathf.Max(1, profile.verticalClearance);
        float nearestGap = float.MaxValue; // body-edge-to-wall distance, min over neighbours

        for (int ndz = -ring; ndz <= ring; ndz++)
        {
            for (int ndx = -ring; ndx <= ring; ndx++)
            {
                if (ndx == 0 && ndz == 0)
                {
                    continue; // own column: already known air at the surface
                }
                // Closest point of neighbour voxel [ndx,ndx+1]x[ndz,ndz+1]
                // (cell-local coords) to the cell center at (0.5, 0.5).
                float ddx = 0.5f - Mathf.Clamp(0.5f, ndx, ndx + 1);
                float ddz = 0.5f - Mathf.Clamp(0.5f, ndz, ndz + 1);
                float dist = Mathf.Sqrt(ddx * ddx + ddz * ddz);
                float gap = dist - radius;
                if (gap >= WallAvoidMargin)
                {
                    continue; // too far to fail the fit or contribute a penalty
                }
                bool solid = false;
                for (int h = 0; h < band; h++)
                {
                    int sx = wx + ndx;
                    int sy = wy + h;
                    int sz = wz + ndz;
                    // Unknown (unloaded) neighbours are treated as non-blocking;
                    // column-level OutOfBounds already gates do-not-enter.
                    if (ws.IsInBounds(sx, sy, sz) && VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(sx, sy, sz)))
                    {
                        solid = true;
                        break;
                    }
                }
                if (!solid)
                {
                    continue;
                }
                if (gap < 0f)
                {
                    return false; // disk overlaps a wall — mob can't stand here
                }
                if (gap < nearestGap)
                {
                    nearestGap = gap;
                }
            }
        }

        if (nearestGap < WallAvoidMargin)
        {
            float t = 1f - nearestGap / WallAvoidMargin; // 1 at touching, 0 at margin edge
            wallCostMultiplier = 1f + WallProximityCost * t;
        }
        return true;
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
    // generously — an entry is cacheSize² × MaxColumnLayers WalkabilityCells;
    // realistic resident footprint (the few quanta around active mobs) is a
    // small fraction of the cap. Eviction sweeps the oldest 25% when this
    // trips so we don't thrash on the cap.
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
            Cells = new WalkabilityCell[cacheSize * cacheSize * WalkabilityGrid.MaxColumnLayers],
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
                WalkabilityGrid.SampleColumn(ws, world, profile, wx, anchorY, wz,
                    entry.Cells, (j * cacheSize + i) * WalkabilityGrid.MaxColumnLayers);
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
