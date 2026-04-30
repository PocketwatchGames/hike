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
public readonly struct TraversalProfile
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
    // origin column.
    public void Sample(WorldState ws, in TraversalProfile profile, int worldX, int worldY, int worldZ, int size)
    {
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

        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int wx = _originX + i;
                int wz = _originZ + j;
                _cells[j * size + i] = SampleColumn(ws, profile, wx, worldY, wz);
            }
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
    private static WalkabilityCell SampleColumn(WorldState ws, in TraversalProfile profile, int wx, int anchorY, int wz)
    {
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

            cell.surfaceY = (short)wy;
            cell.flags = CellFlags.Walkable;
            cell.cost = 1f;
            return cell;
        }

        return cell;
    }
}
