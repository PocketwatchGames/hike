using System.Collections.Generic;
using Godot;

// Turns a painted document into a WorldState / .hike. The deterministic half of
// the painter: given the same layers it must produce the same world.
//
// Split out of WorldMapState so the model is only the map and this is only what
// is made of it. It holds the WorldState under construction, the kit-slot
// binding, and the four-stage bake driver; it RESOLVES nothing about the
// document itself — what stands at a column is the model's answer
// (Map.TreeAt / Map.GrassAt / Map.PropSetAt), which is why the map preview and
// the bake cannot disagree about it.
//
// It does not compile against WorldMapInkData and must not: a display value can
// never decide what a world is made of.
public class WorldMapBake
{
    // The document being baked.
    public readonly WorldMapState Map;

    // The world under construction. Null until BuildWorld runs.
    public WorldState WorldState;

    public WorldMapBake(WorldMapState map)
    {
        Map = map;
    }

    // ---- Bake -----------------------------------------------------------

    // Full build from the current layers: create the WorldState + every chunk,
    // stamp regions/zones, stamp all columns, propagate sunlight.
    public WorldState BuildWorld(System.Action<float, string> progress = null)
    {
        // The painter itself only reads the layer images, so nothing binds the
        // flat block/kit tables at launch the way StartGame / StartEditor do.
        // The stamp below reads them per voxel, so bind here — this is the only
        // place in the painter that needs them, and it covers both bake entry
        // points (Ctrl+S and WorldMapData's headless "Bake to .hike" button).
        // ChunkMesh.SetTerrains is deliberately not called: no meshes are built.
        // Named rather than left to NullReferenceException from the middle of a
        // bake: `finish` is the tuning every derived channel reads, so a document
        // missing it produces a world with no moss, no climb crust, no fog and a
        // staircase for every painted slope.
        if (Map.Data.finish == null)
        {
            throw new System.InvalidOperationException(
                $"WorldMapData.finish is null on {Map.Data.ResourcePath} — assign a WorldFinishData.");
        }

        // The bake's OWN palette instance, handed to the world it builds. It
        // used to bind process-global tables from this background thread while
        // the painter was live on the main one — which is why "one bake at a
        // time" had to be a rule rather than a consequence.
        // Per-stage timing. The build is minutes on a real document, and which
        // stage is minutes is not guessable from reading it.
        var stageClock = System.Diagnostics.Stopwatch.StartNew();
        var stages = new System.Text.StringBuilder();
        void Stage(string name)
        {
            stages.Append($" {name}={stageClock.ElapsedMilliseconds}ms");
            stageClock.Restart();
        }

        Blocks.Bind();
        var palette = KitPalette.Build(Map.Data.kitPalette);

        var ws = new WorldState(Map.Data.MinChunk, Map.Data.MaxChunk, Map.Data.simData, palette);
        // A placement's forked entry, and anything that entry authors inline,
        // lives in this document - which is a bake input and does not ship.
        ws.AuthoringDocument = Map.Data.placementsPath ?? "";
        BindZoneKits(palette);

        // Runtime zone table comes from the PAINTED palette, so a chunk's stamped
        // index and WorldState.Zones[index] are the same list by construction.
        ZoneData[] zones = Map.Data.PaintableZones;
        ws.Zones = new ZoneState[zones.Length];
        for (int i = 0; i < zones.Length; i++)
        {
            ws.Zones[i] = new ZoneState
            {
                Data = zones[i],
                WindDirection = new Vector3(0.7f, 0f, 0.7f),
                Elevation = 0f,
            };
        }
        RegionData[] regions = Map.Data.regions ?? [];
        ws.Regions = new RegionState[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            ws.Regions[i] = new RegionState { Data = regions[i] };
        }

        for (int cx = Map.Data.MinChunk.X; cx <= Map.Data.MaxChunk.X; cx++)
        {
            for (int cy = Map.Data.MinChunk.Y; cy <= Map.Data.MaxChunk.Y; cy++)
            {
                for (int cz = Map.Data.MinChunk.Z; cz <= Map.Data.MaxChunk.Z; cz++)
                {
                    var coord = new Vector3I(cx, cy, cz);
                    var chunk = new ChunkState(coord);
                    chunk.RegionIndex = SampleChunkIndex(Map.Region, cx, cz, Map.RegionCount);
                    chunk.ZoneIndex = SampleChunkIndex(Map.Zone, cx, cz, Map.ZoneCount);
                    ws._chunks[coord] = chunk;
                }
            }
        }

        // The run's quests, party and starting knowledge, off the document's

        // own start content — so a painted .hike carries its own rather than taking

        // whichever world the menu had selected.

        ws.BindStartContent(Map.Data.startContent);


        WorldState = ws;
        Stage("alloc");

        // Stamped a strip at a time purely so the bake can report progress; the
        // result is identical to one whole-map call.
        int width = Map.Data.ImageWidth;
        for (int px = 0; px < width; px++)
        {
            StampColumns(new Rect2I(px, 0, 1, Map.Data.ImageHeight), null);
            if ((px & 15) == 0)
            {
                progress?.Invoke(STAMP_START + (STAMP_END - STAMP_START) * px / width, "Stamping terrain");
            }
        }

        // Stamp the authored subscenes over the terrain, before the routes and
        // the scatter: a scene brings its own floor and its own walls, so
        // anything measured off the ground has to see the building already
        // standing. Entities come with it, which is why this precedes the
        // scatter pass that would otherwise plant trees inside it.
        Stage("columns");
        progress?.Invoke(STAMP_END, "Stamping scenes");
        StampPlacements();
        Stage("scenes");

        // Turn the painted routes into climbable rock, running WORLDGEN'S OWN
        // pass over the world we just stamped: it takes the per-column answers it
        // cannot look up here (a route flag instead of a zone's coverage, the
        // painted water layer instead of a HeightMap) and does the rest itself —
        // the exposed-face walk, the run heights, the per-block growth table.
        // Unpatched, so a marked column's whole face is dressed rather than a
        // fraction of it. Must follow the terrain stamp: it finds walls by
        // walking exposed faces of real voxels.
        progress?.Invoke(STAMP_END, "Cutting climbing routes");
        WorldFinish.StampClimbSurfaces(ws, Map.Data.finish,
            (wx, wz) => Map.ClimbRouteAt(wx - Map.Data.WorldMinX, wz - Map.Data.WorldMinZ) ? 1f : 0f,
            (wx, wz) => Map.WaterSurface(wx - Map.Data.WorldMinX, wz - Map.Data.WorldMinZ),
            Map.Data.climbRouteMinWallVoxels, false);
        Stage("climb");

        // Scatter props/interactives into the fresh WorldState (Sim is null
        // here, so this only adds sim states — the painter's initial entity
        // load spawns the nodes).
        //
        // A spawn entry asks its CONTEXT for the level it should place at
        // (SpawnContext.MobLevel / ForgeLevel). The bake hands it the painted
        // difficulty layer through SpawnContextForBake, so the answer reaches
        // exactly this pass — where worldgen installs its zone-band sampler on
        // the contexts IT builds.
        progress?.Invoke(STAMP_END, "Scattering entities");
        RescatterColumns(new Rect2I(0, 0, Map.Data.ImageWidth, Map.Data.ImageHeight));
        Stage("scatter");

        StampEntities();
        Stage("entities");

        // Everything a finished world derives from its own voxels, through the
        // SAME list worldgen ends on (WorldFinish.Finish): grades, detail, the
        // air pipeline, fog, water currents and the cascades. This used to be
        // four hand-picked calls into WorldGen, so the channels it did not know
        // about — fog, EnvTag, interiorness, currents — baked as zeros and
        // nothing recomputed them on load. A painted world had no fog anywhere
        // and read Outdoor in every building and tunnel.
        //
        // Three things differ from a generated world, and each is a fact about a
        // painted one rather than a switch:
        //   - no zone-weight kernel, so the detail scatter takes each voxel's
        //     own kit (the painter assigns kits per column deterministically);
        //   - no river-flow field, so water gets the ambient drift only;
        //   - no sunlight flood, because every consumer of a .hike relights on
        //     open (Main on both load branches, WorldEditor on both its open
        //     paths) and it was ~19s of a ~22s bake, discarded every time. Sky
        //     exposure still runs: it is not serialized, but interiorness — which
        //     is — floods from it.
        progress?.Invoke(WRITE_START, "Deriving world");
        WorldFinish.Finish(ws, Map.Data.finish, new WorldFinish.Options
        {
            MinX = Map.Data.WorldMinX,
            MaxX = Map.Data.WorldMinX + Map.Data.ImageWidth - 1,
            MinZ = Map.Data.WorldMinZ,
            MaxZ = Map.Data.WorldMinZ + Map.Data.ImageHeight - 1,
            MaxGradeStep = Map.MaxGradeStep,
            // Paving is a deliberate bare tread, exactly as a road is — and so
            // is a stamped surface: a building's roof, floor and courtyard are
            // authored, so the scatter has no business dressing them.
            SkipDetailColumn = (wx, wz) =>
                Map.SurfacePavingAt(wx - Map.Data.WorldMinX, wz - Map.Data.WorldMinZ) != null
                || IsStampedColumn(wx, wz),
            GroundYAt = GroundYAtWorld,
            PaintedWaterBlockAt = (wx, wz) => Map.PaintedWaterBlockAt(wx - Map.Data.WorldMinX, wz - Map.Data.WorldMinZ),
            // Moss comes off the painted GROUND, because a painted world has no
            // ZoneGenData to ask (its zone layer paints ZoneData, and the two
            // palettes do not correspond). A column's surface kit and cave kit
            // are exactly the two coverages the pass wants, so painting a
            // material brings its moss with it — no second brush, and no moss
            // where nothing was painted.
            MossCoverageAt = MossCoverageAtWorld,
            StampWind = StampWind,
        });
        Stage("finish");
        GD.Print($"[bake] build stages:{stages}");

        // Authored spawn, or the world origin when none is placed.
        int spawnX = Map.Placements.hasSpawn ? Map.Placements.spawnXZ.X : 0;
        int spawnZ = Map.Placements.hasSpawn ? Map.Placements.spawnXZ.Y : 0;
        int spawnH = GroundYAtWorld(spawnX, spawnZ);
        ws.Spawn = new Vector3(spawnX + 0.5f, spawnH + 2f, spawnZ + 0.5f);

        // NOT lit here: the sun flood is step 3 of the bake (BakeRelightAndWrite),
        // after the main thread has stamped the canopy. It IS baked into the file
        // — nothing relights a world on load any more, so a .hike written without
        // it would load black. See LightEngine.LIGHT_VERSION.
        return ws;
    }

    // Hand-placed entities, spawned through the SAME path a scattered one takes
    // (SpawnEntryData.TrySpawn with the bake's context), so a chest placed by
    // hand and one rolled by a spawn set differ in position and nothing else.
    //
    // The seed is the column, so re-baking an unchanged document places the same
    // thing twice — an entry that rolls a variant or a loot table must not
    // shuffle between bakes.
    private void StampEntities()
    {
        foreach (EntityPlacement placement in Map.Placements.entities)
        {
            if (placement?.Entry == null)
            {
                continue;
            }
            int px = placement.anchorXZ.X - Map.Data.WorldMinX;
            int pz = placement.anchorXZ.Y - Map.Data.WorldMinZ;
            int floor = placement.floorY;
            if (floor == EntityPlacement.OnTheGround)
            {
                // The top of the COLUMN, edits and stamps included — not the
                // height field. They differ wherever something was carved or
                // built, and an entity that says it stands on the ground should
                // stand on the ground that is there: dig a pit under one and it
                // drops in, drop one on a stamped plaza and it stands on the
                // plaza, rather than hanging at the height the terrain used to
                // be. The stamp is the half the document cannot answer, so it is
                // asked first — SurfaceBelow sees the tunnel mask but never a
                // subscene.
                floor = IsStampedColumn(placement.anchorXZ.X, placement.anchorXZ.Y)
                    ? GroundYAtWorld(placement.anchorXZ.X, placement.anchorXZ.Y)
                    : Map.SurfaceBelow(px, pz, int.MaxValue);
                if (floor < Map.Data.WorldMinY)
                {
                    floor = Map.TerrainHeight(px, pz);
                }
            }
            var pos = new Vector3(placement.anchorXZ.X + 0.5f, floor + 1f,
                placement.anchorXZ.Y + 0.5f);
            uint seed = WorldMapState.Hash(px, pz, WorldMapState.ENTITY_SALT);
            // The eighth turn the placement was aimed at. Set on the shared bake
            // context and cleared again rather than passed: this loop is the one
            // caller that has a facing, and everything else the context answers
            // must keep giving a scattered entity its random yaw. Safe because
            // the bake walks these one at a time on one thread.
            //
            // AuthoredPosition rides along for the same reason and CANNOT live
            // on the shared context: RescatterColumns uses that context too, and
            // a scattered mob must keep the lateral-clearance gate — rejecting a
            // 1-voxel tunnel is exactly what it is for. A hand-placed one is the
            // opposite case: the gate wants 4-connected air around the anchor,
            // which is what a wall is not, so a villager placed in the doorway
            // you aimed at would silently never spawn. It is the same claim
            // WorldGen makes for an entry it drops on an authored subscene
            // marker.
            SpawnContext context = SpawnContextForBake();
            context.FacingY = placement.FacingRadians;
            context.AuthoredPosition = true;
            placement.Entry.TrySpawn(WorldState, pos, new System.Random((int)seed), context);
            context.FacingY = null;
            context.AuthoredPosition = false;
        }
    }

    // Seat each stamp on the ground under its footprint and write it in.
    //
    // The height is WorldGen's own FootprintPlateauY — the most common ground
    // level across the footprint, ties to the lower one — fed the painted
    // terrain instead of a HeightMap. Averaging or taking the max would float a
    // building over a dip or bury it in a rise; the stamp overwrites its whole
    // bbox, so cutting in is self-correcting and floating is not.
    private void StampPlacements()
    {
        foreach (SubscenePlacement placement in Map.Placements.placements)
        {
            SubsceneState sub = Map.SubsceneFor(placement);
            if (sub == null)
            {
                continue;
            }
            var anchor = new Vector3(placement.anchorXZ.X, Map.SeatY(placement), placement.anchorXZ.Y);
            SubsceneStamper.StampAll(WorldState, sub, anchor);
            RecordStampedGround(placement);
            GD.Print($"WorldMapState: stamped {placement.path.GetFile()} at {anchor} "
                + $"(size={sub.Size}, rot={(int)placement.rotation * 90}deg, yOffset={placement.yOffset})");
        }
    }

    // Per-column ground a subscene stamp left behind, or NotStamped.
    //
    // A stamp is a PLACEMENT, not a layer: it exists only in the WorldState the
    // bake is building. The document's height queries cannot see one — SolidAt
    // is the height field plus the TUNNEL mask, and nothing writes a stamp into
    // either — so every pass after the `scenes` stage that asks the document
    // "how high is the ground here" gets the bare terrain the building is
    // standing on. That is what seated a hand-placed sign at the painted height
    // instead of on the plaza it was dropped on.
    private const int NotStamped = int.MinValue;
    private int[] _stampedGround;

    // The stamp's top surface as seen from above: the highest solid voxel with
    // air over it. For a plaza or a road that is the floor you walk on. For a
    // ROOFED building it is the roof, which is the honest answer to a top-down
    // question — an entity meant to stand inside one is placed against the
    // cutaway plane and carries its own floorY, so it never asks this.
    private void RecordStampedGround(SubscenePlacement placement)
    {
        _stampedGround ??= FilledStampedGround();
        // FootprintOf answers in PIXEL space, not world space.
        Rect2I footprint = Map.FootprintOf(placement);
        int worldMinY = WorldState.Min.Y * ChunkState.SIZE;
        int worldMaxY = WorldState.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        for (int px = footprint.Position.X; px < footprint.Position.X + footprint.Size.X; px++)
        {
            for (int pz = footprint.Position.Y; pz < footprint.Position.Y + footprint.Size.Y; pz++)
            {
                if (px < 0 || pz < 0 || px >= Map.Data.ImageWidth || pz >= Map.Data.ImageHeight)
                {
                    continue;
                }
                int wx = px + Map.Data.WorldMinX;
                int wz = pz + Map.Data.WorldMinZ;
                bool airAbove = true;
                for (int wy = worldMaxY; wy >= worldMinY; wy--)
                {
                    bool solid = Blocks.IsSolid(WorldState.GetBlockWorld(wx, wy, wz));
                    if (solid && airAbove)
                    {
                        // A footprint is a bounding BOX, so most columns inside
                        // one are untouched ground the scene never wrote. Mark
                        // only where the stamp actually moved the surface —
                        // otherwise every building strips the detail scatter
                        // from its whole bounding box, hundreds of columns of
                        // plain terrain included.
                        if (wy != Map.SurfaceBelow(px, pz, int.MaxValue))
                        {
                            _stampedGround[pz * Map.Data.ImageWidth + px] = wy;
                        }
                        break;
                    }
                    airAbove = !solid;
                }
            }
        }
    }

    private int[] FilledStampedGround()
    {
        var a = new int[Map.Data.ImageWidth * Map.Data.ImageHeight];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = NotStamped;
        }
        return a;
    }

    // The ground at a world column for anything running AFTER the scenes stage:
    // what the stamp put there if one covers it, else what the document says.
    // Before that stage there is nothing to add and this is TerrainHeight.
    private int GroundYAtWorld(int wx, int wz)
    {
        int px = wx - Map.Data.WorldMinX;
        int pz = wz - Map.Data.WorldMinZ;
        if (_stampedGround != null
            && px >= 0 && pz >= 0 && px < Map.Data.ImageWidth && pz < Map.Data.ImageHeight)
        {
            int stamped = _stampedGround[pz * Map.Data.ImageWidth + px];
            if (stamped != NotStamped)
            {
                return stamped;
            }
        }
        return Map.TerrainHeight(px, pz);
    }

    private bool IsStampedColumn(int wx, int wz)
    {
        int px = wx - Map.Data.WorldMinX;
        int pz = wz - Map.Data.WorldMinZ;
        return _stampedGround != null
            && px >= 0 && pz >= 0 && px < Map.Data.ImageWidth && pz < Map.Data.ImageHeight
            && _stampedGround[pz * Map.Data.ImageWidth + px] != NotStamped;
    }

    // Bake phase boundaries, as a fraction of the whole job: stamping is most of
    // it, then the sun flood and the file write (RELIGHT_START / WRITE_FILE_START).
    private const float STAMP_START = 0.05f;
    private const float STAMP_END = 0.80f;
    private const float WRITE_START = 0.85f;

    // Map every paintable ground set onto palette slots. Runs after
    // WorldGen.BindActivePalettes, which is what builds that palette.
    private void BindZoneKits(KitPalette kits)
    {
        TerrainKitData[] palette = kits.Kits;

        GroundSetData[] grounds = Map.GroundSets;
        _groundKits = new ZoneKits[grounds.Length];
        for (int i = 0; i < grounds.Length; i++)
        {
            _groundKits[i] = KitsOf(palette, grounds[i]);
        }
        _defaultKits = KitsOf(palette, Map.Data.defaultGround);
    }

    private static ZoneKits KitsOf(TerrainKitData[] palette, GroundSetData g)
    {
        // Every slot falls back to the surface one: a set that authors no shore
        // or cave kit should read as its own ground, not as slot 0's.
        byte surface = SlotOf(palette, g?.surfaceKit);
        return new ZoneKits
        {
            Surface = surface,
            Shore = g?.shoreKit != null ? SlotOf(palette, g.shoreKit) : surface,
            Submerged = g?.submergedKit != null ? SlotOf(palette, g.submergedKit) : surface,
            Cave = g?.caveKit != null ? SlotOf(palette, g.caveKit) : surface,
        };
    }

    // A ground set may name a kit the document's authored palette does not
    // carry — that kit has no slot, and silently falling back to 0 would
    // texture it as some other material.
    //
    // The fix is DATA, never appending the missing kit here: the per-voxel
    // TerrainId is an INDEX into this palette, so appending at bake time would
    // shift every index and mis-texture the whole world. Append the kit to
    // KitPaletteData instead, which is the one safe edit.
    private static byte SlotOf(TerrainKitData[] palette, TerrainKitData kit)
    {
        if (kit == null || palette == null)
        {
            return 0;
        }
        for (int i = 0; i < palette.Length; i++)
        {
            if (palette[i] == kit)
            {
                return (byte)i;
            }
        }
        GD.PushWarning($"WorldMapState: kit '{kit.ResourcePath}' is not in this document's kit "
            + "palette, so columns using it bake as palette slot 0. APPEND it to the KitPaletteData "
            + "(never insert or reorder), or drop the ground set that names it.");
        return 0;
    }

    // Re-stamp every column under a texel rect, recording changed voxels.
    public void StampColumns(Rect2I texelRect, List<Vector3I> changed)
    {
        int x0 = Mathf.Max(0, texelRect.Position.X);
        int z0 = Mathf.Max(0, texelRect.Position.Y);
        int x1 = Mathf.Min(Map.Data.ImageWidth, texelRect.Position.X + texelRect.Size.X);
        int z1 = Mathf.Min(Map.Data.ImageHeight, texelRect.Position.Y + texelRect.Size.Y);
        for (int px = x0; px < x1; px++)
        {
            for (int pz = z0; pz < z1; pz++)
            {
                StampColumn(px, pz, changed);
            }
        }
    }

    private void StampColumn(int px, int pz, List<Vector3I> changed)
    {
        int wx = Map.Data.WorldMinX + px;
        int wz = Map.Data.WorldMinZ + pz;
        int th = Map.TerrainHeight(px, pz);
        int wsurf = Map.WaterSurface(px, pz);

        // Resolved once for the column: paving is one voxel, and resolving a
        // surface-seated level walks the column.
        BlockData paving = Map.PavingAt(px, pz);
        int pavedY = paving != null ? Map.PavedYAt(px, pz) : Map.Data.WorldMinY - 1;

        // Which of the column's zone kits its ground is made of. Painting a zone
        // changes the material, not just the chunk's runtime behaviour — without
        // this every painted world came out in whichever kit happened to land in
        // palette slot 0.
        ZoneKits kits = KitsAt(px, pz);
        byte topKit = wsurf > th
            ? kits.Submerged
            : th - ShoreWaterSurface(px, pz) <= Map.Data.shoreBandVoxels ? kits.Shore : kits.Surface;

        // The chunk is resolved once per 16 voxels of column instead of per
        // write. WorldState's world-space setters each hash a Vector3I, and the
        // three-argument SetBlockWorld hashes three times (it reads the block
        // and the shape before writing) — ~3.5 dictionary lookups per voxel over
        // every voxel in the world, which was the whole cost of this pass.
        int lx = wx & ChunkGrid.MASK;
        int lz = wz & ChunkGrid.MASK;
        int cx = wx >> ChunkGrid.SHIFT;
        int cz = wz >> ChunkGrid.SHIFT;
        ChunkState chunk = null;
        int chunkCy = int.MinValue;

        for (int wy = Map.Data.WorldMinY; wy <= Map.Data.WorldMaxY; wy++)
        {
            int cy = wy >> ChunkGrid.SHIFT;
            if (cy != chunkCy)
            {
                chunkCy = cy;
                chunk = WorldState.GetChunk(new Vector3I(cx, cy, cz));
            }
            if (chunk == null)
            {
                continue;
            }
            int ly = wy & ChunkGrid.MASK;

            int desired;
            byte edit = Map.VoxelEdit(px, pz, wy);
            if (edit == WorldMapState.EditCarve)
            {
                // Carve removes GROUND; what stands in the space it opens is the
                // water layer's business, exactly as it is above the terrain. So
                // a passage bored below a painted surface comes out flooded —
                // which is the only way to paint water in a tunnel at all, and it
                // is undone the same way it is anywhere else, by erasing the
                // column's water. (Per COLUMN, so a dry passage cannot run under
                // a lake: the erase that drains the tunnel drains the lake too.)
                desired = wy <= wsurf ? Blocks.DefaultWaterId : Blocks.AirId;
            }
            else if (wy <= th || edit == WorldMapState.EditAdd)
            {
                // Added geometry stands above the painted ground, so it takes
                // the zone's surface kit — or its submerged one where the new
                // voxel sits below a water surface.
                byte kit = wy > th
                    ? (wy <= wsurf ? kits.Submerged : kits.Surface)
                    : th - wy <= Map.Data.surfaceDepthVoxels ? topKit : kits.Cave;
                chunk.SetTerrainId(lx, ly, lz, kit);
                desired = WorldState.Kits.BlockFor(kit);
                if (wy == pavedY)
                {
                    // Paving replaces the kit's block on ONE voxel — the floor
                    // it was laid on, which is the top of the column for a road
                    // on open ground and the floor of a passage or the ground
                    // under an arch for one laid beneath the cutaway. Paving is
                    // a surface, so the rock under a road is still the
                    // hillside's, and the kit channel keeps its own value: it is
                    // what the column IS made of, which a road laid over it does
                    // not change.
                    desired = paving.blockId;
                }
            }
            else if (wy <= wsurf)
            {
                desired = Blocks.DefaultWaterId;
            }
            else
            {
                desired = Blocks.AirId;
            }

            int current = chunk.Voxels[lx, ly, lz];
            if (changed != null)
            {
                if (current == desired)
                {
                    continue;
                }
                changed.Add(new Vector3I(wx, wy, wz));
            }

            // Ground snaps on Y so the painted terraces read as clean steps;
            // air and water take their block's own default — and a voxel whose
            // block did not move keeps the shape it already had, which is what
            // WorldState's three-argument setter does.
            SharpAxes shape = Blocks.IsNaturalGround(desired)
                ? SharpAxes.Y
                : current == desired ? (SharpAxes)chunk.Shape[lx, ly, lz] : Blocks.DefaultShape(desired);
            chunk.Voxels[lx, ly, lz] = (byte)desired;
            chunk.Shape[lx, ly, lz] = (byte)shape;
        }
    }

    // The water a column could have a BEACH against: its own surface, or the
    // highest standing in the four columns around it.
    //
    // Measured from the water rather than from seaLevel, because there is no
    // waterline to measure from any more — and because seaLevel was the wrong
    // reference in both directions anyway: it sanded the floor of a dry basin
    // dug below zero, and it never gave a mountain lake a shore at all. A column
    // with no water anywhere near it comes out far above NoWater's Y and takes
    // the surface kit, which is the answer it wanted.
    private int ShoreWaterSurface(int px, int pz)
    {
        int best = Map.WaterSurface(px, pz);
        for (int d = 0; d < 4; d++)
        {
            best = Mathf.Max(best, Map.WaterSurface(px + WorldMapState.NeighbourDx[d], pz + WorldMapState.NeighbourDz[d]));
        }
        return best;
    }

    // Re-evaluate scatter for every column under a texel rect: drop the old
    // entity (if any), then place a new one when the cell has a kind + the
    // hash roll falls under its density, on dry land. Adds/removes sim states on
    // WorldState during the bake.
    public void RescatterColumns(Rect2I texelRect)
    {
        int x0 = Mathf.Max(0, texelRect.Position.X);
        int z0 = Mathf.Max(0, texelRect.Position.Y);
        int x1 = Mathf.Min(Map.Data.ImageWidth, texelRect.Position.X + texelRect.Size.X);
        int z1 = Mathf.Min(Map.Data.ImageHeight, texelRect.Position.Y + texelRect.Size.Y);
        for (int px = x0; px < x1; px++)
        {
            for (int pz = z0; pz < z1; pz++)
            {
                RescatterColumn(px, pz);
            }
        }
    }

    private void RescatterColumn(int px, int pz)
    {
        if (!Map.CanSpawnAt(px, pz))
        {
            return;
        }
        // Both spawn layers run the same column routine — a mob set is a set
        // whose tree and foliage slots happen to be empty.
        ScatterColumn(Map.PropSetAt(px, pz, out float propDensity), propDensity, px, pz);
        ScatterColumn(Map.MobSetAt(px, pz, out float mobDensity), mobDensity, px, pz);
    }

    private void ScatterColumn(SpawnSetData set, float density, int px, int pz)
    {
        if (set == null)
        {
            return;
        }

        int surfaceY = Map.TerrainHeight(px, pz);
        var pos = new Vector3(Map.Data.WorldMinX + px + 0.5f, surfaceY + 1f, Map.Data.WorldMinZ + pz + 0.5f);

        // Canopy and ground cover roll separately, each at its own rate.
        // Anchored at +1.5, not +1: the mesher's shallow-Y smoothing lifts a
        // flat column's visible top half a voxel, so +1 buries a sprite in the
        // ground. Worldgen carries the same constant for the same reason.
        var propPos = new Vector3(pos.X, surfaceY + 1.5f, pos.Z);
        if (Map.TreeAt(set, px, pz, density))
        {
            PlaceProp(set.treeScenes, PropType.Tree, WorldMapState.TREE_SALT, px, pz, propPos, 0f);
        }
        if (Map.GrassAt(set, px, pz, density))
        {
            PlaceProp(set.foliageScenes, PropType.Foliage, WorldMapState.GRASS_SALT, px, pz, propPos, set.positionJitter);
        }

        // Entities: each entry's OWN authored rate, then its own Spawn logic.
        // The hash decides placement; the seeded Random only fills in details,
        // so the map preview above stays exact.
        SpawnListRow[] rows = set.RowsFlat;
        if (rows == null)
        {
            return;
        }
        for (int i = 0; i < rows.Length; i++)
        {
            SpawnListRow row = rows[i];
            if (row?.entry == null)
            {
                continue;
            }
            uint h = WorldMapState.Hash(px, pz, WorldMapState.ENTITY_SALT + (uint)i);
            if (!WorldMapState.AreaRoll(h, row.squareMetersPerSpawn, density))
            {
                continue;
            }
            row.TrySpawn(WorldState, pos, new System.Random((int)h), SpawnContextForBake());
        }
    }

    private void PlaceProp(WeightedScene[] scenes, PropType type, uint salt, int px, int pz, Vector3 pos, float jitter)
    {
        WeightedList<PackedScene> w = WeightedScene.BuildList(scenes);
        if (w.Count == 0)
        {
            return;
        }
        if (jitter > 0f)
        {
            pos = new Vector3(
                pos.X + (WorldMapState.ToFloat01(WorldMapState.Hash(px, pz, salt + 3u)) * 2f - 1f) * jitter,
                pos.Y,
                pos.Z + (WorldMapState.ToFloat01(WorldMapState.Hash(px, pz, salt + 4u)) * 2f - 1f) * jitter);
        }
        WorldState.AddEntity(new PropSimState(type, pos, w.Choose(WorldMapState.ToFloat01(WorldMapState.Hash(px, pz, salt + 1u)) * w.TotalWeight))
        {
            RotationY = WorldMapState.ToFloat01(WorldMapState.Hash(px, pz, salt + 2u)) * Mathf.Tau,
        });
    }

    private SpawnContext _bakeContext;

    // Minimal context: the three column queries entries ask about, answered off
    // the painted document rather than a HeightMap.
    private SpawnContext SpawnContextForBake()
    {
        int levelCap = Map.Data.finish.mobLevelCap;
        return _bakeContext ??= new SpawnContext
        {
            SurfaceYAt = GroundYAtWorld,
            IsValidColumn = (wx, wz) => Map.CanSpawnAt(wx - Map.Data.WorldMinX, wz - Map.Data.WorldMinZ),
            IsFlatColumn = (wx, wz) => Map.IsFlatAt(wx - Map.Data.WorldMinX, wz - Map.Data.WorldMinZ),
            // The painted difficulty layer, which is the only thing that knows a
            // mob's level in a world nothing generated.
            MobLevelOverride = (pos, baseLevel) =>
                Mathf.Clamp(baseLevel + Map.MobLevelAtWorld(pos), 0, levelCap),
            // A forge takes its tier from the SAME painted layer. Its own scale
            // matches the mob one by design ("a forge sits at the same tier as
            // monsters in its zone"), and the danger you painted is the only
            // statement about difficulty a painted world contains.
            ForgeLevelOverride = pos => Mathf.Clamp(Map.MobLevelAtWorld(pos), 0, levelCap),
        };
    }

    // The moss coverage of the kits under a world-space column: the surface kit
    // for open ground and cliff faces, the cave kit for anything cut into rock.
    private (float surface, float cave) MossCoverageAtWorld(int wx, int wz)
    {
        ZoneKits kits = KitsAt(wx - Map.Data.WorldMinX, wz - Map.Data.WorldMinZ);
        KitPalette palette = WorldState?.Kits;
        if (palette == null)
        {
            return (0f, 0f);
        }
        return (palette.KitAt(kits.Surface)?.mossCoverage ?? 0f,
                palette.KitAt(kits.Cave)?.mossCoverage ?? 0f);
    }

    // Painted ground, else the document's default. The zone has no say in the
    // material any more — that is the whole point of splitting them.
    private ZoneKits KitsAt(int px, int pz)
    {
        int g = Map.GroundIndexAt(px, pz);
        return g >= 0 && _groundKits != null && g < _groundKits.Length ? _groundKits[g] : _defaultKits;
    }

    private int ZoneIndexAt(int px, int pz)
    {
        Vector2I ct = Map.Data.ColumnTexelToChunkTexel(px, pz);
        int idx = Mathf.RoundToInt(Map.Zone.GetPixel(ct.X, ct.Y).R * 255f);
        return Map.ZoneCount > 0 ? Mathf.Clamp(idx, 0, Map.ZoneCount - 1) : 0;
    }

    // — see LightEngine.LIGHT_VERSION), and it has to see the tree canopy, which
    // only FoliageStamper knows and which needs the main thread
    // (PackedScene.Instantiate). Build and relight are the long passes and want
    // a worker thread. So a caller that is NOT the main thread drives the three
    // in order and hops threads for the stamp — see WorldMapPainter.SaveAndBake.
    // This wrapper is for a main-thread caller, where the split does not matter.
    public bool Bake(System.Action<float, string> progress = null)
    {
        return BakeBuild(progress) && BakeStampOccluders() && BakeRelightAndWrite(progress);
    }

    // Step 1 — the painted document to voxels. The long one.
    public bool BakeBuild(System.Action<float, string> progress = null)
    {
        if (string.IsNullOrEmpty(Map.Data.outputWorldPath))
        {
            GD.PrintErr("WorldMapState: no OutputWorldPath set, nothing to bake.");
            return false;
        }
        _bakeClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            progress?.Invoke(0f, "Building chunks");
            BuildWorld(progress);
            _bakeBuildMs = _bakeClock.ElapsedMilliseconds;
            return true;
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"WorldMapState: world export failed: {e}");
            return false;
        }
    }

    // Step 2 — MAIN THREAD ONLY. Rasterize the sun occluders (tree canopies,
    // roofs, entity voxels) so the relight below sees them. Milliseconds.
    public bool BakeStampOccluders()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            FoliageStamper.Stamp(WorldState);
            EntityVoxelStamper.Stamp(WorldState);
            _bakeStampMs = sw.ElapsedMilliseconds;
            return true;
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"WorldMapState: occluder stamp failed: {e}");
            return false;
        }
    }

    // Step 3 — the sun flood and the file write.
    public bool BakeRelightAndWrite(System.Action<float, string> progress = null)
    {
        try
        {
            progress?.Invoke(RELIGHT_START, "Lighting");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LightEngine.Relight(WorldState, r => progress?.Invoke(
                RELIGHT_START + r * (WRITE_FILE_START - RELIGHT_START), "Lighting"));
            long relightMs = sw.ElapsedMilliseconds;
            progress?.Invoke(WRITE_FILE_START, "Writing .hike");
            sw.Restart();
            WorldFile.Write(Map.Data.outputWorldPath, WorldState);
            long writeMs = sw.ElapsedMilliseconds;
            progress?.Invoke(1f, "Done");
            long ms = _bakeClock != null ? _bakeClock.ElapsedMilliseconds : 0;
            GD.Print($"WorldMapState: baked world to {Map.Data.outputWorldPath} in {ms}ms");
            // Phase breakdown, because a bake is minutes and "it was slow" is not
            // a place to start optimizing from.
            GD.Print($"[bake] build={_bakeBuildMs}ms occluderStamp={_bakeStampMs}ms relight={relightMs}ms write={writeMs}ms");
            return true;
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"WorldMapState: world export failed: {e}");
            return false;
        }
    }

    // Progress boundaries for the two passes that follow BuildWorld.
    private const float RELIGHT_START = 0.9f;
    private const float WRITE_FILE_START = 0.98f;

    private System.Diagnostics.Stopwatch _bakeClock;
    private long _bakeBuildMs;
    private long _bakeStampMs;

    private byte SampleChunkIndex(Image img, int cx, int cz, int count)
    {
        int lcx = cx - Map.Data.MinChunk.X;
        int lcz = cz - Map.Data.MinChunk.Z;
        if (lcx < 0 || lcx >= img.GetWidth() || lcz < 0 || lcz >= img.GetHeight())
        {
            return 0;
        }
        return WorldMapState.ClampIndex((byte)Mathf.RoundToInt(img.GetPixel(lcx, lcz).R * 255f), count);
    }

    // Before the wind layer existed the painter skipped this pass altogether, so
    // a baked .hike shipped a subgrid of stored zeros — signed zero, so not a
    // wrong wind, just no wind at all for the grass, the motes and mob drift to
    // read.
    private void StampWind(WorldState ws)
    {
        foreach (ChunkState chunk in ws._chunks.Values)
        {
            if (Map.WindForChunk(chunk.ChunkCoord.X, chunk.ChunkCoord.Z, out Vector3 dir, out float speed))
            {
                WindGen.FillChunkWind(chunk, dir * speed);
            }
            else
            {
                WindGen.ComputeChunkWind(ws, chunk);
            }
        }
    }

    private struct ZoneKits
    {
        public byte Surface;
        public byte Shore;
        public byte Submerged;
        public byte Cave;
    }

    private ZoneKits[] _groundKits;
    private ZoneKits _defaultKits;
}
