using System.Collections.Generic;
using Godot;

// A connected component of cells: one SPACE, as opposed to one column.
public struct CellRegion
{
    public int Id;
    public int CellCount;
    public int MinFloorY;
    public int MaxFloorY;
    // SKY_CEILING for a sky region; both ends of the real spread otherwise. The
    // spread is what says whether the "ceiling lumps are bounded by one plateau"
    // claim actually holds for this region.
    public int MinCeilingY;
    public int MaxCeilingY;
    public bool IsSky;
    // The region continues past the window, so MaxCeilingY — and therefore the
    // cut height derived from it — is measured over the RESIDENT PART ONLY and
    // will change as the player walks with no visible cause. Reported so a
    // truncated region is identifiable at a glance instead of being inferred
    // from odd behaviour later.
    public bool TouchesWindowEdge;
    // Plateau index the region would cut at: ceil(MaxCeilingY / plateau).
    public int CutPlateau;
    // Lexicographically-first cell in WORLD coords. Region ids are assigned in
    // scan order and are therefore unstable between ticks — this key is not, so
    // the debug colouring stays put as the window scrolls and relabels.
    public int KeyX;
    public int KeyZ;
    public int KeyFloorY;

    public float CutHeight => CutPlateau * CellRegions.Plateau;
}

// Segments a CellField into regions and answers "which region is the player in".
//
// A region is a connected component of cells joined by adjacency (4-neighbour
// columns, overlapping vertical ranges, a traversable floor step) subject to one
// homogeneity constraint: both cells must CUT AT THE SAME PLANE.
//
// Labelling runs per tick over the window and is deliberately not cached — see
// the note in CellField on why regions cannot be a global bake.
public class CellRegions
{
    // Vertical band the cutaway snaps to. GameCamera owns the number because the
    // editor's plateau-snapped brushes build onto the same grid; one shared
    // constant is the point, so this reads it rather than declaring a second 4.
    public static readonly int Plateau = Mathf.Max(1, Mathf.RoundToInt(GameCamera.PLATEAU_STEP));

    // Safety bound on the wall flood, in columns. Intended as a backstop for the
    // unbounded-solid case (a cave cut into a hillside) rather than as a tuning
    // value — but see ClaimWalls: it also bites on any building whose outer wall
    // is one long connected run, which is most of them. Generous over any
    // authored wall THICKNESS, which is the dimension it was meant to bound.
    public const int MAX_WALL_DILATE = 8;

    private const int NO_LABEL = -1;
    // Bucket a sky cell lands in. Its own, so a sky cell can never join a cell
    // with a real ceiling however close their floors are — which is what
    // separates a cave mouth from the open air outside it with no special case.
    private const int SKY_PLATEAU = int.MaxValue;
    // How far the hysteresis anchor may sit from the player's own column before
    // it counts as stale. Two columns covers a doorway plus its jambs; anything
    // further and the player has been moved rather than having shuffled.
    private const int HYSTERESIS_RADIUS = 2;
    // Clearance a cell needs before the player counts as STANDING in it. A door
    // closing over the player leaves a 1-voxel slot above the barrier that their
    // feet technically resolve into; seeding there would put them in a singleton
    // region and cut a hole the size of the doorframe. Their real space is
    // whichever room they were last in, which is what hysteresis holds.
    private const int MIN_STAND_CLEARANCE = 2;

    // Whether the clearance term takes part in the join predicate. Off by
    // default: clearance is never used in the cut — cut height derives from
    // ceilingY alone — so ceiling homogeneity is the only kind a region needs,
    // and the clearance term only fragments (a room with a raised platform
    // splits into two regions cutting at the same plane, for no benefit). The
    // A/B exists because if it DOES earn its place that is a finding worth
    // understanding, not a default to have left switched on.
    public bool UseClearanceBucket;
    // Floor step two cells may differ by and still join. Derived from
    // PlayerData.stepHeight but floored at one voxel: the authored value is 0.5m,
    // which rounds to zero, and a zero step fragments ordinary terrain into a
    // region per elevation. Raise it only if the player's step ever exceeds a
    // voxel and a ledge that tall should read as one space.
    public int StepVoxels = 1;

    public enum ESeedSource
    {
        None,
        // The player's own cell, resolved directly.
        Direct,
        // Their cell is a doorway void, or too short to stand in — the previous
        // region is held instead.
        Hysteresis,
        // No previous region to hold (spawn, teleport): the roomiest adjacent
        // cell at the player's level.
        Fallback,
    }

    private readonly int[] _label = new int[CellField.GRID_SIZE * CellField.GRID_SIZE * CellField.MAX_CELLS_PER_COLUMN];
    private readonly int[] _queue = new int[CellField.GRID_SIZE * CellField.GRID_SIZE * CellField.MAX_CELLS_PER_COLUMN];
    private readonly List<CellRegion> _regions = new();

    // Wall columns the player's region claims, and the flood scratch behind them.
    private readonly bool[] _wallClaim = new bool[CellField.GRID_SIZE * CellField.GRID_SIZE];
    private readonly bool[] _columnSeen = new bool[CellField.GRID_SIZE * CellField.GRID_SIZE];
    private readonly int[] _columnQueue = new int[CellField.GRID_SIZE * CellField.GRID_SIZE];

    // Hysteresis anchor, in WORLD coords so it survives a window scroll. Held as
    // a cell identity (column + floor) rather than a region id, because ids are
    // re-assigned every tick.
    private bool _hasPrevSeed;
    private int _prevSeedX;
    private int _prevSeedZ;
    private int _prevSeedFloorY;

    public IReadOnlyList<CellRegion> Regions => _regions;
    public int PlayerRegion { get; private set; } = NO_LABEL;
    public ESeedSource SeedSource { get; private set; }
    public int WallColumnsClaimed { get; private set; }
    // True when the wall flood ran out of budget rather than terminating on the
    // far face of a wall — the signature of flooding into unbounded solid (a cave
    // cut into a hillside), where the bite taken out of the hill is arbitrary.
    public bool WallClaimHitBudget { get; private set; }
    // Columns of solid the flood travelled before it stopped. NOT wall thickness:
    // walls are connected to each other, so a flood that steps sideways along a
    // wall run accumulates depth by DISTANCE ALONG the run. A one-column-thick
    // ring around a building reports the ring's length, not 1.
    public int WallClaimDepth { get; private set; }

    // Version of the field the current labelling was built against, and the join
    // parameter it used. A labelling is a pure function of both, so re-running it
    // against an unchanged window is wasted work — and the window is unchanged on
    // every frame the player doesn't cross a column boundary.
    private int _labelledVersion = -1;
    private bool _labelledClearanceBucket;

    public void Tick(CellField field, WorldState world, Vector3 playerPosition, int maxDilate)
    {
        using var _prof = Profiler.Sample("CellRegions.Tick");
        if (field.Version != _labelledVersion || UseClearanceBucket != _labelledClearanceBucket)
        {
            Label(field);
            _labelledVersion = field.Version;
            _labelledClearanceBucket = UseClearanceBucket;
        }
        // Both of these depend on where the player IS, not only on the cells, so
        // they re-run every tick regardless. Each is bounded by the player's own
        // region rather than by the window.
        ResolvePlayerRegion(field, world, playerPosition);
        ClaimWalls(field, maxDilate);
    }

    public int LabelAt(int gx, int gz, int slot)
    {
        return _label[(gz * CellField.GRID_SIZE + gx) * CellField.MAX_CELLS_PER_COLUMN + slot];
    }

    public bool IsWallColumn(int gx, int gz)
    {
        return _wallClaim[gz * CellField.GRID_SIZE + gx];
    }

    // Flood every cell in the window, seeding a fresh region from each unlabelled
    // one. Labelling all of them rather than only the player's is what gives the
    // Stage 1 visualisation a whole picture to be judged against intuition —
    // which is the entire point of building this before anything renders from it.
    private void Label(CellField field)
    {
        System.Array.Fill(_label, NO_LABEL);
        _regions.Clear();

        for (int gz = 0; gz < CellField.GRID_SIZE; gz++)
        {
            for (int gx = 0; gx < CellField.GRID_SIZE; gx++)
            {
                int count = field.CountAt(gx, gz);
                for (int slot = 0; slot < count; slot++)
                {
                    if (LabelAt(gx, gz, slot) != NO_LABEL)
                    {
                        continue;
                    }
                    Flood(field, gx, gz, slot, _regions.Count);
                }
            }
        }
    }

    private void Flood(CellField field, int seedX, int seedZ, int seedSlot, int regionId)
    {
        int head = 0;
        int tail = 0;
        _label[Index(seedX, seedZ, seedSlot)] = regionId;
        _queue[tail++] = Index(seedX, seedZ, seedSlot);

        Cell seed = field.CellAt(seedX, seedZ, seedSlot);
        var region = new CellRegion
        {
            Id = regionId,
            CellCount = 0,
            MinFloorY = int.MaxValue,
            MaxFloorY = int.MinValue,
            MinCeilingY = int.MaxValue,
            MaxCeilingY = int.MinValue,
            IsSky = seed.IsSky,
            KeyX = int.MaxValue,
            KeyZ = int.MaxValue,
            KeyFloorY = int.MaxValue,
        };

        while (head < tail)
        {
            Unpack(_queue[head++], out int gx, out int gz, out int slot);
            Cell cell = field.CellAt(gx, gz, slot);
            Accumulate(ref region, field, gx, gz, cell);

            for (int side = 0; side < 4; side++)
            {
                int nx = gx + (side == 0 ? 1 : side == 1 ? -1 : 0);
                int nz = gz + (side == 2 ? 1 : side == 3 ? -1 : 0);
                if (nx < 0 || nz < 0 || nx >= CellField.GRID_SIZE || nz >= CellField.GRID_SIZE)
                {
                    continue;
                }
                int neighbourCount = field.CountAt(nx, nz);
                for (int ns = 0; ns < neighbourCount; ns++)
                {
                    if (_label[Index(nx, nz, ns)] != NO_LABEL)
                    {
                        continue;
                    }
                    if (!Adjacent(cell, field.CellAt(nx, nz, ns)))
                    {
                        continue;
                    }
                    _label[Index(nx, nz, ns)] = regionId;
                    _queue[tail++] = Index(nx, nz, ns);
                }
            }
        }

        region.CutPlateau = region.IsSky ? SKY_PLATEAU : CeilDiv(region.MaxCeilingY, Plateau);
        _regions.Add(region);
    }

    private static void Accumulate(ref CellRegion region, CellField field, int gx, int gz, in Cell cell)
    {
        region.CellCount++;
        region.MinFloorY = Mathf.Min(region.MinFloorY, cell.FloorY);
        region.MaxFloorY = Mathf.Max(region.MaxFloorY, cell.FloorY);
        region.MinCeilingY = Mathf.Min(region.MinCeilingY, cell.CeilingY);
        region.MaxCeilingY = Mathf.Max(region.MaxCeilingY, cell.CeilingY);
        region.TouchesWindowEdge |= CellField.IsWindowEdge(gx, gz);

        // Lexicographic min over (worldZ, worldX, floorY) — a total order, so the
        // winner is the same cell whatever order the flood visited them in.
        int wx = field.WorldX(gx);
        int wz = field.WorldZ(gz);
        if (wz < region.KeyZ
            || (wz == region.KeyZ && wx < region.KeyX)
            || (wz == region.KeyZ && wx == region.KeyX && cell.FloorY < region.KeyFloorY))
        {
            region.KeyZ = wz;
            region.KeyX = wx;
            region.KeyFloorY = cell.FloorY;
        }
    }

    private bool Adjacent(in Cell a, in Cell b)
    {
        // An authored opening joins nothing. Two 3m rooms either side of a door
        // would otherwise merge into one region — often fine, occasionally not,
        // and this is the author's way of forcing the split.
        if (a.IsOpening || b.IsOpening)
        {
            return false;
        }
        // Vertical ranges must overlap. The barn's bay floor at [0,3) and its
        // balcony deck at [3,8) in neighbouring columns are two spaces, not one.
        if (a.FloorY >= b.CeilingY || b.FloorY >= a.CeilingY)
        {
            return false;
        }
        if (Mathf.Abs(a.FloorY - b.FloorY) > StepVoxels)
        {
            return false;
        }
        if (CutPlateauOf(a) != CutPlateauOf(b))
        {
            return false;
        }
        if (UseClearanceBucket && ClearanceBucketOf(a) != ClearanceBucketOf(b))
        {
            return false;
        }
        return true;
    }

    // The plateau boundary this cell's cut plane would land on.
    //
    // Deliberately ceil(), not floor(). The homogeneity a region needs is "these
    // cells cut at the same plane", and the cut is ceil(ceiling / plateau) — so
    // quantizing the same way makes the bucket exactly the property the cut
    // depends on. Ceilings at 7 and 8 both cut at 8 and belong together; a
    // floor() bucket would have split them for no reason the render could show.
    private static int CutPlateauOf(in Cell cell)
    {
        return cell.IsSky ? SKY_PLATEAU : CeilDiv(cell.CeilingY, Plateau);
    }

    private static int ClearanceBucketOf(in Cell cell)
    {
        return cell.IsSky ? SKY_PLATEAU : cell.Clearance / Plateau;
    }

    private void ResolvePlayerRegion(CellField field, WorldState world, Vector3 playerPosition)
    {
        PlayerRegion = NO_LABEL;
        SeedSource = ESeedSource.None;

        bool resolved = field.TryResolveCell(world, playerPosition, out int gx, out int gz, out int slot);
        if (resolved)
        {
            Cell cell = field.CellAt(gx, gz, slot);
            int label = LabelAt(gx, gz, slot);
            if (label != NO_LABEL && !cell.IsOpening && cell.Clearance >= MIN_STAND_CLEARANCE)
            {
                PlayerRegion = label;
                SeedSource = ESeedSource.Direct;
                RecordSeed(field.WorldX(gx), field.WorldZ(gz), cell.FloorY);
                return;
            }
        }

        // Standing in a doorway, or under a door that just shut over us. Hold the
        // previous region rather than picking between the two rooms the doorway
        // touches: "roomiest adjacent" is ambiguous exactly where it is most
        // exercised, and chatters as the player shuffles. With hysteresis the
        // transition fires once, on the far side.
        if (TryHysteresis(field, playerPosition, out int held))
        {
            PlayerRegion = held;
            SeedSource = ESeedSource.Hysteresis;
            return;
        }

        // Nothing to hold — spawn, teleport, or the anchor went stale.
        if (TryFallback(field, playerPosition, out int fallback, out int fx, out int fz, out int ffloor))
        {
            PlayerRegion = fallback;
            SeedSource = ESeedSource.Fallback;
            RecordSeed(fx, fz, ffloor);
            return;
        }

        // Last resort: the cell we resolved, however unstandable, beats nothing.
        if (resolved)
        {
            int label = LabelAt(gx, gz, slot);
            if (label != NO_LABEL)
            {
                PlayerRegion = label;
                SeedSource = ESeedSource.Direct;
                RecordSeed(field.WorldX(gx), field.WorldZ(gz), field.CellAt(gx, gz, slot).FloorY);
            }
        }
    }

    // Re-resolves the anchor cell in THIS tick's labelling. Identity is (column,
    // floorY), so a relabel or a window scroll can't lose it — but a door closing
    // underneath the anchor can, and then it correctly falls through.
    private bool TryHysteresis(CellField field, Vector3 playerPosition, out int label)
    {
        label = NO_LABEL;
        if (!_hasPrevSeed)
        {
            return false;
        }
        int wx = Mathf.FloorToInt(playerPosition.X);
        int wz = Mathf.FloorToInt(playerPosition.Z);
        if (Mathf.Abs(wx - _prevSeedX) > HYSTERESIS_RADIUS || Mathf.Abs(wz - _prevSeedZ) > HYSTERESIS_RADIUS)
        {
            return false;
        }
        if (!field.TryColumn(_prevSeedX, _prevSeedZ, out int gx, out int gz))
        {
            return false;
        }
        int count = field.CountAt(gx, gz);
        for (int slot = 0; slot < count; slot++)
        {
            if (field.CellAt(gx, gz, slot).FloorY != _prevSeedFloorY)
            {
                continue;
            }
            int found = LabelAt(gx, gz, slot);
            if (found == NO_LABEL)
            {
                return false;
            }
            label = found;
            return true;
        }
        return false;
    }

    private bool TryFallback(CellField field, Vector3 playerPosition,
        out int label, out int seedX, out int seedZ, out int seedFloorY)
    {
        label = NO_LABEL;
        seedX = 0;
        seedZ = 0;
        seedFloorY = 0;
        int wx = Mathf.FloorToInt(playerPosition.X);
        int wz = Mathf.FloorToInt(playerPosition.Z);
        int footY = Mathf.FloorToInt(playerPosition.Y);
        int best = -1;

        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!field.TryColumn(wx + dx, wz + dz, out int gx, out int gz))
                {
                    continue;
                }
                int count = field.CountAt(gx, gz);
                for (int slot = 0; slot < count; slot++)
                {
                    Cell cell = field.CellAt(gx, gz, slot);
                    if (cell.IsOpening || Mathf.Abs(cell.FloorY - footY) > StepVoxels + 1)
                    {
                        continue;
                    }
                    int found = LabelAt(gx, gz, slot);
                    if (found == NO_LABEL || cell.Clearance <= best)
                    {
                        continue;
                    }
                    best = cell.Clearance;
                    label = found;
                    seedX = field.WorldX(gx);
                    seedZ = field.WorldZ(gz);
                    seedFloorY = cell.FloorY;
                }
            }
        }
        return label != NO_LABEL;
    }

    private void RecordSeed(int wx, int wz, int floorY)
    {
        _hasPrevSeed = true;
        _prevSeedX = wx;
        _prevSeedZ = wz;
        _prevSeedFloorY = floorY;
    }

    // Walls are solid, so they belong to no region — without carrying them the
    // roof over a room cuts while the walls holding it up stand to full height.
    //
    // The extent is not a distance. It floods the SOLID outward from the region
    // boundary and stops at air belonging to another region, so it derives from
    // the wall's own thickness: a thin partition and a thick stone wall are both
    // claimed whole, and a party wall stops at its far face by construction.
    // Terminating on foreign air is NECESSARY BUT NOT SUFFICIENT, which is what
    // stage 2 showed the moment it started cutting: walls are connected to each
    // other, so a flood that only rejects foreign air steps SIDEWAYS along a wall
    // run and accumulates reach by distance along it. A building's outer wall is
    // one continuous ring, so standing in one room clipped the ring right round
    // past every other room — cutting the neighbouring two-storey space's walls
    // down to this room's lower ceiling while its roof, which resolves
    // participation over its OWN footprint, correctly stayed. Walls gone, roof
    // floating.
    //
    // Two rules fix it, and neither is a distance:
    //
    //   CLAIM BUT DO NOT PROPAGATE. A solid column already bounding somebody
    //   else's space is the far face of a wall. It cuts — it is still this
    //   region's wall too — but the flood stops there rather than continuing
    //   along it. An outer wall is bounded by open air along its whole length, so
    //   the run-along leak cannot start.
    //
    //   SEED DIAGONALLY. Rule one alone loses corner posts: a corner column is
    //   only ever diagonal from the region's air, so nothing orthogonal ever
    //   reaches it, and it would stand full height between two cut walls. The
    //   region's own columns therefore seed all eight neighbours; propagation
    //   past that stays orthogonal, which is what keeps thickness the only thing
    //   being measured.
    //
    // Thickness still derives itself: a wall is claimed layer by layer until the
    // layer that touches open air, so a thin partition and a thick stone wall are
    // both taken whole with no parameter. `maxDilate` is left as a pure backstop
    // for solid with no far face at all — a cave cut into a hillside — which is
    // the one case it was always meant to catch. WallClaimHitBudget reports it.
    private void ClaimWalls(CellField field, int maxDilate)
    {
        System.Array.Clear(_wallClaim, 0, _wallClaim.Length);
        System.Array.Clear(_columnSeen, 0, _columnSeen.Length);
        WallColumnsClaimed = 0;
        WallClaimHitBudget = false;
        WallClaimDepth = 0;
        if (PlayerRegion == NO_LABEL || PlayerRegion >= _regions.Count)
        {
            return;
        }
        CellRegion region = _regions[PlayerRegion];
        if (region.IsSky)
        {
            // Sky regions never cut, so there is nothing to carry walls for.
            return;
        }

        // The band the cut operates in. A cell of another region that overlaps it
        // is a space at the player's own level — the far side of a party wall —
        // and is where the flood must stop.
        int bandLow = region.MinFloorY;
        int bandHigh = region.CutPlateau * Plateau;

        int head = 0;
        int tail = 0;
        for (int gz = 0; gz < CellField.GRID_SIZE; gz++)
        {
            for (int gx = 0; gx < CellField.GRID_SIZE; gx++)
            {
                if (!ColumnHasRegion(field, gx, gz, PlayerRegion))
                {
                    continue;
                }
                _columnSeen[gz * CellField.GRID_SIZE + gx] = true;
                _columnQueue[tail++] = gz * CellField.GRID_SIZE + gx;
            }
        }

        // Breadth-first, so depth is uniform across the frontier and one counter
        // bounds the whole flood. `frontier` is where the current depth ends, and
        // the seed layer is everything queued above — which is how a step knows
        // whether it is leaving the region (eight ways) or crossing a wall's
        // thickness (four).
        int seedEnd = tail;
        int depth = 0;
        int frontier = tail;
        while (head < tail)
        {
            if (head == frontier)
            {
                depth++;
                frontier = tail;
                WallClaimDepth = depth;
                if (depth > maxDilate)
                {
                    WallClaimHitBudget = true;
                    break;
                }
            }
            int index = _columnQueue[head++];
            int cx = index % CellField.GRID_SIZE;
            int cz = index / CellField.GRID_SIZE;
            int sides = head <= seedEnd ? 8 : 4;
            for (int side = 0; side < sides; side++)
            {
                int nx = cx + NeighbourX[side];
                int nz = cz + NeighbourZ[side];
                if (nx < 0 || nz < 0 || nx >= CellField.GRID_SIZE || nz >= CellField.GRID_SIZE)
                {
                    continue;
                }
                int neighbour = nz * CellField.GRID_SIZE + nx;
                if (_columnSeen[neighbour])
                {
                    continue;
                }
                _columnSeen[neighbour] = true;
                // Another region's floor, not a wall. Never claimed.
                if (HasForeignCellInBand(field, nx, nz, bandLow, bandHigh))
                {
                    continue;
                }
                _wallClaim[neighbour] = true;
                WallColumnsClaimed++;
                // Already the far face of this wall: it cuts, but carrying the
                // flood past it is what walked the outer wall round the building.
                if (BoundsForeignSpace(field, nx, nz, bandLow, bandHigh))
                {
                    continue;
                }
                _columnQueue[tail++] = neighbour;
            }
        }
    }

    // Orthogonal offsets first, then the four diagonals — so one table serves
    // both the eight-way seed step and the four-way thickness step.
    private static readonly int[] NeighbourX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] NeighbourZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

    // Does this solid column have somebody else's space directly against it?
    // Open air outside a building counts, which is exactly what stops an outer
    // wall carrying the flood along its own length.
    private bool BoundsForeignSpace(CellField field, int gx, int gz, int bandLow, int bandHigh)
    {
        for (int side = 0; side < 4; side++)
        {
            int nx = gx + NeighbourX[side];
            int nz = gz + NeighbourZ[side];
            if (nx < 0 || nz < 0 || nx >= CellField.GRID_SIZE || nz >= CellField.GRID_SIZE)
            {
                continue;
            }
            if (HasForeignCellInBand(field, nx, nz, bandLow, bandHigh))
            {
                return true;
            }
        }
        return false;
    }

    private bool ColumnHasRegion(CellField field, int gx, int gz, int regionId)
    {
        int count = field.CountAt(gx, gz);
        for (int slot = 0; slot < count; slot++)
        {
            if (LabelAt(gx, gz, slot) == regionId)
            {
                return true;
            }
        }
        return false;
    }

    // Air at the player's own level belonging to somebody else: the far face of
    // the wall. A cell entirely above the cut plane (an attic, the hillside's own
    // sky cell) does not overlap and does not stop the flood — the wall is
    // supposed to be carried up through the cut.
    //
    // An authored Opening is NOT somebody else's air. It is a void IN a wall, and
    // it joins nothing precisely so that it can't merge the spaces either side —
    // which makes every window and doorway its own singleton region, and made the
    // flood treat each one as a far face and refuse to claim its column. The wall
    // then cut around them, leaving one full-height pillar standing per window and
    // per door. For the cut, an opening is part of the wall it was cut into.
    private bool HasForeignCellInBand(CellField field, int gx, int gz, int bandLow, int bandHigh)
    {
        int count = field.CountAt(gx, gz);
        for (int slot = 0; slot < count; slot++)
        {
            int label = LabelAt(gx, gz, slot);
            if (label == NO_LABEL || label == PlayerRegion)
            {
                continue;
            }
            Cell cell = field.CellAt(gx, gz, slot);
            if (cell.IsOpening)
            {
                continue;
            }
            if (cell.FloorY < bandHigh && cell.CeilingY > bandLow)
            {
                return true;
            }
        }
        return false;
    }

    private static int Index(int gx, int gz, int slot)
    {
        return (gz * CellField.GRID_SIZE + gx) * CellField.MAX_CELLS_PER_COLUMN + slot;
    }

    private static void Unpack(int packed, out int gx, out int gz, out int slot)
    {
        slot = packed % CellField.MAX_CELLS_PER_COLUMN;
        int column = packed / CellField.MAX_CELLS_PER_COLUMN;
        gx = column % CellField.GRID_SIZE;
        gz = column / CellField.GRID_SIZE;
    }

    private static int CeilDiv(int value, int divisor)
    {
        int q = value / divisor;
        return (value % divisor > 0) ? q + 1 : q;
    }
}
