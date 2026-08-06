using Godot;

// Synthetic-geometry checks for the cell decomposition and its region
// segmentation. Same shape as MesherProbe: build a small WorldState in memory,
// run the real code over it, print what it decided.
//
// This exists because the cases the model has to get right are not reachable by
// standing somewhere and looking — a door closing over the player's head is a
// one-frame ordering question, and "does the barn's bay separate from the space
// under its balcony" is a claim about the join predicate that a screenshot can
// only weakly support. Each case here is the geometry from the design write-up,
// built exactly, with the expected answer asserted.
public static class CellRegionProbe
{
    private static int _passed;
    private static int _failed;

    public static void Run()
    {
        _passed = 0;
        _failed = 0;
        GD.Print($"[cell_probe] === plateau={CellRegions.Plateau} maxWallDilate={CellRegions.MAX_WALL_DILATE} ===");
        Barn();
        RoofIsAnEntity();
        OpeningSplitsRooms();
        DoorClosesOverPlayer();
        PartyWall();
        WindowsCutWithTheirWall();
        HillsideTunnel();
        GD.Print($"[cell_probe] === {_passed} passed, {_failed} failed ===");
    }

    // The reference case. A barn with an open bay and a balcony over half of it
    // yields THREE spaces, and the two that share a floor are separated by the
    // cut-plane bucket alone — the bay cuts at the roof, the space under the deck
    // cuts at the deck. This is what the whole model is for.
    private static void Barn()
    {
        WorldState ws = NewWorld();
        Box(ws, 0, -1, 0, 11, -1, 7, VoxelType.Stone);   // floor slab
        Shell(ws, 0, 0, 0, 11, 7, 7);                     // walls
        Box(ws, 0, 8, 0, 11, 8, 7, VoxelType.Stone);      // roof slab
        Box(ws, 1, 3, 1, 5, 3, 6, VoxelType.Stone);       // balcony deck over half

        Run(ws, new Vector3(8.5f, 0.5f, 3.5f), out CellField field, out CellRegions regions);
        Report("barn", field, regions);

        int bay = RegionAt(field, regions, 8, 0, 3);
        int under = RegionAt(field, regions, 3, 0, 3);
        int upstairs = RegionAt(field, regions, 3, 4, 3);
        Check("barn: bay, under-balcony and upstairs are three distinct regions",
            bay >= 0 && under >= 0 && upstairs >= 0 && bay != under && bay != upstairs && under != upstairs);
        Check("barn: bay cuts at the roof (8)", CutOf(regions, bay) == 8f);
        Check("barn: under-balcony cuts at the deck (4)", CutOf(regions, under) == 4f);
        Check("barn: upstairs cuts at the roof (8)", CutOf(regions, upstairs) == 8f);
        // The claim the ceiling-only bucket has to make good: a region's ceiling
        // spread cannot exceed one plateau, so the lumps Stage 2 will show are
        // bounded rather than unbounded (which is what a clearance bucket gives
        // on a sloping ceiling).
        Check("barn: no region's ceiling spread exceeds one plateau", MaxCeilingSpread(regions) < CellRegions.Plateau);
    }

    // A roof is a Node3D, not a voxel. Without reading SunOpaque the interior
    // under one is a SKY cell, sky regions never cut, and the barn fails at step
    // one — so this guards the single assumption everything else rests on.
    private static void RoofIsAnEntity()
    {
        WorldState ws = NewWorld();
        Box(ws, 0, -1, 0, 7, -1, 7, VoxelType.Stone);
        Shell(ws, 0, 0, 0, 7, 3, 7);
        for (int x = 0; x <= 7; x++)
        {
            for (int z = 0; z <= 7; z++)
            {
                ws.SetSunOpaqueWorld(x, 4, z);
            }
        }

        Run(ws, new Vector3(3.5f, 0.5f, 3.5f), out CellField field, out CellRegions regions);
        Report("roof-entity", field, regions);

        int interior = RegionAt(field, regions, 3, 0, 3);
        Check("roof-entity: interior is enclosed, not sky",
            interior >= 0 && !regions.Regions[interior].IsSky);
        Check("roof-entity: interior cuts at the roof sheet (4)", CutOf(regions, interior) == 4f);
    }

    // Two rooms joined by a plain gap are one space; the same gap authored as
    // VoxelType.Opening is two. That is the only job Opening has left.
    private static void OpeningSplitsRooms()
    {
        WorldState plain = TwoRooms(VoxelType.Air);
        Run(plain, new Vector3(2.5f, 0.5f, 2.5f), out CellField f1, out CellRegions r1);
        Check("opening: a plain gap merges the two rooms",
            RegionAt(f1, r1, 2, 0, 2) == RegionAt(f1, r1, 8, 0, 2) && RegionAt(f1, r1, 2, 0, 2) >= 0);

        WorldState authored = TwoRooms(VoxelType.Opening);
        Run(authored, new Vector3(2.5f, 0.5f, 2.5f), out CellField f2, out CellRegions r2);
        Report("opening", f2, r2);
        int a = RegionAt(f2, r2, 2, 0, 2);
        int b = RegionAt(f2, r2, 8, 0, 2);
        Check("opening: an authored Opening splits them", a >= 0 && b >= 0 && a != b);
    }

    // The ordering that hysteresis exists for, and the one most likely to go
    // wrong: a door shutting while the player stands in its doorway splits the
    // region underneath them and leaves their feet inside a Barrier. The answer
    // has to be "the room they came from", not a singleton the size of the
    // doorframe and not nothing.
    private static void DoorClosesOverPlayer()
    {
        WorldState ws = TwoRooms(VoxelType.Opening);
        var regions = new CellRegions();
        var field = new CellField();

        // Walk in from room A. Each step records the anchor, exactly as the live
        // tick does — hysteresis reaches two columns, so the approach matters.
        int roomA = -1;
        for (int x = 2; x <= 4; x++)
        {
            Tick(field, regions, ws, new Vector3(x + 0.5f, 0.5f, 2.5f));
            roomA = regions.PlayerRegion;
        }
        Check("door: walking in room A seeds directly",
            regions.SeedSource == CellRegions.ESeedSource.Direct && roomA >= 0);

        // Into the doorway. The cell there is an authored Opening and joins
        // nothing, so the previous region must be held.
        Tick(field, regions, ws, new Vector3(5.5f, 0.5f, 2.5f));
        Check("door: standing in the doorway holds room A",
            regions.SeedSource == CellRegions.ESeedSource.Hysteresis && regions.PlayerRegion == roomA);

        // The door shuts on top of them.
        ws.SetVoxelWorld(5, 0, 2, VoxelType.Barrier);
        ws.SetVoxelWorld(5, 1, 2, VoxelType.Barrier);
        ws.SetVoxelWorld(5, 0, 3, VoxelType.Barrier);
        ws.SetVoxelWorld(5, 1, 3, VoxelType.Barrier);
        foreach (Vector3I coord in ws.CellChunkDirty)
        {
            field.InvalidateChunk(coord);
        }
        ws.CellChunkDirty.Clear();
        Tick(field, regions, ws, new Vector3(5.5f, 0.5f, 2.5f));
        Report("door-closed", field, regions);
        Check("door: closing over the player still holds room A",
            regions.SeedSource == CellRegions.ESeedSource.Hysteresis && regions.PlayerRegion == roomA);
    }

    // The wall flood derives its extent from the wall itself: it claims the
    // party wall whole and stops on its far face, because the room beyond has
    // air at the player's own level.
    private static void PartyWall()
    {
        WorldState ws = TwoRooms(VoxelType.Stone);   // solid divider, no doorway
        Run(ws, new Vector3(2.5f, 0.5f, 2.5f), out CellField field, out CellRegions regions);
        Report("party-wall", field, regions);

        Check("party wall: the divider is claimed", WallAt(field, regions, 5, 2));
        Check("party wall: the far room's floor is not", !WallAt(field, regions, 6, 2));

        // The run-along leak, which cut the neighbouring space's walls down to
        // this room's ceiling while its roof — resolving over its own footprint —
        // correctly stayed put. The outer wall alongside the FAR room belongs to
        // that room, not this one.
        Check("party wall: the outer wall past the far room is NOT claimed",
            !WallAt(field, regions, 8, 0) && !WallAt(field, regions, 9, 5));
        Check("party wall: this room's own outer wall still is",
            WallAt(field, regions, 0, 2) && WallAt(field, regions, 2, 0));
        // Rule one alone loses these: a corner is only ever diagonal from the
        // region's air, so it would stand full height between two cut walls.
        Check("party wall: corner posts of this room are claimed",
            WallAt(field, regions, 0, 0) && WallAt(field, regions, 0, 5));
        Check("party wall: the flood terminated rather than running out of budget",
            !regions.WallClaimHitBudget);
    }

    // A window is a hole in a wall, not a room of its own. It joins nothing — that
    // is the entire point of authoring it — which makes it a singleton region, and
    // the wall flood used to read that singleton as "somebody else's air at my
    // level" and stop. The wall then cut around every window and doorway, leaving
    // a full-height pillar standing at each one.
    private static void WindowsCutWithTheirWall()
    {
        WorldState ws = NewWorld();
        Box(ws, 0, -1, 0, 6, -1, 5, VoxelType.Stone);
        Shell(ws, 0, 0, 0, 6, 3, 5);
        Box(ws, 0, 4, 0, 6, 4, 5, VoxelType.Stone);      // ceiling
        ws.SetVoxelWorld(0, 1, 2, VoxelType.Opening);    // window in the west wall
        ws.SetVoxelWorld(3, 0, 0, VoxelType.Opening);    // doorway in the north wall
        ws.SetVoxelWorld(3, 1, 0, VoxelType.Opening);

        Run(ws, new Vector3(3.5f, 0.5f, 2.5f), out CellField field, out CellRegions regions);
        Report("window-in-wall", field, regions);

        Check("window: the window's column cuts with its wall", WallAt(field, regions, 0, 2));
        Check("window: the doorway's column cuts with its wall", WallAt(field, regions, 3, 0));
        Check("window: a plain stretch of the same wall still cuts", WallAt(field, regions, 0, 3));
        // The doorway must still not let the cut escape into the street.
        Check("window: the cut does not leak through the doorway to outside",
            !WallAt(field, regions, 3, -1));
    }

    // The case with no far face: a tunnel bored through a hill. The solid is
    // unbounded, so only MAX_WALL_DILATE stops the flood and the bite it takes
    // out of the hillside is arbitrary. Asserted so the diagnostic that reports
    // it stays honest — this is the case to watch when Stage 2 starts cutting.
    private static void HillsideTunnel()
    {
        WorldState ws = NewWorld();
        Box(ws, -15, -8, -15, 15, 10, 15, VoxelType.Stone);
        Box(ws, -15, 0, -1, 15, 2, 1, VoxelType.Air);   // tunnel through it

        Run(ws, new Vector3(0.5f, 0.5f, 0.5f), out CellField field, out CellRegions regions);
        Report("hillside-tunnel", field, regions);

        int tunnel = RegionAt(field, regions, 0, 0, 0);
        Check("tunnel: the bore is its own enclosed region",
            tunnel >= 0 && !regions.Regions[tunnel].IsSky);
        Check("tunnel: the wall flood runs to its safety bound in unbounded solid",
            regions.WallClaimHitBudget);
    }

    // --- geometry helpers -------------------------------------------------

    // Two 5x6 rooms under a shared ceiling, divided at x=5. The divider's middle
    // two columns are filled with `gap`: Air merges the rooms, Opening splits
    // them, Stone makes it a party wall.
    private static WorldState TwoRooms(VoxelType gap)
    {
        WorldState ws = NewWorld();
        Box(ws, 0, -1, 0, 10, -1, 5, VoxelType.Stone);
        Shell(ws, 0, 0, 0, 10, 2, 5);
        Box(ws, 0, 3, 0, 10, 3, 5, VoxelType.Stone);   // ceiling
        Box(ws, 5, 0, 0, 5, 2, 5, VoxelType.Stone);    // divider
        Box(ws, 5, 0, 2, 5, 1, 3, gap);                // the doorway itself
        return ws;
    }

    private static WorldState NewWorld()
    {
        var ws = new WorldState(new Vector3I(-2, -2, -2), new Vector3I(1, 1, 1), null);
        for (int cx = -2; cx <= 1; cx++)
        {
            for (int cy = -2; cy <= 1; cy++)
            {
                for (int cz = -2; cz <= 1; cz++)
                {
                    var coord = new Vector3I(cx, cy, cz);
                    ws._chunks[coord] = new ChunkState(coord);
                }
            }
        }
        return ws;
    }

    private static void Box(WorldState ws, int x0, int y0, int z0, int x1, int y1, int z1, VoxelType type)
    {
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    ws.SetVoxelWorld(x, y, z, type);
                }
            }
        }
    }

    // Four walls, no floor and no ceiling.
    private static void Shell(WorldState ws, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        Box(ws, x0, y0, z0, x0, y1, z1, VoxelType.Stone);
        Box(ws, x1, y0, z0, x1, y1, z1, VoxelType.Stone);
        Box(ws, x0, y0, z0, x1, y1, z0, VoxelType.Stone);
        Box(ws, x0, y0, z1, x1, y1, z1, VoxelType.Stone);
    }

    // --- run + inspect ----------------------------------------------------

    private static void Run(WorldState ws, Vector3 center, out CellField field, out CellRegions regions)
    {
        field = new CellField();
        regions = new CellRegions();
        ws.CellChunkDirty.Clear();
        Tick(field, regions, ws, center);
    }

    private static void Tick(CellField field, CellRegions regions, WorldState ws, Vector3 center)
    {
        field.Tick(ws, center);
        regions.Tick(field, ws, center, CellRegions.MAX_WALL_DILATE);
    }

    // Region owning the cell at this world voxel, or -1.
    private static int RegionAt(CellField field, CellRegions regions, int wx, int wy, int wz)
    {
        if (!field.TryColumn(wx, wz, out int gx, out int gz))
        {
            return -1;
        }
        int count = field.CountAt(gx, gz);
        for (int slot = 0; slot < count; slot++)
        {
            if (field.CellAt(gx, gz, slot).Contains(wy))
            {
                return regions.LabelAt(gx, gz, slot);
            }
        }
        return -1;
    }

    private static bool WallAt(CellField field, CellRegions regions, int wx, int wz)
    {
        return field.TryColumn(wx, wz, out int gx, out int gz) && regions.IsWallColumn(gx, gz);
    }

    private static float CutOf(CellRegions regions, int regionId)
    {
        return regionId < 0 || regionId >= regions.Regions.Count ? float.NaN : regions.Regions[regionId].CutHeight;
    }

    private static int MaxCeilingSpread(CellRegions regions)
    {
        int worst = 0;
        foreach (CellRegion r in regions.Regions)
        {
            if (!r.IsSky)
            {
                worst = Mathf.Max(worst, r.MaxCeilingY - r.MinCeilingY);
            }
        }
        return worst;
    }

    private static void Report(string label, CellField field, CellRegions regions)
    {
        int enclosed = 0;
        foreach (CellRegion r in regions.Regions)
        {
            if (!r.IsSky) { enclosed++; }
        }
        GD.Print($"[cell_probe] {label}: regions={regions.Regions.Count} enclosed={enclosed} "
            + $"playerRegion={regions.PlayerRegion} seed={regions.SeedSource} "
            + $"wallColumns={regions.WallColumnsClaimed} wallDepth={regions.WallClaimDepth} "
            + $"hitBudget={regions.WallClaimHitBudget} "
            + $"truncatedColumns={field.TruncatedColumns}");
    }

    // Behaviour that is observed and understood but not yet decided — a question
    // for the stage that will actually cut, not a pass or a fail here.
    private static void Note(string what)
    {
        GD.Print($"[cell_probe] NOTE {what}");
    }

    private static void Check(string what, bool ok)
    {
        if (ok)
        {
            _passed++;
        }
        else
        {
            _failed++;
        }
        GD.Print($"[cell_probe] {(ok ? "PASS" : "FAIL")} {what}");
    }
}
