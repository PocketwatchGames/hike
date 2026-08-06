using Godot;

[System.Flags]
public enum ECellFlags : byte
{
    None = 0,
    // The run contains an authored VoxelType.Opening voxel — the void of a
    // doorway or window. Under the cell model this is the ONE job Opening has
    // left: forcing a region split an author wants and geometry cannot imply.
    // Its old job (blocking the column rule's band test) is gone with the rule.
    Opening = 1 << 0,
    // No ceiling — the run reaches the top of the world. Sky cells form sky
    // regions, and sky regions never cut.
    Sky = 1 << 1,
    // The ceiling is an entity roof (WorldState.SunOpaque), not a voxel. Roofs
    // are Node3Ds with their own collision, so they cap a space exactly like
    // stone — without this a cottage interior is a sky cell and never cuts.
    RoofCapped = 1 << 2,
}

// One air gap in one voxel column: a contiguous run of empty space with solid
// below it and solid — or sky — above.
//
// The air run is [FloorY, CeilingY): FloorY is the lowest empty voxel (where
// feet sit), CeilingY the lowest solid above it. Clearance is their difference.
public struct Cell
{
    public short FloorY;
    public short CeilingY;
    public ECellFlags Flags;

    public bool IsSky => (Flags & ECellFlags.Sky) != 0;
    public bool IsOpening => (Flags & ECellFlags.Opening) != 0;
    public int Clearance => CeilingY - FloorY;
    public bool Contains(int wy) => wy >= FloorY && wy < CeilingY;
}

// Cell decomposition of the voxel world: every column walked bottom to top,
// one cell emitted per air gap. The substrate the ceiling cutaway's region
// segmentation is built on (see CellRegions).
//
// WHY CELLS AT ALL: every unresolved defect in the column rule has the same
// shape — a column is being asked to know something only a space can know. A
// column doesn't know it belongs to a roof, a land bridge or a tunnel, so each
// rule written at column scope needs another special case to reconstruct what a
// space would have known for free. This builds the space explicitly.
//
// CELLS ARE PLAYER-INDEPENDENT. They are a property of the world and read the
// same wherever you stand. The window below is only the RESIDENCY policy for
// that cache — the same relationship WindowedVolumeMap has to light — not a
// player-relative rule. Columns are rescanned when they scroll in or when their
// chunk is invalidated, never per frame.
//
// Regions, by contrast, are deliberately NOT cached here: a connected component
// has unbounded extent (a cave system spans hundreds of chunks, an open plain is
// one region), so labelling them world-wide would need the "iterate every chunk"
// pass the streaming design forbids, and one knocked-through wall would merge
// two components arbitrarily far away. CellRegions labels per tick over the
// window instead.
public class CellField
{
    // Columns per side of the window. One column is one voxel column — the
    // resolution the occluders actually have. A buffer size the indexing depends
    // on, not a tuning value; matches ClipColumnMask's window so the two cutaway
    // modes reach the same distance.
    public const int GRID_SIZE = 64;
    // Cells kept per column, packed low to high. Mirrors
    // WalkabilityGrid.MaxColumnLayers — a barn column holds two, natural terrain
    // one. Overflow is COUNTED rather than silently dropped: if TruncatedColumns
    // is ever non-zero the number is wrong, not the model.
    public const int MAX_CELLS_PER_COLUMN = 4;
    // CeilingY of a cell open to the sky.
    public const short SKY_CEILING = short.MaxValue;

    // Voxels the floor resolve climbs looking for air. The player's Y is
    // fractional on slopes and grade blocks and the capsule settles slightly
    // into what it stands on, so the feet voxel is often the solid underfoot.
    private const int MAX_FLOOR_CLIMB = 3;

    private readonly Cell[] _cells = new Cell[GRID_SIZE * GRID_SIZE * MAX_CELLS_PER_COLUMN];
    private readonly byte[] _count = new byte[GRID_SIZE * GRID_SIZE];
    // Per-column "needs a rescan". Set for columns scrolling in from the window
    // edge and for columns whose chunk stack was invalidated.
    private readonly bool[] _stale = new bool[GRID_SIZE * GRID_SIZE];

    // Scratch for Scroll, ping-ponged so overlapping ranges can't clobber
    // themselves. Sized once rather than per scroll.
    private readonly Cell[] _cellScratch = new Cell[GRID_SIZE * GRID_SIZE * MAX_CELLS_PER_COLUMN];
    private readonly byte[] _countScratch = new byte[GRID_SIZE * GRID_SIZE];
    private readonly bool[] _staleScratch = new bool[GRID_SIZE * GRID_SIZE];

    // Window anchor in whole columns of world space, so a carried column keeps
    // describing the same patch of world.
    private int _minColumnX;
    private int _minColumnZ;
    private bool _anchored;

    // Diagnostics for the Stage 1 dump. Columns rescanned this tick, and columns
    // that hit the MAX_CELLS_PER_COLUMN cap (which should stay at zero).
    public int ScannedColumns { get; private set; }
    public int TruncatedColumns { get; private set; }

    // Bumped whenever the cells or their indexing changed — a rescan, or a
    // scroll (which shifts every column's index even where its contents were
    // carried). A region labelling is a pure function of the cells plus the join
    // parameters, so this is what lets CellRegions skip relabelling a window
    // nothing has happened to.
    public int Version { get; private set; }

    // World XZ of the window's (0,0) corner, and its size in columns.
    public int MinColumnX => _minColumnX;
    public int MinColumnZ => _minColumnZ;

    // Rebuilds whatever the window needs this tick: scroll onto `center`, honour
    // pending invalidations, rescan stale columns. Costs nothing on a tick where
    // the player hasn't crossed a column boundary and nothing was edited.
    public void Tick(WorldState world, Vector3 center)
    {
        if (world == null)
        {
            return;
        }
        using var _prof = Profiler.Sample("CellField.Tick");

        Recenter(center);
        ScannedColumns = 0;
        TruncatedColumns = 0;
        for (int gz = 0; gz < GRID_SIZE; gz++)
        {
            int row = gz * GRID_SIZE;
            for (int gx = 0; gx < GRID_SIZE; gx++)
            {
                if (!_stale[row + gx])
                {
                    continue;
                }
                ScanColumn(world, gx, gz);
                _stale[row + gx] = false;
                ScannedColumns++;
            }
        }
        if (ScannedColumns > 0)
        {
            Version++;
        }
    }

    // Marks every column over a chunk's XZ footprint for rescan. A chunk's cells
    // can be changed by a voxel write, by a roof stamping SunOpaque with no voxel
    // write at all, or by a door stamping Barrier into its own doorway.
    public void InvalidateChunk(Vector3I chunkCoord)
    {
        if (!_anchored)
        {
            return;
        }
        // A chunk at ANY height invalidates the whole column, because a cell is a
        // run through the entire column and a change anywhere in it can split,
        // merge or re-ceiling every cell above.
        int minX = chunkCoord.X * ChunkState.SIZE - _minColumnX;
        int minZ = chunkCoord.Z * ChunkState.SIZE - _minColumnZ;
        int maxX = Mathf.Min(minX + ChunkState.SIZE - 1, GRID_SIZE - 1);
        int maxZ = Mathf.Min(minZ + ChunkState.SIZE - 1, GRID_SIZE - 1);
        minX = Mathf.Max(minX, 0);
        minZ = Mathf.Max(minZ, 0);
        for (int gz = minZ; gz <= maxZ; gz++)
        {
            int row = gz * GRID_SIZE;
            for (int gx = minX; gx <= maxX; gx++)
            {
                _stale[row + gx] = true;
            }
        }
    }

    public int CountAt(int gx, int gz)
    {
        if (gx < 0 || gz < 0 || gx >= GRID_SIZE || gz >= GRID_SIZE)
        {
            return 0;
        }
        return _count[gz * GRID_SIZE + gx];
    }

    public Cell CellAt(int gx, int gz, int slot)
    {
        return _cells[(gz * GRID_SIZE + gx) * MAX_CELLS_PER_COLUMN + slot];
    }

    // Window column holding this world column, or false if it is out of reach.
    public bool TryColumn(int wx, int wz, out int gx, out int gz)
    {
        gx = wx - _minColumnX;
        gz = wz - _minColumnZ;
        return gx >= 0 && gz >= 0 && gx < GRID_SIZE && gz < GRID_SIZE;
    }

    public int WorldX(int gx) => _minColumnX + gx;
    public int WorldZ(int gz) => _minColumnZ + gz;

    // True for the outermost ring. A region touching it is TRUNCATED — its true
    // extent continues past the window, so its max ceiling (and therefore its cut
    // height) is measured over the resident part only. That is the likeliest
    // source of unexplained instability in this design, so it is reported rather
    // than left to be inferred from odd behaviour later.
    public static bool IsWindowEdge(int gx, int gz)
    {
        return gx == 0 || gz == 0 || gx == GRID_SIZE - 1 || gz == GRID_SIZE - 1;
    }

    // The cell the player is standing in: the one whose air run contains their
    // feet. Climbs like ClipColumnMask.ResolveFloor before searching — the
    // capsule settles into what it stands on and grade blocks put the feet inside
    // a solid voxel, so floor(playerY) alone lands below every cell in the column.
    public bool TryResolveCell(WorldState world, Vector3 position, out int gx, out int gz, out int slot)
    {
        slot = -1;
        int wx = Mathf.FloorToInt(position.X);
        int wz = Mathf.FloorToInt(position.Z);
        if (!TryColumn(wx, wz, out gx, out gz))
        {
            return false;
        }
        int foot = Mathf.FloorToInt(position.Y);
        for (int i = 0; i < MAX_FLOOR_CLIMB && IsOccupied(world, wx, foot, wz); i++)
        {
            foot++;
        }
        int count = _count[gz * GRID_SIZE + gx];
        int baseIndex = (gz * GRID_SIZE + gx) * MAX_CELLS_PER_COLUMN;
        for (int i = 0; i < count; i++)
        {
            if (_cells[baseIndex + i].Contains(foot))
            {
                slot = i;
                return true;
            }
        }
        return false;
    }

    // Occupancy for the decomposition. TWO sources, and the second is easy to
    // miss: a roof is an ENTITY, so a purely voxel-based test lets a cottage
    // interior read as open sky and the space under it never cuts. Roofs stamp
    // their cover into SunOpaque (one sheet at eave level, punched through
    // wherever the roof is holed), which is exactly the ceiling this wants.
    // Same pair InteriornessGen.IsOpen and ClipColumnMask.HasCoverAbove use.
    //
    // Water is NOT occupied: a flooded cave is still a space you occupy and the
    // cutaway has to cut its ceiling.
    public static bool IsOccupied(WorldState world, int wx, int wy, int wz)
    {
        return VoxelTypeInfo.IsSolid(world.GetVoxelWorld(wx, wy, wz))
            || world.GetSunOpaqueWorld(wx, wy, wz);
    }

    // Moves the window onto the player in whole columns, carrying scanned
    // columns with it so each keeps describing the same world column. Columns
    // scrolling in from the edge are marked stale and rescanned this tick.
    private void Recenter(Vector3 center)
    {
        int half = GRID_SIZE / 2;
        int newMinX = Mathf.FloorToInt(center.X) - half;
        int newMinZ = Mathf.FloorToInt(center.Z) - half;
        if (!_anchored)
        {
            System.Array.Fill(_stale, true);
            _minColumnX = newMinX;
            _minColumnZ = newMinZ;
            _anchored = true;
            return;
        }

        int dx = newMinX - _minColumnX;
        int dz = newMinZ - _minColumnZ;
        if (dx == 0 && dz == 0)
        {
            return;
        }
        _minColumnX = newMinX;
        _minColumnZ = newMinZ;
        // A scroll shifts every column's index, so a labelling built against the
        // old indexing is invalid even where the cells themselves were carried.
        Version++;
        if (Mathf.Abs(dx) >= GRID_SIZE || Mathf.Abs(dz) >= GRID_SIZE)
        {
            System.Array.Fill(_stale, true);
            return;
        }
        Scroll(dx, dz);
    }

    // Destination column (gx, gz) holds the world column that lived at
    // (gx + dx, gz + dz) in the old window. Everything outside the carried block
    // is left stale (the scratch buffers are pre-filled with "stale, no cells"),
    // so a column that just came into range is rescanned rather than inheriting
    // whatever used to sit at its index.
    private void Scroll(int dx, int dz)
    {
        System.Array.Clear(_cellScratch, 0, _cellScratch.Length);
        System.Array.Clear(_countScratch, 0, _countScratch.Length);
        System.Array.Fill(_staleScratch, true);

        int copyWidth = GRID_SIZE - Mathf.Abs(dx);
        int dstX = Mathf.Max(0, -dx);
        int srcX = dstX + dx;
        for (int gz = 0; gz < GRID_SIZE; gz++)
        {
            int srcZ = gz + dz;
            if (srcZ < 0 || srcZ >= GRID_SIZE)
            {
                continue;
            }
            System.Array.Copy(_count, srcZ * GRID_SIZE + srcX, _countScratch, gz * GRID_SIZE + dstX, copyWidth);
            System.Array.Copy(_stale, srcZ * GRID_SIZE + srcX, _staleScratch, gz * GRID_SIZE + dstX, copyWidth);
            System.Array.Copy(_cells, (srcZ * GRID_SIZE + srcX) * MAX_CELLS_PER_COLUMN,
                _cellScratch, (gz * GRID_SIZE + dstX) * MAX_CELLS_PER_COLUMN, copyWidth * MAX_CELLS_PER_COLUMN);
        }

        System.Array.Copy(_cellScratch, _cells, _cells.Length);
        System.Array.Copy(_countScratch, _count, _count.Length);
        System.Array.Copy(_staleScratch, _stale, _stale.Length);
    }

    // Walks one column bottom to top and emits a cell per air gap.
    //
    // Iterates the column's CHUNK STACK rather than calling GetVoxelWorld per
    // voxel: that accessor is a dictionary lookup each time, and this is the only
    // hot loop in the subsystem. Resolving the chunk (and its SunOpaque sheet)
    // once per 16 voxels turns ~200 lookups per column into ~12.
    //
    // A non-resident chunk reads as air, matching GetVoxelWorld and every other
    // accessor in the codebase — the correct streaming default, and what makes an
    // absent sky chunk read as sky rather than as a ceiling.
    private void ScanColumn(WorldState world, int gx, int gz)
    {
        int columnIndex = gz * GRID_SIZE + gx;
        int baseIndex = columnIndex * MAX_CELLS_PER_COLUMN;
        int wx = _minColumnX + gx;
        int wz = _minColumnZ + gz;
        int cx = FloorDiv(wx, ChunkState.SIZE);
        int cz = FloorDiv(wz, ChunkState.SIZE);
        int lx = wx - cx * ChunkState.SIZE;
        int lz = wz - cz * ChunkState.SIZE;

        int count = 0;
        // Sentinel for "not inside a run". Every run is bounded below by a solid
        // voxel or by the bottom of the world, which counts as solid — there is
        // nothing under the world to fall into.
        int runStart = int.MinValue;
        bool runHasOpening = false;

        for (int cy = world.Min.Y; cy <= world.Max.Y; cy++)
        {
            var coord = new Vector3I(cx, cy, cz);
            ChunkState chunk = world.GetChunk(coord);
            world.SunOpaque.TryGetValue(coord, out bool[,,] opaque);
            int chunkBaseY = cy * ChunkState.SIZE;
            for (int ly = 0; ly < ChunkState.SIZE; ly++)
            {
                VoxelType voxel = chunk == null ? VoxelType.Air : chunk.Voxels[lx, ly, lz];
                bool roof = opaque != null && opaque[lx, ly, lz];
                if (!VoxelTypeInfo.IsSolid(voxel) && !roof)
                {
                    if (runStart == int.MinValue)
                    {
                        runStart = chunkBaseY + ly;
                        runHasOpening = false;
                    }
                    if (voxel == VoxelType.Opening)
                    {
                        runHasOpening = true;
                    }
                    continue;
                }
                if (runStart == int.MinValue)
                {
                    continue;
                }
                if (count < MAX_CELLS_PER_COLUMN)
                {
                    ECellFlags flags = runHasOpening ? ECellFlags.Opening : ECellFlags.None;
                    if (roof)
                    {
                        flags |= ECellFlags.RoofCapped;
                    }
                    _cells[baseIndex + count] = new Cell
                    {
                        FloorY = (short)runStart,
                        CeilingY = (short)(chunkBaseY + ly),
                        Flags = flags,
                    };
                    count++;
                }
                else
                {
                    TruncatedColumns++;
                }
                runStart = int.MinValue;
            }
        }

        // A run still open at the top of the world reaches the sky.
        if (runStart != int.MinValue && count < MAX_CELLS_PER_COLUMN)
        {
            _cells[baseIndex + count] = new Cell
            {
                FloorY = (short)runStart,
                CeilingY = SKY_CEILING,
                Flags = ECellFlags.Sky | (runHasOpening ? ECellFlags.Opening : ECellFlags.None),
            };
            count++;
        }
        _count[columnIndex] = (byte)count;
    }

    private static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? q - 1 : q;
    }
}
