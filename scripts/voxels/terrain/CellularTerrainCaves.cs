using System;
using System.Collections.Generic;
using Godot;

// The CELLULAR approach's carving: caves, tunnels and the air under a land
// bridge. Part of CellularTerrainGen.
//
// Everything carved by this approach is decided in BuildHeightMap and written
// into ONE bitset, which IsCarvedAt then reads. The bitset is never touched
// again after the height map is returned, which is what satisfies the hook's
// contract — fill order is not guaranteed and the mesher re-queries the same
// voxel as a neighbour, so the answer has to be the same however often it is
// asked. (The alternative the contract also allows, deriving shapes
// analytically from noise, cannot express the rules below: "this cave reaches
// daylight" and "this roof is thick enough" are properties of a whole system,
// not of one voxel.) A 288x256 world with a 40-voxel carve band costs ~360 KB
// of bits, which is cheaper than the height field beside it.
//
// The hard rules, and why each one is here:
//
//   CEILINGS on a multiple of HeightMap.LevelStep (interiorLevelStep, 4). That
//   lattice exists for exactly this — it is the world's vertical grid for
//   ENCLOSED space, shared with building floors, and the camera cutaway slices
//   everything on it. A cave ceiling off the lattice is a room the cutaway cuts
//   through at an arbitrary height. The ONE exception is the arch over an
//   entrance, which is an opening in a cliff face rather than an enclosed
//   ceiling, and is three columns deep.
//
//   FLOORS on a multiple of quantizeStep (2), plus a smooth 1-voxel swell. That
//   swell is the one place in this approach where single-voxel vertical detail
//   is wanted: a dead-flat floor reads as a corridor rather than as a cave.
//
//   FOUR VOXELS of headroom everywhere, including under the arch, after the
//   floor swell is spent.
//
//   NEVER the roof of an existing land feature, and never open to the outside
//   except at an entrance. See the caves section below for how that is enforced,
//   and for why the version that tried to enforce it with a per-column predicate
//   produced overhangs anyway.
public partial class CellularTerrainGen
{
    // The carve bitset. Indexed [(lx * sizeZ + lz) * ySpan + (wy - minY)].
    private ulong[] _carve;

    // The subset of the carve that must stay AIR even below the waterline —
    // cave interiors, which are enclosed rock on all sides with their one
    // opening above the sea. Chunk fill floods any carved voxel at or under its
    // column's waterline, and without this a passage descending below y=0 (which
    // is where most of this world's rock is) would fill to the ceiling. The air
    // under a BRIDGE deck is deliberately NOT in here: it is open to the sky,
    // and a bridge over a channel should have the channel running under it.
    private ulong[] _sealed;
    private int _carveMinY;
    private int _carveYSpan;
    private int _carveSizeX;
    private int _carveSizeZ;
    private int _carveWorldMinX;
    private int _carveWorldMinZ;

    // Diagnostics, verified as the carve is written rather than trusted. Every
    // one of these is an invariant this file promises; a number out of band in
    // the log is a bug, not a tuning problem.
    private int _caveSystems;
    private int _caveColumns;
    private long _carvedVoxels;
    private int _caveMinHeadroom = int.MaxValue;
    private int _caveMinRoofRock = int.MaxValue;
    private int _caveStalagmites;
    private int _caveDeepestFloor = int.MaxValue;
    private int _bridgeVoxels;
    private int _bridgeMinDeck = int.MaxValue;

    // Is this voxel carved out despite sitting at or below its column's solid
    // height? A pure read of a bitset that was complete before the first chunk
    // existed — see the file header. `columnSolidHeight` is deliberately unused:
    // the bitset was built against the same height field, so a bit is only ever
    // set below it.
    public bool IsCarvedAt(int wx, int wy, int wz, int columnSolidHeight)
    {
        if (_carve == null) { return false; }
        int lx = wx - _carveWorldMinX;
        int lz = wz - _carveWorldMinZ;
        int ly = wy - _carveMinY;
        if (lx < 0 || lx >= _carveSizeX || lz < 0 || lz >= _carveSizeZ) { return false; }
        if (ly < 0 || ly >= _carveYSpan) { return false; }
        long bit = ((long)lx * _carveSizeZ + lz) * _carveYSpan + ly;
        return (_carve[bit >> 6] & (1UL << (int)(bit & 63))) != 0UL;
    }

    // Must this carved voxel be left dry, even though it sits at or below its
    // column's waterline? True for cave interiors, false for the air under a
    // bridge — see the _sealed field.
    public bool IsSealedFromWaterAt(int wx, int wy, int wz)
    {
        if (_sealed == null) { return false; }
        int lx = wx - _carveWorldMinX;
        int lz = wz - _carveWorldMinZ;
        int ly = wy - _carveMinY;
        if (lx < 0 || lx >= _carveSizeX || lz < 0 || lz >= _carveSizeZ) { return false; }
        if (ly < 0 || ly >= _carveYSpan) { return false; }
        long bit = ((long)lx * _carveSizeZ + lz) * _carveYSpan + ly;
        return (_sealed[bit >> 6] & (1UL << (int)(bit & 63))) != 0UL;
    }

    // Nothing needs the finished voxel grid to be CARVED: every volume this
    // approach hollows is known from the height field alone, so it all goes
    // through IsCarvedAt. What the finished grid is good for is checking that it
    // worked, which is what this does instead.
    //
    // Worth the pass because the failure it catches is silent. A carved voxel at
    // or below its column's waterline is turned into WATER by chunk fill, not
    // left as air, so a cave that ran under a river comes out flooded to the
    // ceiling with nothing in any log to say so — and "the caves are full of
    // water" is indistinguishable from "the caves did not generate" until
    // someone walks into one. Scanning the bitset against the voxels that
    // actually resulted turns that into a number.
    public void CarveVolumes(WorldState ws)
    {
        if (_carve == null) { return; }
        int air = 0;
        int seaUnderBridge = 0;
        int flooded = 0;
        int stillSolid = 0;
        for (int lx = 0; lx < _carveSizeX; lx++)
        {
            for (int lz = 0; lz < _carveSizeZ; lz++)
            {
                long baseBit = ((long)lx * _carveSizeZ + lz) * _carveYSpan;
                for (int ly = 0; ly < _carveYSpan; ly++)
                {
                    long bit = baseBit + ly;
                    if ((_carve[bit >> 6] & (1UL << (int)(bit & 63))) == 0UL) { continue; }
                    int wy = ly + _carveMinY;
                    int v = ws.GetBlockWorld(lx + _carveWorldMinX, wy, lz + _carveWorldMinZ);
                    if (v == Blocks.AirId) { air++; }
                    else if (!Blocks.IsWater(v)) { stillSolid++; }
                    // Water BELOW the global waterline is the sea running under
                    // a bridge deck, which is the whole point of spanning a
                    // channel. Water ABOVE it is an inland river or lake that
                    // got into a carve, which is the failure worth shouting
                    // about — a cave under a river fills to its ceiling and
                    // looks exactly like a cave that never generated.
                    else if (wy <= TerrainMath.SEA_LEVEL) { seaUnderBridge++; }
                    else { flooded++; }
                }
            }
        }
        GD.Print($"[CellularTerrain] carve check: {air} voxels open,"
            + $" {seaUnderBridge} holding sea under a deck, {stillSolid} still solid,"
            + $" {flooded} flooded above the waterline"
            + (flooded + stillSolid > 0
                ? " — BAD CARVE: something did not come out hollow"
                : ""));
    }

    private void AllocateCarveGrid(int[,] height, CellularTerrainData cd,
        int worldMinX, int worldMinZ)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int lo = int.MaxValue;
        int hi = int.MinValue;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                int h = height[lx, lz];
                if (h < lo) { lo = h; }
                if (h > hi) { hi = h; }
            }
        }

        _carveSizeX = sizeX;
        _carveSizeZ = sizeZ;
        _carveWorldMinX = worldMinX;
        _carveWorldMinZ = worldMinZ;
        // The band reaches BELOW the lowest terrain column, because caverns sit
        // in the solid rock FitVerticalExtent keeps under the world
        // (undergroundDepthVoxels). Sizing it to the height field alone would
        // put every deep chamber outside the grid, where SetCarved drops it
        // silently and the cave simply is not there.
        _carveMinY = lo - Math.Max(0, cd.undergroundDepthVoxels);
        _carveYSpan = Math.Max(1, hi - _carveMinY + 1);
        long bits = (long)sizeX * sizeZ * _carveYSpan;
        _carve = new ulong[(bits + 63) / 64];
        _sealed = new ulong[(bits + 63) / 64];
    }

    private void SetSealed(int lx, int lz, int wy)
    {
        int ly = wy - _carveMinY;
        if (ly < 0 || ly >= _carveYSpan) { return; }
        long bit = ((long)lx * _carveSizeZ + lz) * _carveYSpan + ly;
        _sealed[bit >> 6] |= 1UL << (int)(bit & 63);
    }

    private void SetCarved(int lx, int lz, int wy)
    {
        int ly = wy - _carveMinY;
        if (ly < 0 || ly >= _carveYSpan) { return; }
        long bit = ((long)lx * _carveSizeZ + lz) * _carveYSpan + ly;
        ulong mask = 1UL << (int)(bit & 63);
        // Counted only when the bit is NEWLY set, so the totals in the log
        // reconcile with the carve check's scan. Two ribbons can overlap, and
        // counting every call double-counts the shared voxels — a small enough
        // discrepancy to look like a rounding artefact and waste an hour.
        if ((_carve[bit >> 6] & mask) != 0UL) { return; }
        _carve[bit >> 6] |= mask;
        _carvedVoxels++;
    }

    // ------------------------------------------------------------ land bridges

    // Ground level under each bridge column before its deck was stamped, or
    // int.MinValue where no deck covers it. Written by BuildLandBridges and read
    // by CarveUnderBridges — the height field itself cannot answer it after the
    // stamp, since the stamp is what overwrote it.
    private int[,] _bridgeGroundUnder;

    // Hollow out under each bridge deck. The deck itself was written into Height
    // by BuildLandBridges; this is the other half of it — without the air below,
    // a "bridge" is a solid causeway filling the gap it was meant to span.
    //
    // The slab left under the walking surface is bridgeThickness voxels and is
    // never allowed to round down to one. A one-voxel deck is the plateau
    // approach's floating-slab failure: a roof with nothing holding it up, which
    // the mesher draws as a sheet of paper over a void.
    private void CarveUnderBridges(List<BridgeRibbon> ribbons, int[,] height, CellularTerrainData cd,
        int worldMinX, int worldMinZ)
    {
        if (ribbons.Count == 0) { return; }
        int thickness = Math.Max(2, cd.bridgeThickness);
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);

        long before = _carvedVoxels;

        // The ground the decks were stamped over is gone from `height` by now —
        // it holds the deck. Re-deriving it is not possible, so each ribbon
        // carves against the ORIGINAL ground recorded when the deck went down.
        foreach (BridgeRibbon r in ribbons)
        {
            ForEachRibbonColumn(r, sizeX, sizeZ, worldMinX, worldMinZ, (lx, lz, i) =>
            {
                int under = _bridgeGroundUnder[lx, lz];
                if (under == int.MinValue) { return; }
                // Per column, not per ribbon: the deck arches, so the slab hangs
                // lower over the crown than it does at the abutments.
                int deckY = r.Deck[i];
                int top = deckY - thickness;
                if (top <= under) { return; }
                for (int wy = under + 1; wy <= top; wy++) { SetCarved(lx, lz, wy); }
                int deck = deckY - top;
                if (deck < _bridgeMinDeck) { _bridgeMinDeck = deck; }
            });
            // One slice per bridge as well as per cave mouth: the deck and the
            // air under it are the only place in this world where a column is
            // solid, hollow and solid again, and that is precisely what a
            // heightfield dump cannot show.
            _sliceRows.Add(Math.Clamp(Mathf.RoundToInt((r.Z0 + r.Z1) * 0.5f) - worldMinZ, 0, sizeZ - 1));
        }
        _bridgeVoxels = (int)(_carvedVoxels - before);
        GD.Print($"[CellularTerrain] bridge carve: {_bridgeVoxels} voxels under"
            + $" {ribbons.Count} decks, thinnest deck"
            + $" {(_bridgeMinDeck == int.MaxValue ? 0 : _bridgeMinDeck)}v (floor 2v)");
    }

    // ------------------------------------------------------------------ caves
    //
    // THREE kinds of space, built separately, because they answer to different
    // rules — and separating them is what stopped the overhangs.
    //
    //   ENTRANCE — the ONE place a cave is allowed to open to the outside world.
    //              A short arched mouth cut through an existing cell wall.
    //   CAVERN   — an underground chamber. Enclosed on every side.
    //   TUNNEL   — a wandering passage joining an entrance to a cavern, or one
    //              cavern to another. Enclosed everywhere.
    //
    // The earlier version had no such distinction. ONE flood grew outward from
    // the mouth over any column with roof rock above it, and lateral enclosure
    // was a per-column PREDICATE judging each candidate against a PREDICTED
    // level for its neighbours. Both halves of that were wrong: a prediction is
    // not the geometry that ends up built, and the mouth exemption applied to a
    // RADIUS rather than to an identified doorway. So wherever the flood ran
    // along a narrow spine of high ground it opened to the air on both sides at
    // once and left the rock above standing as a slab over open ground.
    //
    // The fix is to stop predicting and start VERIFYING. Everything is claimed
    // first, then a fixpoint deletes any column with a carved voxel facing open
    // air unless that column belongs to a doorway, and repeats — deleting a
    // column exposes its neighbour, so one pass is not enough. What survives is
    // enclosed because it was checked, not because it was argued. A flood from
    // the entrances then throws away whatever they cannot reach: a sealed
    // chamber the player can never find is spent budget, not content.

    // A claimed column's air span: solid at Floor, solid at Ceiling, air between.
    // int.MinValue in _caveFloor means "not claimed".
    private int[,] _caveFloor;
    private int[,] _caveCeiling;

    // Columns belonging to a doorway — the only ones allowed to face open air.
    private bool[,] _caveDoorway;

    // Columns on a level-change ramp — the only interior ground allowed off the
    // interior lattice.
    private bool[,] _caveRamp;

    private int _caveDoorwayOffLattice;
    private int _caveRampColumns;
    private int _caveStrayOffLattice;

    // Headroom every cave owes the player, in voxels. Not authored: below this a
    // passage stops being somewhere you can walk and turns into a crawl the
    // camera cannot follow, so it is a floor on the tuning rather than part of
    // it. caveClearance, caveFloorPathRise and the mouth arch are all clamped
    // against it.
    private const int MIN_CAVE_HEADROOM = 4;

    // One mouth: the buried column just inside a cell wall, the level it opens
    // at, and which way is inward.
    private struct CaveEntrance
    {
        public int Lx, Lz;
        public int Floor;
        public int Ceiling;
        public int Dx, Dz;      // unit step from the outside column to the inside one
        public int InnerLx, InnerLz;
    }


    private void BuildCaves(int[,] height, int[,] water, bool[,] noCarve, CellularTerrainData cd,
        int worldMinX, int worldMinZ)
    {
        if (cd.caveSystemCount <= 0) { return; }
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int levelStep = Math.Max(1, cd.interiorLevelStep);
        int quantize = Math.Max(1, cd.quantizeStep);
        int clearance = Math.Max(MIN_CAVE_HEADROOM + 1, cd.caveClearance);
        int roofRock = Math.Max(1, cd.caveRoofRock);

        // Deepest a floor may go. FitVerticalExtent gives the world
        // undergroundDepthVoxels of solid rock below its lowest column, and a
        // chamber that ran into the bottom of that would open into the void
        // under the world.
        int lowestGround = int.MaxValue;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                if (height[lx, lz] < lowestGround) { lowestGround = height[lx, lz]; }
            }
        }
        int floorLimit = lowestGround - Math.Max(0, cd.undergroundDepthVoxels) + roofRock;

        _caveFloor = new int[sizeX, sizeZ];
        _caveCeiling = new int[sizeX, sizeZ];
        _caveDoorway = new bool[sizeX, sizeZ];
        _caveRamp = new bool[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++) { _caveFloor[lx, lz] = int.MinValue; }
        }

        var wander = TerrainMath.MakePerlin(TerrainMath.DeriveSeed(_worldSeed, SEED_SALT_CAVE),
            cd.caveWanderFrequency, 2);

        List<CaveEntrance> candidates = FindEntrances(height, water, noCarve, cd,
            clearance, roofRock, levelStep, quantize, out int candidateMouths);
        if (candidates.Count == 0)
        {
            GD.Print($"[CellularTerrain] caves: 0/{cd.caveSystemCount} — no cell wall in the world"
                + $" is tall enough to hold a {clearance}v mouth under {roofRock}v of roof."
                + " Lower caveClearance or caveRoofRock, or raise maxCellStep.");
            return;
        }

        // Selection and the porch test are ONE loop, and separating them was a
        // real bug: picking caveSystemCount candidates by spacing first and
        // testing them afterwards meant four tries out of sixty-one, and when
        // all four happened to be slots the world came out with no caves at all
        // while the log insisted no mouth in it could ever work. A candidate now
        // only spends a slot if its porch actually reached enclosed rock.
        float spacing = Math.Max(1f, cd.caveEntranceSpacing);
        var entrances = new List<CaveEntrance>();
        int slotRejects = 0;
        foreach (CaveEntrance candidate in candidates)
        {
            if (entrances.Count >= cd.caveSystemCount) { break; }
            bool clear = true;
            foreach (CaveEntrance other in entrances)
            {
                float dx = other.Lx - candidate.Lx;
                float dz = other.Lz - candidate.Lz;
                if (dx * dx + dz * dz < spacing * spacing) { clear = false; break; }
            }
            if (!clear) { continue; }

            CaveEntrance e = candidate;
            if (ClaimEntrance(ref e, height, noCarve, cd, roofRock)) { entrances.Add(e); }
            else { slotRejects++; }
        }
        if (entrances.Count == 0)
        {
            GD.Print($"[CellularTerrain] caves: 0 — all {candidateMouths} candidate mouths were"
                + " slots whose rock never closed in behind them. Raise caveMouthMaxDepth, or"
                + " caveClearance is too tall for the walls this world has.");
            return;
        }
        // ONE short local system per entrance, and nothing joins them up.
        int levelsDropped = 0;
        foreach (CaveEntrance e in entrances)
        {
            levelsDropped += GrowSystem(e, height, water, noCarve, wander, cd,
                clearance, roofRock, levelStep, floorLimit, worldMinX, worldMinZ);
        }

        int exposed = SealAgainstDaylight(height);
        int unreachable = KeepOnlyWhatAnEntranceReaches(entrances);
        EmitCaves(height, cd, worldMinX, worldMinZ);

        foreach (CaveEntrance e in entrances)
        {
            if (_caveFloor[e.Lx, e.Lz] != int.MinValue) { _sliceRows.Add(e.Lz); }
        }

        GD.Print($"[CellularTerrain] caves: {entrances.Count}/{cd.caveSystemCount} systems (of"
            + $" {candidateMouths} candidate mouths, {slotRejects} rejected as slots),"
            + $" {levelsDropped} entry ramps -> {_caveColumns} columns,"
            + $" {_carvedVoxels - _bridgeVoxels} voxels;"
            + $" floors and ceilings on the {levelStep}v lattice (arch at the mouth, ramp at the"
            + $" entry drop); min headroom"
            + $" {(_caveMinHeadroom == int.MaxValue ? 0 : _caveMinHeadroom)}v"
            + $" (floor {MIN_CAVE_HEADROOM}v), min roof rock"
            + $" {(_caveMinRoofRock == int.MaxValue ? 0 : _caveMinRoofRock)}v (floor {roofRock}v);"
            + $" deepest floor y={(_caveDeepestFloor == int.MaxValue ? 0 : _caveDeepestFloor)}"
            + $" (waterline {TerrainMath.SEA_LEVEL}, world floor {floorLimit});"
            + $" off-lattice floors: {_caveRampColumns} ramp + {_caveDoorwayOffLattice} doorway"
            + $" (both allowed), {_caveStrayOffLattice} STRAY (must be 0);"
            + $" enclosure check removed {exposed} columns open to daylight and"
            + $" {unreachable} no entrance could reach; {_caveStalagmites} stalagmites."
            + $" Passage width (nearest wall, doubled): {MeasureWidths()}");
        _caveSystems = entrances.Count;
    }

    // Lowest ceiling on the interior lattice that still clears the floor by
    // `clearance`. The lattice is HeightMap.LevelStep — the same grid building
    // floors sit on, so the camera cutaway slices caves and rooms alike.
    private static int CeilingFor(int floor, int clearance, int levelStep)
    {
        int want = floor + clearance;
        int q = want >= 0
            ? (want + levelStep - 1) / levelStep
            : -((-want) / levelStep);
        return q * levelStep;
    }

    // Highest lattice ceiling that still leaves `roofRock` of ground above it.
    private static int CeilingUnder(int ground, int roofRock, int levelStep)
    {
        int want = ground - roofRock;
        int q = want >= 0 ? want / levelStep : -((-want + levelStep - 1) / levelStep);
        return q * levelStep;
    }

    // Floor for a given ceiling, on the SAME lattice. Cave floors sit on
    // interiorLevelStep rather than on quantizeStep — the 4 m grid building
    // floors use, so the camera cutaway slices a cave and a room at the same
    // heights. Both surfaces on the 4 lattice forces the gap to a multiple of 4,
    // and the smallest one that clears the clearance is 8, so headroom is a
    // consistent 7 m everywhere instead of alternating 5 and 7.
    private static int FloorUnderCeiling(int ceiling, int clearance, int levelStep)
    {
        int levels = (clearance + levelStep - 1) / levelStep;
        return ceiling - Math.Max(1, levels) * levelStep;
    }

    // Round a level onto the interior lattice, away from zero downward.
    private static int SnapToLevel(int y, int levelStep)
    {
        int q = y >= 0 ? y / levelStep : -((-y + levelStep - 1) / levelStep);
        return q * levelStep;
    }

    // May a cave with this ceiling claim this column? The roof rule, plus the
    // one thing that would flood the result.
    private static bool RoofHolds(int lx, int lz, int ceiling, int roofRock,
        int[,] height, int[,] water, bool[,] noCarve)
    {
        if (noCarve[lx, lz]) { return false; }
        // Inland water above the cave. A river or lake column is left alone even
        // though sealing would keep the cave dry: the roof under a lake bed is
        // only caveRoofRock of rock holding standing water up, and that is a
        // thinner margin than this pass wants to promise.
        if (water != null && water[lx, lz] != HeightMap.NoWater) { return false; }
        // The rule the plateau approach broke. Ground has to stand a full roof
        // above the ceiling, so no cave ever surfaces as an open pit and no
        // surface detail on top of it pokes through.
        return height[lx, lz] >= ceiling + roofRock;
    }

    // FIRST claim wins. A column holds exactly one air span here, so two spaces
    // meeting in one column have to resolve to one — and taking min(floor) with
    // max(ceiling) is the wrong resolution, however natural it looks. Measured:
    // where a mouth at y=15 shared columns with a tunnel at y=-15 the merge
    // produced a single 31-voxel shaft joining them, against a normal span of 3
    // to 9. Keeping the earlier claim instead leaves the two spaces separate,
    // and DigTunnel refuses to route through a column already claimed at a level
    // it cannot walk into, so the tunnel goes round rather than through.
    //
    // Claim order is entrances, then caverns, then tunnels — most fixed first.
    private void Claim(int lx, int lz, int floor, int ceiling, bool doorway)
    {
        if (_caveFloor[lx, lz] == int.MinValue)
        {
            _caveFloor[lx, lz] = floor;
            _caveCeiling[lx, lz] = ceiling;
        }
        if (doorway) { _caveDoorway[lx, lz] = true; }
    }

    // Can a space at this floor level use a column that may already be claimed?
    // Either it is free, or what is there is close enough in level to walk
    // between — which is what makes a tunnel arriving at a chamber join it
    // rather than stop one column short.
    private bool LevelCompatible(int lx, int lz, int floor, int quantize)
    {
        int had = _caveFloor[lx, lz];
        return had == int.MinValue || Math.Abs(had - floor) <= quantize;
    }

    // ---- reservation

    // Mark the ground a cave mouth will need, BEFORE the cliff erosion runs.
    //
    // The two features compete for the same scarce terrain and it took a
    // measurement to see it. A mouth needs an adjacent pair whose inside column
    // stands a full 12 voxels above the outside one — the ceiling is forced to
    // floor + 8 by the interior lattice, plus caveRoofRock over it — which is
    // exactly maxCellStep, the tallest wall this world is allowed to build. The
    // inside column of such a pair IS the lip of that cliff, and cutting lips is
    // precisely what erosion does. Measured: candidate mouths fell from 80 to 5
    // once erosion was turned on, and it was the candidate test that collapsed,
    // not the porch test — 0 rejections against 1.
    //
    // So the sites are found first and their porch footprints are held back from
    // the cut. Erosion still runs everywhere else; the caves simply get first
    // claim on the handful of walls tall enough to hold them.
    //
    // Deliberately OVER-reserves: the real selection later re-scans against the
    // finished heights and the water pass, so a site can still be lost to a
    // river or fail its porch test. Holding a few spare walls costs a few
    // hundred columns of erosion and stops one unlucky site from taking a cave
    // with it.
    private bool[,] ReserveCaveEntrances(int[,] height, bool[,] noCarve, CellularTerrainData cd,
        out int reserved)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        var mask = new bool[sizeX, sizeZ];
        reserved = 0;
        if (cd.caveSystemCount <= 0) { return mask; }

        int levelStep = Math.Max(1, cd.interiorLevelStep);
        int quantize = Math.Max(1, cd.quantizeStep);
        int clearance = Math.Max(MIN_CAVE_HEADROOM + 1, cd.caveClearance);
        int roofRock = Math.Max(1, cd.caveRoofRock);

        // No water yet — the river pass has not run at this point in the
        // pipeline. Passing null makes RoofHolds skip the water test, which is
        // the right call for a reservation: it costs a few extra held columns
        // and never loses a site that would have worked.
        List<CaveEntrance> candidates = FindEntrances(height, null, noCarve, cd,
            clearance, roofRock, levelStep, quantize, out _);
        if (candidates.Count == 0) { return mask; }

        float spacing = Math.Max(1f, cd.caveEntranceSpacing);
        int want = cd.caveSystemCount * RESERVE_OVERSAMPLE;
        int depth = Math.Max(1, cd.caveMouthMaxDepth);
        int half = Mathf.CeilToInt(cd.caveMouthWidth) + 1;
        var taken = new List<CaveEntrance>();

        foreach (CaveEntrance e in candidates)
        {
            if (taken.Count >= want) { break; }
            bool clear = true;
            foreach (CaveEntrance other in taken)
            {
                float dx = other.Lx - e.Lx;
                float dz = other.Lz - e.Lz;
                if (dx * dx + dz * dz < spacing * spacing) { clear = false; break; }
            }
            if (!clear) { continue; }
            taken.Add(e);

            // The porch's own swept footprint, plus a column of margin: inward
            // along the entry direction, and either side of its centre line.
            int px = -e.Dz;
            int pz = e.Dx;
            for (int along = -1; along <= depth; along++)
            {
                for (int side = -half; side <= half; side++)
                {
                    int lx = e.Lx + e.Dx * along + px * side;
                    int lz = e.Lz + e.Dz * along + pz * side;
                    if (lx < 0 || lx >= sizeX || lz < 0 || lz >= sizeZ) { continue; }
                    if (mask[lx, lz]) { continue; }
                    mask[lx, lz] = true;
                    reserved++;
                }
            }
        }
        _reservedSites = taken.Count;
        return mask;
    }

    // Sites held back per system wanted. Three gives the later, stricter
    // selection spares to fall back on without holding much ground.
    private const int RESERVE_OVERSAMPLE = 3;

    private int _reservedSites;

    // ---- entrances

    // Candidate mouths are adjacent column pairs where the ground drops far
    // enough in ONE column to hold a mouth and its roof — that is, an existing
    // cell wall. Cell tops are flat and their borders vertical, so there is no
    // intermediate column at the wall and no thin-roof band to cross: the
    // outside column's ground is at the mouth floor, and the inside column's is
    // a full roof above the ceiling.
    private List<CaveEntrance> FindEntrances(int[,] height, int[,] water, bool[,] noCarve,
        CellularTerrainData cd, int clearance, int roofRock, int levelStep, int quantize,
        out int candidates)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        var found = new List<CaveEntrance>();
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                for (int d = 0; d < 4; d++)
                {
                    int nx = lx + NEIGHBOUR_DX[d];
                    int nz = lz + NEIGHBOUR_DZ[d];
                    if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }

                    // The OUTSIDE column sets the floor: the mouth comes out
                    // level with the ground in front of it, so the player walks
                    // straight in off the terrace below the wall.
                    int floor = height[nx, nz];
                    // The DOORWAY must be above the waterline, even though the
                    // caverns behind it are free to sit well below one. A mouth
                    // at or under the sea is an opening the water pours through,
                    // and it would flood the system back up to the waterline
                    // however well sealed the rest of it is.
                    if (floor < TerrainMath.SEA_LEVEL) { continue; }
                    // On the INTERIOR lattice, and this is the load-bearing
                    // line for the whole "cave floors are at 4 m elevations"
                    // rule. A system takes its level from its mouth, and the
                    // mouth takes its floor from the terrace outside — which
                    // sits on quantizeStep, so half of them are a full 2 voxels
                    // off the interior grid. Letting those through put EVERY
                    // column of those systems off-lattice: 527 of 568 in one
                    // run, which is the player standing at y=-13 in a long
                    // tunnel that should have been at -11 or -15.
                    //
                    // Terraces on 4 are half of all terraces, so this costs
                    // candidate mouths and buys the invariant outright. Nothing
                    // downstream has to snap, round or compensate.
                    if (floor % levelStep != 0) { continue; }

                    int ceiling = CeilingFor(floor, clearance, levelStep);
                    if (!RoofHolds(lx, lz, ceiling, roofRock, height, water, noCarve)) { continue; }
                    // The outside column must be clear too — a river running
                    // along the foot of the wall would pour into the mouth.
                    if (water != null && water[nx, nz] != HeightMap.NoWater) { continue; }
                    if (noCarve[nx, nz]) { continue; }

                    found.Add(new CaveEntrance
                    {
                        Lx = lx, Lz = lz, Floor = floor, Ceiling = ceiling,
                        Dx = lx - nx, Dz = lz - nz, InnerLx = lx, InnerLz = lz,
                    });
                }
            }
        }
        candidates = found.Count;
        if (found.Count == 0) { return found; }

        // LOWEST floor first, then hashed within a level.
        //
        // The floor comes from the ground outside the wall, and it decides
        // everything downstream: the ceiling sits a clearance above it, so a
        // mouth on a high terrace needs ground above it and walls beside it that
        // only the tallest handful of columns in the world can offer, and no
        // tunnel can leave it. Measured: with a pure hash ordering the surviving
        // entrance sat at floor 8, needed ground of 20 to route out of, and
        // reached nothing. Low mouths have the most rock over them — and a cave
        // opening at the foot of a cliff is where one belongs anyway.
        found.Sort((a, b) =>
        {
            int cmp = a.Floor.CompareTo(b.Floor);
            if (cmp != 0) { return cmp; }
            int ha = Hash(a.Lx, a.Lz, _worldSeed + SEED_SALT_CAVE);
            int hb = Hash(b.Lx, b.Lz, _worldSeed + SEED_SALT_CAVE);
            cmp = ha.CompareTo(hb);
            if (cmp != 0) { return cmp; }
            cmp = a.Lx.CompareTo(b.Lx);
            return cmp != 0 ? cmp : a.Lz.CompareTo(b.Lz);
        });

        return found;
    }

    // Cut the mouth: a short passage running inward from the cell wall, with an
    // ARCHED roof.
    //
    // The arch is the one place a ceiling leaves the interior lattice, and it is
    // deliberate. That lattice exists so the camera cutaway slices enclosed
    // rooms at shared heights; a mouth is an opening in a cliff face, three
    // columns deep, and a flat-topped rectangular hole in a cliff reads as a
    // slab with a gap under it rather than as a cave. The arch rise is clamped
    // so the shortest part of the opening still clears MIN_CAVE_HEADROOM — the
    // sides round off by going SOLID, not by going low.
    // The porch runs inward until the ROCK CLOSES IN, not for a fixed number of
    // columns, and that is the difference between a mouth and a hole.
    //
    // A fixed-depth doorway leaves the first column BEHIND it enclosed only if
    // the cliff face happens to be straight there. Where it is not, that column
    // faces daylight, the enclosure check deletes it — correctly — and the
    // tunnel behind it is severed from its own entrance. Measured: four columns
    // deleted that way left 3338 columns of cavern with no way in, and the
    // reachability pass then threw all of it away.
    //
    // So the porch is extended while the surrounding ground is still below the
    // ceiling, and the tunnel starts where it stops. An entrance whose rock
    // never closes in within the cap is DROPPED, which is exactly the case worth
    // dropping: it is a slot through a spine, and roofing one is how the old
    // version made overhangs.
    private bool ClaimEntrance(ref CaveEntrance e, int[,] height, bool[,] noCarve,
        CellularTerrainData cd, int roofRock)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        float halfWidth = Math.Max(1f, cd.caveMouthWidth);
        int maxDepth = Math.Max(1, cd.caveMouthMaxDepth);
        int fullHeadroom = e.Ceiling - e.Floor - 1;
        int rise = Math.Clamp(cd.caveMouthArchRise, 0, Math.Max(0, fullHeadroom - MIN_CAVE_HEADROOM));

        // Across the doorway, not along it: the arch is a shape you see in the
        // cliff face, so it varies with the LATERAL offset from the centre line
        // and is constant with depth.
        int px = -e.Dz;
        int pz = e.Dx;
        int reach = Mathf.CeilToInt(halfWidth);

        int interiorCeiling = e.Ceiling;
        // The SAME radius the tunnel router demands, and it has to be the same
        // or the porch stops one column short of anywhere a tunnel may begin:
        // the low ground in front of the mouth is still inside the router's
        // wall disc, so its first step fails and no entrance ever reaches a
        // cavern. Measured — that is exactly what 0/2 looked like.
        const int wall = 1;
        bool Enclosed(int cx, int cz)
        {
            for (int dx = -wall; dx <= wall; dx++)
            {
                for (int dz = -wall; dz <= wall; dz++)
                {
                    if (dx * dx + dz * dz > wall * wall) { continue; }
                    int nx = cx + dx;
                    int nz = cz + dz;
                    if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                    if (height[nx, nz] < interiorCeiling) { return false; }
                }
            }
            return true;
        }

        int depth = 0;
        while (depth < maxDepth)
        {
            int cx = e.Lx + e.Dx * depth;
            int cz = e.Lz + e.Dz * depth;
            if (cx < 0 || cx >= sizeX || cz < 0 || cz >= sizeZ) { return false; }
            if (height[cx, cz] < interiorCeiling + roofRock || noCarve[cx, cz]) { return false; }
            depth++;
            // One column PAST the point the rock closes in, so the tunnel
            // starting there is walled rather than merely on the boundary.
            if (Enclosed(cx, cz)) { break; }
        }
        if (depth >= maxDepth) { return false; }

        for (int along = 0; along < depth; along++)
        {
            for (int side = -reach; side <= reach; side++)
            {
                int lx = e.Lx + e.Dx * along + px * side;
                int lz = e.Lz + e.Dz * along + pz * side;
                if (lx < 0 || lx >= sizeX || lz < 0 || lz >= sizeZ) { continue; }

                float t = Math.Abs(side) / halfWidth;
                if (t > 1f) { continue; }
                // Circular profile: full height on the centre line, dropping to
                // the full rise at the jambs. The sides round off by going
                // SOLID, not by going low — a column whose arch would leave less
                // than four voxels of headroom is simply not carved.
                int drop = Mathf.RoundToInt(rise * (1f - Mathf.Sqrt(Math.Max(0f, 1f - t * t))));
                int ceiling = e.Ceiling - drop;
                if (ceiling - e.Floor - 1 < MIN_CAVE_HEADROOM) { continue; }
                if (noCarve[lx, lz]) { continue; }
                // The roof over the arch is measured against the arch's OWN
                // ceiling, which is lower than the interior one — so the jambs
                // are allowed under slightly shallower ground, which is exactly
                // where a mouth sits.
                if (height[lx, lz] < ceiling + roofRock) { continue; }
                Claim(lx, lz, e.Floor, ceiling, doorway: true);
            }
        }
        e.InnerLx = e.Lx + e.Dx * (depth - 1);
        e.InnerLz = e.Lz + e.Dz * (depth - 1);
        return true;
    }

    // ---- the system behind a mouth

    // ONE short, LOCAL system per entrance, grown as a bounded flood at a
    // SINGLE lattice level. Systems are never joined to each other.
    //
    // This replaced a design of separately-placed chambers linked by A*-routed
    // tunnels, and the simpler shape is not a compromise — the routed version
    // was wrong in ways that kept coming back. A tunnel spanning a hundred
    // metres between two chambers has to change level on the way, every level
    // change is a ramp, and fitting those ramps to a route's length is what put
    // floors off the lattice again and again. Nothing here changes level at all
    // once it is past the entrance, so "floors sit on interiorLevelStep" is true
    // by construction rather than by arithmetic that has to be got right.
    //
    // Systems MAY overlap: two floods that meet simply share their columns, and
    // where they are at different levels the first claim wins and the second
    // flows around. Overlap is harmless because the only thing that actually has
    // to hold is that nothing opens to the sky, and SealAgainstDaylight checks
    // that over the finished claim regardless of who claimed what.
    //
    // Returns 1 if the system dropped a level on the way in, 0 if it is flat.
    private int GrowSystem(CaveEntrance e, int[,] height, int[,] water, bool[,] noCarve,
        FastNoiseLite wander, CellularTerrainData cd, int clearance, int roofRock,
        int levelStep, int floorLimit, int worldMinX, int worldMinZ)
    {
        // How far in the system sits below its own doorway. Tried DEEPEST
        // first: dropping is what puts a cave under real rock rather than just
        // inside the cliff face, and it is also what lets one reach below the
        // waterline in a world whose land is barely twenty voxels tall. A drop
        // that will not fit falls back to the next, and 0 is always legal.
        for (int drop = Math.Max(0, cd.caveDescentLevels); drop >= 0; drop--)
        {
            int level = e.Floor - drop * levelStep;
            int startX = e.InnerLx;
            int startZ = e.InnerLz;
            if (drop > 0)
            {
                if (level < floorLimit) { continue; }
                if (!DigEntryRamp(e, level, height, water, noCarve, cd, clearance, roofRock,
                        levelStep, out startX, out startZ))
                {
                    continue;
                }
            }
            int claimed = FloodSystem(startX, startZ, level, height, water, noCarve, wander, cd,
                clearance, roofRock, levelStep, worldMinX, worldMinZ);
            if (claimed > 0 || drop == 0) { return drop > 0 ? 1 : 0; }
        }
        return 0;
    }

    // The one ramp a system is allowed: a short straight pitch running inward
    // from the porch, dropping to the system's level a voxel at a time.
    //
    // It is the only ground behind the doorway that leaves the lattice, and it
    // is deliberately kept at the entry. A level change in the middle of a cave
    // is a step the player meets with no warning; a ramp just inside the mouth
    // reads as the way down.
    private bool DigEntryRamp(CaveEntrance e, int level, int[,] height, int[,] water,
        bool[,] noCarve, CellularTerrainData cd, int clearance, int roofRock, int levelStep,
        out int endX, out int endZ)
    {
        endX = e.InnerLx;
        endZ = e.InnerLz;
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int total = e.Floor - level;
        if (total <= 0) { return true; }
        float grade = Math.Max(0.05f, cd.caveDescentGrade);
        int run = Math.Max(total, Mathf.CeilToInt(total / grade));
        int half = Math.Max(1, cd.caveWidth / 2);
        int px = -e.Dz;
        int pz = e.Dx;

        // Walked TWICE: once to prove the whole pitch fits, and only then to
        // claim it. Claiming as it goes and giving up halfway leaves a ramp
        // ending in a wall, which is worse than no ramp at all.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 1; i <= run; i++)
            {
                int floor = e.Floor - Math.Min(total, Mathf.RoundToInt((float)i / run * total));
                int ceiling = CeilingFor(floor, clearance, levelStep);
                int cx = e.InnerLx + e.Dx * i;
                int cz = e.InnerLz + e.Dz * i;
                for (int side = -half; side <= half; side++)
                {
                    int lx = cx + px * side;
                    int lz = cz + pz * side;
                    if (lx < 0 || lx >= sizeX || lz < 0 || lz >= sizeZ) { return false; }
                    if (!RoofHolds(lx, lz, ceiling, roofRock, height, water, noCarve))
                    {
                        return false;
                    }
                    if (pass == 1)
                    {
                        Claim(lx, lz, floor, ceiling, doorway: false);
                        if (floor % levelStep != 0) { _caveRamp[lx, lz] = true; }
                    }
                }
                if (pass == 1 && i == run) { endX = cx; endZ = cz; }
            }
        }
        return true;
    }

    // Fill the space behind the entrance: a bounded flood at ONE level over
    // every column with enough rock over it, cheapest-first with a smooth
    // wander field folded into the cost.
    //
    // A flood rather than a routed path, and that is what fixes the width. A
    // path widened by a disc is only ever as wide as the disc, and the enclosure
    // check then trims its edges back further — which is why tunnels kept coming
    // out narrow however large the half-width was set. A flood takes every
    // column the rock allows instead, so a passage is as wide as the ground it
    // runs through and opens into a chamber wherever the rock does.
    // `caveOpenness` blends the cost between pure distance (one round chamber)
    // and mostly wander (a warren of passages between wider lobes).
    private int FloodSystem(int startX, int startZ, int level, int[,] height, int[,] water,
        bool[,] noCarve, FastNoiseLite wander, CellularTerrainData cd, int clearance,
        int roofRock, int levelStep, int worldMinX, int worldMinZ)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int ceiling = CeilingFor(level, clearance, levelStep);
        float reach = Math.Max(1f, cd.caveReach);
        float wanderScale = (1f - Math.Clamp(cd.caveOpenness, 0f, 1f)) * reach;
        int budget = Math.Max(1, cd.caveMaxColumns);

        if (startX < 0 || startX >= sizeX || startZ < 0 || startZ >= sizeZ) { return 0; }
        if (!RoofHolds(startX, startZ, ceiling, roofRock, height, water, noCarve)) { return 0; }

        var visited = new HashSet<int>();
        var open = new PriorityQueue<int, (float, int)>();
        int start = startX * sizeZ + startZ;
        open.Enqueue(start, (0f, start));
        visited.Add(start);
        int claimed = 0;

        while (open.TryDequeue(out int cur, out _) && claimed < budget)
        {
            int lx = cur / sizeZ;
            int lz = cur % sizeZ;
            Claim(lx, lz, level, ceiling, doorway: false);
            claimed++;

            for (int d = 0; d < 4; d++)
            {
                int nx = lx + NEIGHBOUR_DX[d];
                int nz = lz + NEIGHBOUR_DZ[d];
                if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                int nIdx = nx * sizeZ + nz;
                if (!visited.Add(nIdx)) { continue; }
                if (!RoofHolds(nx, nz, ceiling, roofRock, height, water, noCarve)) { continue; }

                float dx = nx - startX;
                float dz = nz - startZ;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > reach) { continue; }
                float w = 0.5f * (wander.GetNoise2D(nx + worldMinX, nz + worldMinZ) + 1f);
                open.Enqueue(nIdx, (dist + wanderScale * w, nIdx));
            }
        }
        return claimed;
    }


    // ---- verification

    // Delete every claimed column with a carved voxel facing OPEN AIR, unless it
    // is part of a doorway. Repeated to a fixpoint, because removing a column
    // exposes whatever was behind it.
    //
    // This is the check that replaced a per-column predicate, and the difference
    // is the whole point: the predicate asked "would a neighbour be eligible at
    // its predicted level", which is a guess about geometry that has not been
    // built yet. This asks the finished claim what is actually beside each
    // voxel. A column is walled when its neighbour is either claimed at an
    // overlapping level (interior) or solid ground at that height; anything else
    // is a hole in a cliff face, and the rock above it is an overhang.
    private int SealAgainstDaylight(int[,] height)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int removed = 0;
        var doomed = new List<int>();

        while (true)
        {
            doomed.Clear();
            for (int lx = 0; lx < sizeX; lx++)
            {
                for (int lz = 0; lz < sizeZ; lz++)
                {
                    if (_caveFloor[lx, lz] == int.MinValue || _caveDoorway[lx, lz]) { continue; }
                    if (!FacesDaylight(lx, lz, height, sizeX, sizeZ)) { continue; }
                    doomed.Add(lx * sizeZ + lz);
                }
            }
            if (doomed.Count == 0) { break; }
            foreach (int idx in doomed)
            {
                _caveFloor[idx / sizeZ, idx % sizeZ] = int.MinValue;
                removed++;
            }
        }
        return removed;
    }

    private bool FacesDaylight(int lx, int lz, int[,] height, int sizeX, int sizeZ)
    {
        int floor = _caveFloor[lx, lz];
        int ceiling = _caveCeiling[lx, lz];
        for (int d = 0; d < 4; d++)
        {
            int nx = lx + NEIGHBOUR_DX[d];
            int nz = lz + NEIGHBOUR_DZ[d];
            // The world edge is not daylight — nothing can look in from outside
            // the map.
            if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
            int ground = height[nx, nz];
            bool neighbourClaimed = _caveFloor[nx, nz] != int.MinValue;
            int nFloor = neighbourClaimed ? _caveFloor[nx, nz] : 0;
            int nCeiling = neighbourClaimed ? _caveCeiling[nx, nz] : 0;
            for (int wy = floor + 1; wy < ceiling; wy++)
            {
                if (wy <= ground) { continue; }                                  // solid rock beside it
                if (neighbourClaimed && wy > nFloor && wy < nCeiling) { continue; } // next room along
                return true;                                                     // open sky
            }
        }
        return false;
    }

    // Throw away anything no entrance can walk to. A sealed chamber the player
    // can never find is spent budget, and worse, it is invisible in every dump —
    // so it would look like the cave pass simply produced less than it did.
    private int KeepOnlyWhatAnEntranceReaches(List<CaveEntrance> entrances)
    {
        int sizeX = _caveFloor.GetLength(0);
        int sizeZ = _caveFloor.GetLength(1);
        var reached = new bool[sizeX, sizeZ];
        var queue = new Queue<int>();
        foreach (CaveEntrance e in entrances)
        {
            if (_caveFloor[e.Lx, e.Lz] == int.MinValue || reached[e.Lx, e.Lz]) { continue; }
            reached[e.Lx, e.Lz] = true;
            queue.Enqueue(e.Lx * sizeZ + e.Lz);
        }

        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int lx = idx / sizeZ;
            int lz = idx % sizeZ;
            for (int d = 0; d < 4; d++)
            {
                int nx = lx + NEIGHBOUR_DX[d];
                int nz = lz + NEIGHBOUR_DZ[d];
                if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                if (reached[nx, nz] || _caveFloor[nx, nz] == int.MinValue) { continue; }
                // Connected only where the two air spans actually overlap —
                // otherwise a chamber directly under a passage would count as
                // joined to it through solid rock.
                if (_caveFloor[nx, nz] + 1 >= _caveCeiling[lx, lz]) { continue; }
                if (_caveFloor[lx, lz] + 1 >= _caveCeiling[nx, nz]) { continue; }
                reached[nx, nz] = true;
                queue.Enqueue(nx * sizeZ + nz);
            }
        }

        int dropped = 0;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                if (_caveFloor[lx, lz] == int.MinValue || reached[lx, lz]) { continue; }
                _caveFloor[lx, lz] = int.MinValue;
                dropped++;
            }
        }
        return dropped;
    }

    // ---- emit

    // Write the verified claim into the carve grid, measuring each invariant as
    // it goes rather than asserting it was tuned correctly.
    private void EmitCaves(int[,] height, CellularTerrainData cd, int worldMinX, int worldMinZ)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int spikeMax = Math.Max(0, cd.stalagmiteHeightMax);
        int spikeMin = Math.Clamp(cd.stalagmiteHeightMin, 0, spikeMax);
        float mouthClearance = Math.Max(0f, cd.stalagmiteMouthClearance);

        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                int baseFloor = _caveFloor[lx, lz];
                if (baseFloor == int.MinValue) { continue; }
                int ceiling = _caveCeiling[lx, lz];
                int wx = lx + worldMinX;
                int wz = lz + worldMinZ;
                bool doorway = _caveDoorway[lx, lz];
                int floor = baseFloor;
                int headroom = ceiling - floor - 1;

                // Stalagmites: single-column spikes, from a POSITION HASH so the
                // answer is the same however often IsCarvedAt is asked about the
                // voxel. Capped two short of the ceiling — a spike that reaches
                // the roof is a pillar, which reads as structure rather than as
                // a cave — and kept out of the doorway, where it reads as a bug.
                int spike = 0;
                if (spikeMax > 0 && !doorway
                    && !NearAnyDoorway(lx, lz, mouthClearance, sizeX, sizeZ)
                    && Hash01Column(wx, wz, SEED_SALT_STALAGMITE) < cd.stalagmiteDensity)
                {
                    spike = spikeMin + (Hash(wx, wz, _worldSeed + SEED_SALT_STALAGMITE + 1)
                        % (spikeMax - spikeMin + 1));
                    spike = Math.Min(spike, Math.Max(0, headroom - 2));
                    if (spike > 0) { _caveStalagmites++; }
                }

                for (int wy = floor + 1 + spike; wy < ceiling; wy++)
                {
                    SetCarved(lx, lz, wy);
                    // Sealed: this voxel stays AIR even below the waterline. The
                    // claim was verified enclosed on every side and its only
                    // openings are doorways above the waterline, so nothing can
                    // run in — and chunk fill must not put the sea in it just
                    // because the column's waterline is higher. Without this
                    // every chamber below y=0 floods, which is exactly where the
                    // rock in this world is.
                    SetSealed(lx, lz, wy);
                }

                _caveColumns++;
                // The lattice invariant, counted rather than assumed. Interior
                // floors sit on interiorLevelStep; the only ground allowed off
                // it is a doorway (which meets the terrain's own 2-lattice) and
                // a ramp column (a level change spent one voxel at a time). A
                // non-zero "stray" here means some other path is writing floors,
                // which is the bug this number exists to catch.
                if (floor % _data.interiorLevelStep != 0)
                {
                    if (doorway) { _caveDoorwayOffLattice++; }
                    else if (_caveRamp[lx, lz]) { _caveRampColumns++; }
                    else { _caveStrayOffLattice++; }
                }
                if (headroom < _caveMinHeadroom) { _caveMinHeadroom = headroom; }
                int rock = height[lx, lz] - ceiling;
                if (rock < _caveMinRoofRock) { _caveMinRoofRock = rock; }
                if (floor < _caveDeepestFloor) { _caveDeepestFloor = floor; }
            }
        }
    }

    // How wide the finished spaces actually are, as the distance from every
    // claimed column to the nearest unclaimed one. A corridor N columns across
    // peaks at N/2 here, so this reads straight off as width — and it is worth
    // measuring rather than inferring from caveTunnelHalfWidth, because the
    // enclosure check trims edges and RoofHolds refuses columns, so the width
    // that gets BUILT is routinely less than the width that was asked for.
    private string MeasureWidths()
    {
        int sizeX = _caveFloor.GetLength(0);
        int sizeZ = _caveFloor.GetLength(1);
        var dist = new int[sizeX, sizeZ];
        var queue = new Queue<int>();
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                bool claimed = _caveFloor[lx, lz] != int.MinValue;
                dist[lx, lz] = claimed ? int.MaxValue : 0;
                if (!claimed) { queue.Enqueue(lx * sizeZ + lz); }
            }
        }
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int lx = idx / sizeZ;
            int lz = idx % sizeZ;
            int next = dist[lx, lz] + 1;
            for (int d = 0; d < 4; d++)
            {
                int nx = lx + NEIGHBOUR_DX[d];
                int nz = lz + NEIGHBOUR_DZ[d];
                if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                if (dist[nx, nz] <= next) { continue; }
                dist[nx, nz] = next;
                queue.Enqueue(nx * sizeZ + nz);
            }
        }

        var histogram = new SortedDictionary<int, int>();
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                if (_caveFloor[lx, lz] == int.MinValue) { continue; }
                int d = dist[lx, lz];
                histogram.TryGetValue(d, out int had);
                histogram[d] = had + 1;
            }
        }
        var sb = new System.Text.StringBuilder();
        foreach (KeyValuePair<int, int> kv in histogram)
        {
            if (sb.Length > 0) { sb.Append(' '); }
            sb.Append(kv.Key * 2 - 1).Append("m:").Append(kv.Value);
        }
        return sb.ToString();
    }

    private bool NearAnyDoorway(int lx, int lz, float radius, int sizeX, int sizeZ)
    {
        if (radius <= 0f) { return false; }
        int r = Mathf.CeilToInt(radius);
        for (int dx = -r; dx <= r; dx++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                int nx = lx + dx;
                int nz = lz + dz;
                if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                if (dx * dx + dz * dz > radius * radius) { continue; }
                if (_caveDoorway[nx, nz]) { return true; }
            }
        }
        return false;
    }

    // Position hash in [0,1) for a column. Separate from Hash01(long, int),
    // which keys off a packed cell identity rather than a world column.
    private float Hash01Column(int wx, int wz, int salt)
    {
        return (Hash(wx, wz, _worldSeed + salt) & 0xFFFF) / 65536f;
    }

    // ------------------------------------------------------------ diagnostics

    // Write this approach's own view of the carve into the debug dump: vertical
    // slices, which are the one thing the height-field images cannot show. A
    // hillshade of a world with a cave under it looks exactly like a hillshade
    // of a world without one.
    public void DumpDiagnostics(string dir)
    {
        if (_carve == null || _caveSystems == 0) { return; }
        System.IO.Directory.CreateDirectory(dir);

        // Roof rock measured against the LIVE height field, which by dump time
        // has been through the road grader — a pass that runs long after
        // carving and rewrites terrain in place. Nothing stops it cutting its
        // tread down over a cave, and if it ever does the symptom is a hole in
        // a cave ceiling that no carving-side check could have seen. The number
        // below is the whole test: it must stay at or above caveRoofRock.
        int minRock = int.MaxValue;
        for (int lx = 0; lx < _carveSizeX; lx++)
        {
            for (int lz = 0; lz < _carveSizeZ; lz++)
            {
                // Bridge columns are excluded, and getting that wrong wasted a
                // round: the rock over the topmost carved voxel of a bridge
                // column IS the deck, so a 3-voxel deck reports as a 3-voxel
                // roof and reads as a cave roof shaved below its minimum. They
                // are different measurements with different floors —
                // bridgeThickness for one, caveRoofRock for the other.
                if (_bridgeGroundUnder != null && _bridgeGroundUnder[lx, lz] != int.MinValue)
                {
                    continue;
                }
                long baseBit = ((long)lx * _carveSizeZ + lz) * _carveYSpan;
                for (int ly = _carveYSpan - 1; ly >= 0; ly--)
                {
                    long bit = baseBit + ly;
                    if ((_carve[bit >> 6] & (1UL << (int)(bit & 63))) == 0UL) { continue; }
                    int rock = _sliceHeight[lx, lz] - (ly + _carveMinY);
                    if (rock < minRock) { minRock = rock; }
                    break;
                }
            }
        }
        GD.Print($"[CellularTerrain] roof check against the finished (road-graded) heights:"
            + $" thinnest rock over any CAVE voxel {minRock}v (carved with at least"
            + $" {_data.caveRoofRock}v; later passes regrade terrain and may shave it)");

        // One slice through each system's mouth, along X, as text. Text rather
        // than an image because what is being read is exact voxel heights —
        // "is this ceiling on a multiple of 4" is a question about digits.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Cellular terrain carve slices. '#' solid, '.' air (carved), ' ' above ground.");
        sb.AppendLine($"Carve band y {_carveMinY}..{_carveMinY + _carveYSpan - 1}");
        sb.AppendLine();

        foreach (int lz in _sliceRows)
        {
            sb.AppendLine($"--- slice at world z = {lz + _carveWorldMinZ} ---");
            for (int wy = _carveMinY + _carveYSpan - 1; wy >= _carveMinY; wy--)
            {
                sb.Append($"y={wy,4} ");
                for (int lx = 0; lx < _carveSizeX; lx++)
                {
                    int ground = _sliceHeight[lx, lz];
                    // Carved wins over "above ground". At a doorway the air
                    // reaches above the terrace in front of it, and drawing
                    // those voxels as sky hid the top of every arch — the one
                    // part of a mouth worth looking at.
                    if (IsCarvedAt(lx + _carveWorldMinX, wy, lz + _carveWorldMinZ, ground))
                    {
                        sb.Append('.');
                    }
                    else if (wy > ground) { sb.Append(' '); }
                    else { sb.Append('#'); }
                }
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "carve_slices.txt"), sb.ToString());
        GD.Print($"[CellularTerrain] wrote {dir}/carve_slices.txt ({_sliceRows.Count} slices)");
    }

    private readonly List<int> _sliceRows = new();
    private int[,] _sliceHeight;

    // Bank what a slice needs: the finished heights, and one row through each
    // cave mouth. Rows are recorded as the systems are carved rather than
    // searched for afterwards — the mouth is the interesting part of a cave and
    // an arbitrary row usually misses every system in the world.
    private void PrepareCarveSlices(int[,] height)
    {
        _sliceHeight = height;
    }
}
