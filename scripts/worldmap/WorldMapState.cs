using System.Collections.Generic;
using Godot;

// Runtime document + bake for the world-map painter. Owns every layer's mutable
// data, the column/region/zone/tunnel queries the tools and views read, and the
// deterministic bake (BuildWorld) that turns the painted layers into a WorldState
// / .hike. The painter edits only the 2D layer images; no live voxel world is
// kept — the WorldState is materialized on demand at bake/save time.
//
// The elevation + water images REPLACE WorldGen's noise height/water; the rest
// of WorldGen's per-column logic (ramps, shore, kit blending) is out of scope,
// so this is a clean focused stamp rather than a fork of the 3100-line WorldGen.
public class WorldMapState
{
    public readonly WorldMapData Data;

    public WorldState WorldState;  // baked voxels (built on demand at bake time)

    public Image Elevation;        // Rf, per column (normalized height, truth)
    public Image Water;            // Rf, per column (water surface height)
    public Image Region;           // R8, per chunk (region index)
    public Image Zone;             // R8, per chunk (zone index)
    public Image Scatter;          // Rgba8, per column (R = prop set + 1, G = density)
    public Image Ground;           // R8, per column (ground set + 1; 0 = default ground)
    public Image Mobs;             // Rgba8, per column (R = mob set + 1, G = density)
    public Image Scalars;          // Rgba8, per column (R = mob level, G = climb)
    public byte[,,] Tunnels;       // [px, ly, pz] carve mask (1 = carved air)

    // Display-only: whether the views draw standing water over the terrain.
    // Off shows the bare banded height field, which is what you want while
    // shaping a lake bed or a coast you have already flooded. Not part of the
    // document — nothing here is saved.
    public bool ShowWater = true;

    // World Y of the waterline. Read-only: the elevation layer is signed around
    // it, so shorelines are painted rather than made by sliding the sea.
    public int SeaLevel => Data.seaLevel;

    // Palette slots for one zone's kits, resolved once per bake. The per-voxel
    // TerrainId is an index into WorldGen's active kit palette, so this is the
    // translation from "which zone is this chunk" to "which slot does its ground
    // use".
    private struct ZoneKits
    {
        public byte Surface;
        public byte Shore;
        public byte Submerged;
        public byte Cave;
    }

    private ZoneKits[] _groundKits;
    private ZoneKits _defaultKits;


    public WorldMapState(WorldMapData data)
    {
        Data = data;
        Elevation = data.LoadOrCreateElevation();
        Water = data.LoadOrCreateWater();
        Region = data.LoadOrCreateRegion();
        Zone = data.LoadOrCreateZone();
        Scatter = data.LoadOrCreateScatter();
        Ground = data.LoadOrCreateGround();
        Mobs = data.LoadOrCreateMobs();
        Scalars = data.LoadOrCreateScalars();
        Tunnels = data.LoadOrCreateTunnels();
    }

    // Names for the index layers, so the tools can talk about "swamp" instead of
    // "2". Regions carry an authored displayName; zones have no name field yet,
    // so the gen resource's own file name is their identity (swamp_gen.tres ->
    // swamp). Adding ZoneData.displayName later would supersede the fallback
    // without changing callers.
    public string ZoneName(int index)
    {
        ZoneData[] zones = Data.PaintableZones;
        if (index < 0 || index >= zones.Length)
        {
            return $"Zone {index}";
        }
        string file = FileStem(zones[index]?.ResourcePath);
        return string.IsNullOrEmpty(file) ? $"Zone {index}" : file;
    }

    public string RegionName(int index)
    {
        RegionGenData[] regions = Data.genData?.regions;
        if (regions == null || index < 0 || index >= regions.Length)
        {
            return $"Region {index}";
        }
        RegionGenData gen = regions[index];
        string authored = gen?.region?.displayName.ToString();
        if (!string.IsNullOrEmpty(authored))
        {
            return authored;
        }
        string file = FileStem(gen?.ResourcePath);
        return string.IsNullOrEmpty(file) ? $"Region {index}" : file;
    }

    private static string FileStem(string resourcePath)
    {
        return string.IsNullOrEmpty(resourcePath) ? "" : resourcePath.GetFile().GetBaseName();
    }

    public int RegionCount => Data.RegionCount;
    public int ZoneCount => Data.ZoneCount;

    // ---- Queries --------------------------------------------------------

    public int StepVoxels => Mathf.Max(1, Data.elevationStepVoxels);

    // Layer value (voxels relative to sea level) -> absolute world Y, clamped to
    // the document's range and snapped to the authoring lattice. EVERY height in
    // the painter passes through here, so the map, the brushes and the bake can
    // never disagree about where a step lands.
    public int ColumnHeight(float voxelsRelSea)
    {
        return SeaLevel + SnapVoxels(voxelsRelSea);
    }

    public int SnapVoxels(float voxelsRelSea)
    {
        // Clamped to the world's own vertical extent as well as the authored
        // range: seabed painted below the floor chunk simply would not exist,
        // and the column would bake as bottomless water instead of ground.
        float floor = Mathf.Max(Data.minElevationVoxels, Data.WorldMinY - SeaLevel);
        float ceil = Mathf.Min(Data.maxElevationVoxels, Data.WorldMaxY - SeaLevel);
        int step = StepVoxels;
        return Mathf.RoundToInt(Mathf.Clamp(voxelsRelSea, floor, ceil) / step) * step;
    }

    // Raw (unsnapped) layer value, in voxels relative to sea level. Brushes
    // accumulate against this so a stroke can build up to the next step.
    public float ElevationVoxels(int px, int pz)
    {
        return Elevation.GetPixel(ClampX(px), ClampZ(pz)).R;
    }

    public float WaterVoxels(int px, int pz)
    {
        return Water.GetPixel(ClampX(px), ClampZ(pz)).R;
    }

    // Signed lattice index of a column — 0 is the shore, +1 the first step up.
    // The map paints one distinct band per index.
    public int LevelAt(int px, int pz)
    {
        return SnapVoxels(ElevationVoxels(px, pz)) / StepVoxels;
    }

    // Elevation as a 0..1 fraction of the document's range, for views that just
    // want "how high is this" (zone brightness, scatter backdrop).
    public float ElevationFraction(int px, int pz)
    {
        float v = ElevationVoxels(px, pz);
        return Mathf.Clamp(Mathf.InverseLerp(Data.minElevationVoxels, Data.maxElevationVoxels, v), 0f, 1f);
    }

    // Topmost solid voxel of the painted terrain column.
    public int TerrainHeight(int px, int pz)
    {
        return ColumnHeight(ElevationVoxels(px, pz));
    }

    // Effective water surface: the higher of the ocean and any painted water
    // body. (A water value of 0 maps to SeaLevel, so this is >= SeaLevel — an
    // unpainted column is plain ocean.)
    public int WaterSurface(int px, int pz)
    {
        return Mathf.Max(SeaLevel, ColumnHeight(WaterVoxels(px, pz)));
    }

    // Top of what is actually VISIBLE at a column when water is drawn: the water
    // surface where any stands, the ground where it does not.
    public int VisibleSurface(int px, int pz)
    {
        return Mathf.Max(WaterSurface(px, pz), TerrainHeight(px, pz));
    }

    // Column has water standing above its terrain.
    public bool Underwater(int px, int pz)
    {
        return WaterSurface(px, pz) > TerrainHeight(px, pz);
    }

    // Terrain top sits below the ocean (open-sea floor).
    public bool Ocean(int px, int pz)
    {
        return TerrainHeight(px, pz) < SeaLevel;
    }

    public bool IsTunnel(int px, int pz, int wy)
    {
        int ly = wy - Data.WorldMinY;
        if (px < 0 || px >= Data.ImageWidth || pz < 0 || pz >= Data.ImageHeight
            || ly < 0 || ly >= Data.VoxelHeight)
        {
            return false;
        }
        return Tunnels[px, ly, pz] != 0;
    }

    public void SetTunnel(int px, int pz, int wy, bool carved)
    {
        int ly = wy - Data.WorldMinY;
        if (px < 0 || px >= Data.ImageWidth || pz < 0 || pz >= Data.ImageHeight
            || ly < 0 || ly >= Data.VoxelHeight)
        {
            return;
        }
        Tunnels[px, ly, pz] = (byte)(carved ? 1 : 0);
    }

    // Solid land (not carved) at a given Y — used by the tunnel view.
    public bool SolidAt(int px, int pz, int wy)
    {
        return wy <= TerrainHeight(px, pz) && !IsTunnel(px, pz, wy);
    }

    private int ClampX(int px) => Mathf.Clamp(px, 0, Data.ImageWidth - 1);
    private int ClampZ(int pz) => Mathf.Clamp(pz, 0, Data.ImageHeight - 1);

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
        // genData going missing is not hypothetical: WorldMapData is [Tool] and
        // WorldGenData is not, so the editor cannot instantiate this field as its
        // real type, reads it as empty, and writes the .tres back WITHOUT it the
        // next time it saves. See the [Tool] rule in the root CLAUDE.md, which
        // lists this very field as a known gap. Say so rather than throwing a
        // bare NullReferenceException from the middle of the bake.
        if (Data.genData == null)
        {
            throw new System.InvalidOperationException(
                $"WorldMapData.genData is null on {Data.ResourcePath}. The Godot editor strips this "
                + "reference when it saves (WorldMapData is [Tool], WorldGenData is not); restore the "
                + "genData line in the .tres.");
        }

        WorldGen.BindActivePalettes(Data.genData);
        Blocks.Bind();
        KitBlocks.Bind(WorldGen.ActiveKitPalette);

        BindZoneKits();

        var ws = new WorldState(Data.MinChunk, Data.MaxChunk, Data.genData.simData);

        // Runtime zone table comes from the PAINTED palette, so a chunk's stamped
        // index and WorldState.Zones[index] are the same list by construction.
        ZoneData[] zones = Data.PaintableZones;
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
        RegionGenData[] regions = Data.genData.regions ?? [];
        ws.Regions = new RegionState[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            ws.Regions[i] = new RegionState { Data = regions[i]?.region };
        }

        for (int cx = Data.MinChunk.X; cx <= Data.MaxChunk.X; cx++)
        {
            for (int cy = Data.MinChunk.Y; cy <= Data.MaxChunk.Y; cy++)
            {
                for (int cz = Data.MinChunk.Z; cz <= Data.MaxChunk.Z; cz++)
                {
                    var coord = new Vector3I(cx, cy, cz);
                    var chunk = new ChunkState(coord);
                    chunk.RegionIndex = SampleChunkIndex(Region, cx, cz, RegionCount);
                    chunk.ZoneIndex = SampleChunkIndex(Zone, cx, cz, ZoneCount);
                    ws._chunks[coord] = chunk;
                }
            }
        }

        WorldState = ws;

        // Stamped a strip at a time purely so the bake can report progress; the
        // result is identical to one whole-map call.
        int width = Data.ImageWidth;
        for (int px = 0; px < width; px++)
        {
            StampColumns(new Rect2I(px, 0, 1, Data.ImageHeight), null);
            if ((px & 15) == 0)
            {
                progress?.Invoke(STAMP_START + (STAMP_END - STAMP_START) * px / width, "Stamping terrain");
            }
        }

        // Turn the painted routes into climbable rock, running WORLDGEN'S OWN
        // pass over the world we just stamped: it takes the per-column answers it
        // cannot look up here (a route flag instead of a zone's coverage, the
        // painted water layer instead of a HeightMap) and does the rest itself —
        // the exposed-face walk, the run heights, the per-block growth table.
        // Unpatched, so a marked column's whole face is dressed rather than a
        // fraction of it. Must follow the terrain stamp: it finds walls by
        // walking exposed faces of real voxels.
        progress?.Invoke(STAMP_END, "Cutting climbing routes");
        WorldGen.StampClimbSurfaces(ws, Data.genData,
            (wx, wz) => ClimbRouteAt(wx - Data.WorldMinX, wz - Data.WorldMinZ) ? 1f : 0f,
            (wx, wz) => WaterSurface(wx - Data.WorldMinX, wz - Data.WorldMinZ),
            Data.climbRouteMinWallVoxels, false);

        // Scatter props/interactives into the fresh WorldState (Sim is null
        // here, so this only adds sim states — the painter's initial entity
        // load spawns the nodes).
        //
        // MobSpawnEntry asks WorldGen.ComputeMobLevel for its level, and that
        // reads state a Generate() run leaves behind — which a painted world
        // never produces. The override hands it the painted field instead, and
        // is cleared afterwards so nothing else inherits it.
        progress?.Invoke(STAMP_END, "Scattering entities");
        int levelCap = Data.genData.mobLevelCap;
        WorldGen.MobLevelOverride = (pos, baseLevel) =>
            Mathf.Clamp(baseLevel + MobLevelAtWorld(pos), 0, levelCap);
        try
        {
            RescatterColumns(new Rect2I(0, 0, Data.ImageWidth, Data.ImageHeight));
        }
        finally
        {
            WorldGen.MobLevelOverride = null;
        }

        int spawnH = TerrainHeight(-Data.WorldMinX, -Data.WorldMinZ);
        ws.Spawn = new Vector3(0.5f, spawnH + 2f, 0.5f);

        // NOT relit here, deliberately. Every consumer of a .hike relights it on
        // open — Main on both load branches, WorldEditor on both its open paths —
        // because the baked bytes are only as good as the lighting pipeline was
        // at save time. SkyExposure is not even serialized; the format assumes
        // the pass happens on load. Computing it here cost ~19s of a ~22s bake
        // and was discarded every time. A future consumer that loads a .hike
        // WITHOUT relighting would get a black world, and should relight rather
        // than move this back.
        progress?.Invoke(WRITE_START, "Writing .hike");
        return ws;
    }

    // Bake phase boundaries, as a fraction of the whole job. MEASURED: with the
    // lighting pass gone, stamping is nearly all of it and the file write the
    // rest.
    private const float STAMP_START = 0.05f;
    private const float STAMP_END = 0.80f;
    private const float WRITE_START = 0.85f;

    // Detail sprites — the grass blades and pebbles scattered over the ground
    // itself, as opposed to the props standing on it. Stamped on the TOP SOLID
    // voxel only, from the kit that voxel is made of, which is why this belongs
    // to the ground layer rather than to props: it is part of what the material
    // looks like up close.
    //
    // Same math as WorldGen.StampDetailScatter: a noise gate per kit, then
    // strength ramped from the kit's own floor to full across (threshold..1).
    private void StampDetail(int px, int pz, int wx, int wy, int wz, byte kitSlot)
    {
        TerrainKitData[] palette = WorldGen.ActiveKitPalette;
        if (palette == null || kitSlot >= palette.Length)
        {
            return;
        }
        TerrainKitData kit = palette[kitSlot];
        if (kit?.defaultDetail == null)
        {
            return;
        }
        float n = DetailNoise.GetNoise2D(wx * kit.detailNoiseFrequency, wz * kit.detailNoiseFrequency);
        if (n <= kit.detailNoiseThreshold)
        {
            return;
        }
        float t = (n - kit.detailNoiseThreshold) / Mathf.Max(0.0001f, 1f - kit.detailNoiseThreshold);
        int strength = Mathf.Clamp(
            kit.detailStrengthMin + (int)(t * (255 - kit.detailStrengthMin)), 0, 255);
        if (strength <= 0)
        {
            return;
        }
        WorldState.SetDetailGroupWorld(wx, wy, wz, DetailSlotOf(kit.defaultDetail));
        WorldState.SetDetailStrengthWorld(wx, wy, wz, (byte)strength);
    }

    // 1-based index into WorldGen's active detail palette; 0 means "no detail",
    // which is why the palette index is offset rather than used raw.
    private static byte DetailSlotOf(DetailGroupData group)
    {
        DetailGroupData[] palette = WorldGen.ActiveDetailPalette;
        if (group == null || palette == null)
        {
            return 0;
        }
        for (int i = 0; i < palette.Length; i++)
        {
            if (palette[i] == group)
            {
                return (byte)(i + 1);
            }
        }
        return 0;
    }

    private FastNoiseLite _detailNoise;

    // Fixed seed: a painted document has no world seed, and the scatter must
    // come out the same every bake.
    private FastNoiseLite DetailNoise => _detailNoise ??= new FastNoiseLite
    {
        NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
        Frequency = 1f,   // the kit multiplies the COORDINATES, as worldgen does
        FractalOctaves = 2,
        Seed = 0x5EED,
    };

    // Map every paintable ground set onto palette slots. Runs after
    // WorldGen.BindActivePalettes, which is what builds that palette.
    private void BindZoneKits()
    {
        TerrainKitData[] palette = WorldGen.ActiveKitPalette;

        GroundSetData[] grounds = GroundSets;
        _groundKits = new ZoneKits[grounds.Length];
        for (int i = 0; i < grounds.Length; i++)
        {
            _groundKits[i] = KitsOf(palette, grounds[i]);
        }
        _defaultKits = KitsOf(palette, Data.defaultGround);
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

    // The kit palette is built from the WORLD's zones, so a ground set may name a
    // kit no zone uses — that kit has no slot, and silently falling back to 0
    // would texture it as some other material.
    //
    // The fix is DATA, never appending the missing kit here: the per-voxel
    // TerrainId is an INDEX into this palette, and the game rebuilds the palette
    // from genData when it loads the .hike. A bake that appended kits would
    // shift every index and mis-texture the whole world. So a ground set may
    // only use kits reachable from the document's own genData zones.
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
        GD.PushWarning($"WorldMapState: kit '{kit.ResourcePath}' is not in this document's genData kit "
            + "palette, so columns using it bake as palette slot 0. Add a zone using that kit to the "
            + "WorldGenData, or drop the ground set that names it.");
        return 0;
    }

    // Re-stamp every column under a texel rect, recording changed voxels.
    public void StampColumns(Rect2I texelRect, List<Vector3I> changed)
    {
        int x0 = Mathf.Max(0, texelRect.Position.X);
        int z0 = Mathf.Max(0, texelRect.Position.Y);
        int x1 = Mathf.Min(Data.ImageWidth, texelRect.Position.X + texelRect.Size.X);
        int z1 = Mathf.Min(Data.ImageHeight, texelRect.Position.Y + texelRect.Size.Y);
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
        int wx = Data.WorldMinX + px;
        int wz = Data.WorldMinZ + pz;
        int th = TerrainHeight(px, pz);
        int wsurf = WaterSurface(px, pz);

        // Which of the column's zone kits its ground is made of. Painting a zone
        // changes the material, not just the chunk's runtime behaviour — without
        // this every painted world came out in whichever kit happened to land in
        // palette slot 0.
        ZoneKits kits = KitsAt(px, pz);
        byte topKit = wsurf > th
            ? kits.Submerged
            : th - SeaLevel <= Data.shoreBandVoxels ? kits.Shore : kits.Surface;

        for (int wy = Data.WorldMinY; wy <= Data.WorldMaxY; wy++)
        {
            int desired;
            if (IsTunnel(px, pz, wy))
            {
                desired = Blocks.AirId;   // carve wins (air pocket, no flood sim)
            }
            else if (wy <= th)
            {
                byte kit = th - wy <= Data.surfaceDepthVoxels ? topKit : kits.Cave;
                WorldState.SetTerrainIdWorld(wx, wy, wz, kit);
                desired = KitBlocks.ForKit(kit);
                if (wy == th)
                {
                    StampDetail(px, pz, wx, wy, wz, kit);
                }
            }
            else if (wy <= wsurf)
            {
                desired = Blocks.WaterId;
            }
            else
            {
                desired = Blocks.AirId;
            }

            if (changed != null)
            {
                if (WorldState.GetBlockWorld(wx, wy, wz) == desired)
                {
                    continue;
                }
                changed.Add(new Vector3I(wx, wy, wz));
            }

            // Ground snaps on Y so the painted terraces read as clean steps;
            // air and water take their block's own default.
            if (Blocks.IsNaturalGround(desired))
            {
                WorldState.SetBlockWorld(wx, wy, wz, desired, SharpAxes.Y);
            }
            else
            {
                WorldState.SetBlockWorld(wx, wy, wz, desired);
            }
        }
    }

    // ---- Spawn sets -----------------------------------------------------

    public SpawnSetData[] PropSets => Data.propSets ?? System.Array.Empty<SpawnSetData>();

    public SpawnSetData[] MobSets => Data.mobSets ?? System.Array.Empty<SpawnSetData>();

    public int MobLevelCount => Mathf.Max(1, Data.mobLevelColors?.Length ?? 1);

    // Painted difficulty at a column, CONTINUOUS in 0..MobLevelCount-1.
    //
    // Stored as a smooth field rather than whole levels because difficulty wants
    // a gradient — worldgen lerps it across a noise field, and a hard per-column
    // step means walking one pace makes the monsters 50% stronger. Smoothing
    // where it is PAINTED rather than at bake keeps the map honest: what the
    // colours show is what the mobs get, with no second transform in between.
    public float MobLevelAt(int px, int pz)
    {
        return Scalars.GetPixel(ClampX(px), ClampZ(pz)).R * (MobLevelCount - 1);
    }

    public void SetMobLevelAt(int px, int pz, float level)
    {
        Color c = Scalars.GetPixel(px, pz);
        float unit = Mathf.Clamp(level / Mathf.Max(1, MobLevelCount - 1), 0f, 1f);
        Scalars.SetPixel(px, pz, new Color(unit, c.G, c.B, 1f));
    }

    // Is a climbing route painted on this column's walls? A flag, not a
    // coverage: the author is marking WHERE a route is, and the procedural
    // "how much of this zone's rock is climbable" knob (ZoneGenData.climbCoverage)
    // is a different question that worldgen still answers for itself.
    public bool ClimbRouteAt(int px, int pz)
    {
        return Scalars.GetPixel(ClampX(px), ClampZ(pz)).G > 0.5f;
    }

    public void SetClimbRouteAt(int px, int pz, bool route)
    {
        Color c = Scalars.GetPixel(px, pz);
        Scalars.SetPixel(px, pz, new Color(c.R, route ? 1f : 0f, c.B, 1f));
    }

    // How far this column stands above its lowest 4-neighbour — the height of
    // the tallest wall it owns, and 0 on flat ground or at the foot of a step.
    // A route can only be painted where this qualifies, which is exactly the set
    // of edges the map inks: the tool paints what you can see.
    public int WallDropAt(int px, int pz)
    {
        int h = TerrainHeight(px, pz);
        int lowest = h;
        lowest = Mathf.Min(lowest, TerrainHeight(px - 1, pz));
        lowest = Mathf.Min(lowest, TerrainHeight(px + 1, pz));
        lowest = Mathf.Min(lowest, TerrainHeight(px, pz - 1));
        lowest = Mathf.Min(lowest, TerrainHeight(px, pz + 1));
        return h - lowest;
    }

    // World position -> painted level, for the bake's MobLevelOverride.
    public int MobLevelAtWorld(Vector3 pos)
    {
        // Rounded only here, at the point a mob needs a whole level.
        return Mathf.RoundToInt(MobLevelAt(
            Mathf.FloorToInt(pos.X) - Data.WorldMinX,
            Mathf.FloorToInt(pos.Z) - Data.WorldMinZ));
    }

    public GroundSetData[] GroundSets => Data.groundSets ?? System.Array.Empty<GroundSetData>();

    public PaintPresetData[] Presets => Data.presets ?? System.Array.Empty<PaintPresetData>();

    // Ground unpainted anywhere: deliberately a flat neutral rather than a guess
    // at the zone's kits, so it is obvious at a glance which ground you have
    // actually authored and which is still inherited.
    private static readonly Color UNPAINTED_GROUND = new Color(0.30f, 0.29f, 0.27f);

    // What the ground-type views paint: the painted set's own colour and NOTHING
    // of the height, so the colour answers one question. Height is carried
    // entirely by the step outlines in those views, which is why they draw every
    // step down to 1m. Water still composites over the top — a flooded column
    // reads as water first, whatever the ground under it is.
    public Color GroundColorAt(int px, int pz)
    {
        int idx = GroundIndexAt(px, pz);
        GroundSetData[] sets = GroundSets;
        Color c = idx >= 0 && idx < sets.Length && sets[idx] != null ? sets[idx].mapColor : UNPAINTED_GROUND;
        return WithWater(c, px, pz);
    }

    // Painted ground index, or -1 where the column inherits its zone's kits.
    public int GroundIndexAt(int px, int pz)
    {
        int idx = Mathf.RoundToInt(Ground.GetPixel(ClampX(px), ClampZ(pz)).R * 255f) - 1;
        return idx >= 0 && idx < GroundSets.Length ? idx : -1;
    }

    // The painted set at a column, or null. The raster stores index+1 so 0 can
    // mean "nothing painted here".
    public SpawnSetData PropSetAt(int px, int pz, out float density)
        => SetAt(Scatter, PropSets, px, pz, out density);

    public SpawnSetData MobSetAt(int px, int pz, out float density)
        => SetAt(Mobs, MobSets, px, pz, out density);

    private SpawnSetData SetAt(Image layer, SpawnSetData[] sets, int px, int pz, out float density)
    {
        Color cell = layer.GetPixel(ClampX(px), ClampZ(pz));
        int idx = Mathf.RoundToInt(cell.R * 255f) - 1;
        density = cell.G;
        return idx >= 0 && idx < sets.Length && density > 0f ? sets[idx] : null;
    }

    // The one place a spawn decision is made, so the map PREVIEW and the BAKE
    // cannot disagree. Deliberately a pure hash rather than a sequential Random:
    // worldgen rolls its lists off a running rng, where a column's outcome
    // depends on every column before it, and nothing can then be previewed
    // without re-running the whole pass. Hash decides WHERE, rng decides the
    // details (loot counts, rotations) once a spawn is committed.
    //
    // The comparison is the inverted-unit form of SpawnEntryData.RollAreaChance
    // (rng.NextDouble() * sqm < 1), so both agree on what a rate means.
    public static bool AreaRoll(uint hash, float squareMetersPerSpawn, float density)
    {
        if (squareMetersPerSpawn <= 0f || density <= 0f)
        {
            return false;
        }
        return ToFloat01(hash) < density / squareMetersPerSpawn;
    }

    // Does anything spawn at this column, and from which palette entry? Drives
    // the map's dots; returns -1 for nothing.
    public int PreviewSpawnAt(int px, int pz) => PreviewAt(PropSetAt(px, pz, out float d), d, PropSets, px, pz);

    public int PreviewMobAt(int px, int pz) => PreviewAt(MobSetAt(px, pz, out float d), d, MobSets, px, pz);

    private int PreviewAt(SpawnSetData set, float density, SpawnSetData[] sets, int px, int pz)
    {
        if (set == null || !CanSpawnAt(px, pz))
        {
            return -1;
        }
        if (TreeAt(set, px, pz, density) || GrassAt(set, px, pz, density))
        {
            return IndexOfSet(sets, set);
        }
        SpawnEntryData[] entries = set.EntriesFlat;
        if (entries != null)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] != null
                    && AreaRoll(Hash(px, pz, ENTITY_SALT + (uint)i), entries[i].squareMetersPerSpawn, density))
                {
                    return IndexOfSet(sets, set);
                }
            }
        }
        return -1;
    }

    // --- Placement, matching WorldGen.GenerateProps exactly ------------------
    //
    // Trees are TWO passes, as they are there: a per-chunk scatter of
    // treesPerChunkMin..Max attempts at random cells, plus forest pockets whose
    // per-column odds are forestDensity * (f - threshold) / (1 - threshold)
    // wherever the noise clears the threshold. Grass is a single gate with NO
    // roll — every admitted column carries it, which is what makes worldgen's
    // grass read as solid clumps rather than a sprinkle.
    //
    // Worldgen rolls these off a running Random; here every decision is a hash
    // of the column (or the chunk), because the map preview has to reach the
    // same answer without replaying the whole pass. Same curve, same constants,
    // reproducible per column.

    public bool TreeAt(SpawnSetData set, int px, int pz, float density)
    {
        if (set == null || set.treeScenes.Length == 0)
        {
            return false;
        }
        float f = set.ForestNoise.GetNoise2D(Data.WorldMinX + px, Data.WorldMinZ + pz);
        if (f >= set.forestThreshold)
        {
            float t = Mathf.Clamp((f - set.forestThreshold) / Mathf.Max(0.0001f, 1f - set.forestThreshold), 0f, 1f);
            if (ToFloat01(Hash(px, pz, TREE_SALT)) < set.forestDensity * t * density)
            {
                return true;
            }
        }
        // The per-chunk scatter, which is what puts lone trees outside any wood.
        // Resolved for the whole chunk at once and cached: a column cannot tell
        // on its own whether it was one of that chunk's picks.
        return ChunkScatterCells(set, FloorDiv(px, ChunkState.SIZE), FloorDiv(pz, ChunkState.SIZE), density)
            .Contains(Mod(px, ChunkState.SIZE) * ChunkState.SIZE + Mod(pz, ChunkState.SIZE));
    }

    public bool GrassAt(SpawnSetData set, int px, int pz, float density)
    {
        if (set == null || set.foliageScenes.Length == 0)
        {
            return false;
        }
        if (set.GrassNoise.GetNoise2D(Data.WorldMinX + px, Data.WorldMinZ + pz) < set.grassThreshold)
        {
            return false;
        }
        // Worldgen places on every admitted column; painted density is the only
        // extra term, so a half-painted region thins rather than cutting off.
        return density >= 1f || ToFloat01(Hash(px, pz, GRASS_SALT)) < density;
    }

    private readonly System.Collections.Generic.Dictionary<(SpawnSetData, int, int), System.Collections.Generic.HashSet<int>> _chunkScatter = new();

    private System.Collections.Generic.HashSet<int> ChunkScatterCells(SpawnSetData set, int cx, int cz, float density)
    {
        var key = (set, cx, cz);
        if (_chunkScatter.TryGetValue(key, out var cells))
        {
            return cells;
        }
        cells = new System.Collections.Generic.HashSet<int>();
        int span = set.treesPerChunkMax - set.treesPerChunkMin + 1;
        if (span > 0)
        {
            int count = set.treesPerChunkMin + (int)(ToFloat01(Hash(cx, cz, CHUNK_SALT)) * span);
            count = Mathf.RoundToInt(count * Mathf.Clamp(density, 0f, 1f));
            for (int i = 0; i < count; i++)
            {
                // Worldgen picks cells in [1, SIZE-1) — never the chunk border.
                int lx = 1 + (int)(ToFloat01(Hash(cx, cz, CHUNK_SALT + (uint)(i * 2 + 1))) * (ChunkState.SIZE - 2));
                int lz = 1 + (int)(ToFloat01(Hash(cx, cz, CHUNK_SALT + (uint)(i * 2 + 2))) * (ChunkState.SIZE - 2));
                cells.Add(lx * ChunkState.SIZE + lz);
            }
        }
        _chunkScatter[key] = cells;
        return cells;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ((a + 1) / b) - 1;

    private static int Mod(int a, int b) => ((a % b) + b) % b;

    private static int IndexOfSet(SpawnSetData[] sets, SpawnSetData set)
    {
        for (int i = 0; i < sets.Length; i++)
        {
            if (sets[i] == set)
            {
                return i;
            }
        }
        return -1;
    }

    // Dry land, above the waterline — the only ground anything is placed on.
    public bool CanSpawnAt(int px, int pz)
    {
        return !Underwater(px, pz) && TerrainHeight(px, pz) >= SeaLevel;
    }

    // Independent salts: the two slots must roll independently, or every tree
    // would stand in a tuft of grass and every gap would be bare.
    // Practical maximum of the Perlin fields the sets use, measured across the
    // whole map on every authored set (0.67..0.75).
    private const uint TREE_SALT = 0x9E37u;
    private const uint GRASS_SALT = 0x2545u;
    private const uint CHUNK_SALT = 0x7F4Au;
    private const uint ENTITY_SALT = 0x85EBu;

    // Re-evaluate scatter for every column under a texel rect: drop the old
    // entity (if any), then place a new one when the cell has a kind + the
    // hash roll falls under its density, on dry land. Adds/removes sim states on
    // WorldState during the bake.
    public void RescatterColumns(Rect2I texelRect)
    {
        int x0 = Mathf.Max(0, texelRect.Position.X);
        int z0 = Mathf.Max(0, texelRect.Position.Y);
        int x1 = Mathf.Min(Data.ImageWidth, texelRect.Position.X + texelRect.Size.X);
        int z1 = Mathf.Min(Data.ImageHeight, texelRect.Position.Y + texelRect.Size.Y);
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
        if (!CanSpawnAt(px, pz))
        {
            return;
        }
        // Both spawn layers run the same column routine — a mob set is a set
        // whose tree and foliage slots happen to be empty.
        ScatterColumn(PropSetAt(px, pz, out float propDensity), propDensity, px, pz);
        ScatterColumn(MobSetAt(px, pz, out float mobDensity), mobDensity, px, pz);
    }

    private void ScatterColumn(SpawnSetData set, float density, int px, int pz)
    {
        if (set == null)
        {
            return;
        }

        int surfaceY = TerrainHeight(px, pz);
        var pos = new Vector3(Data.WorldMinX + px + 0.5f, surfaceY + 1f, Data.WorldMinZ + pz + 0.5f);

        // Canopy and ground cover roll separately, each at its own rate.
        // Anchored at +1.5, not +1: the mesher's shallow-Y smoothing lifts a
        // flat column's visible top half a voxel, so +1 buries a sprite in the
        // ground. Worldgen carries the same constant for the same reason.
        var propPos = new Vector3(pos.X, surfaceY + 1.5f, pos.Z);
        if (TreeAt(set, px, pz, density))
        {
            PlaceProp(set.treeScenes, PropType.Tree, TREE_SALT, px, pz, propPos, 0f);
        }
        if (GrassAt(set, px, pz, density))
        {
            PlaceProp(set.foliageScenes, PropType.Foliage, GRASS_SALT, px, pz, propPos, set.positionJitter);
        }

        // Entities: each entry's OWN authored rate, then its own Spawn logic.
        // The hash decides placement; the seeded Random only fills in details,
        // so the map preview above stays exact.
        SpawnEntryData[] entries = set.EntriesFlat;
        if (entries == null)
        {
            return;
        }
        for (int i = 0; i < entries.Length; i++)
        {
            SpawnEntryData entry = entries[i];
            if (entry == null)
            {
                continue;
            }
            uint h = Hash(px, pz, ENTITY_SALT + (uint)i);
            if (!AreaRoll(h, entry.squareMetersPerSpawn, density))
            {
                continue;
            }
            entry.TrySpawn(WorldState, pos, new System.Random((int)h), SpawnContextForBake());
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
                pos.X + (ToFloat01(Hash(px, pz, salt + 3u)) * 2f - 1f) * jitter,
                pos.Y,
                pos.Z + (ToFloat01(Hash(px, pz, salt + 4u)) * 2f - 1f) * jitter);
        }
        WorldState.AddEntity(new PropSimState(type, pos, w.Choose(ToFloat01(Hash(px, pz, salt + 1u)) * w.TotalWeight))
        {
            RotationY = ToFloat01(Hash(px, pz, salt + 2u)) * Mathf.Tau,
        });
    }

    private SpawnContext _bakeContext;

    // Minimal context: the three column queries entries ask about, answered off
    // the painted document rather than a HeightMap.
    private SpawnContext SpawnContextForBake()
    {
        return _bakeContext ??= new SpawnContext
        {
            SurfaceYAt = (wx, wz) => TerrainHeight(wx - Data.WorldMinX, wz - Data.WorldMinZ),
            IsValidColumn = (wx, wz) => CanSpawnAt(wx - Data.WorldMinX, wz - Data.WorldMinZ),
            IsFlatColumn = (wx, wz) => IsFlatAt(wx - Data.WorldMinX, wz - Data.WorldMinZ),
        };
    }

    private bool IsFlatAt(int px, int pz)
    {
        int h = TerrainHeight(px, pz);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (TerrainHeight(px + dx, pz + dz) != h)
                {
                    return false;
                }
            }
        }
        return true;
    }

    // Painted ground, else the document's default. The zone has no say in the
    // material any more — that is the whole point of splitting them.
    private ZoneKits KitsAt(int px, int pz)
    {
        int g = GroundIndexAt(px, pz);
        return g >= 0 && _groundKits != null && g < _groundKits.Length ? _groundKits[g] : _defaultKits;
    }

    private int ZoneIndexAt(int px, int pz)
    {
        Vector2I ct = Data.ColumnTexelToChunkTexel(px, pz);
        int idx = Mathf.RoundToInt(Zone.GetPixel(ct.X, ct.Y).R * 255f);
        return ZoneCount > 0 ? Mathf.Clamp(idx, 0, ZoneCount - 1) : 0;
    }

    private static uint Hash(int x, int z, uint salt = 0u)
    {
        unchecked
        {
            uint h = (uint)x * 0x9E3779B1u;
            h ^= (uint)z * 0x85EBCA77u;
            h ^= salt * 0xC2B2AE35u;
            h = ((h >> 16) ^ h) * 0x045D9F3Bu;
            h = ((h >> 16) ^ h) * 0x045D9F3Bu;
            return (h >> 16) ^ h;
        }
    }

    private static float ToFloat01(uint h) => (h & 0xFFFFFFu) / 16777216f;

    // Save the authored document — the layer images and nothing else. Cheap, and
    // deliberately NOT a bake: baking builds every chunk, stamps ~7M voxels,
    // relights the world and writes a ~57MB .hike, which is minutes of work that
    // has nothing to do with not losing your painting. Bake() is the explicit
    // second step, so the cost is paid when you want a world, not every time you
    // save your work.
    public void Save()
    {
        Data.SaveElevation(Elevation);
        Data.SaveWater(Water);
        Data.SaveRegion(Region);
        Data.SaveZone(Zone);
        Data.SaveScatter(Scatter);
        Data.SaveGround(Ground);
        Data.SaveMobs(Mobs);
        Data.SaveScalars(Scalars);
        Data.SaveTunnels(Tunnels);
        GD.Print("WorldMapState: saved layers");
    }

    // Materialize the painted document into a WorldState and write the .hike.
    // Returns false if it could not (no output path, or the bake threw).
    public bool Bake(System.Action<float, string> progress = null)
    {
        if (string.IsNullOrEmpty(Data.outputWorldPath))
        {
            GD.PrintErr("WorldMapState: no OutputWorldPath set, nothing to bake.");
            return false;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            progress?.Invoke(0f, "Building chunks");
            BuildWorld(progress);
            WorldFile.Write(Data.outputWorldPath, WorldState);
            progress?.Invoke(1f, "Done");
            GD.Print($"WorldMapState: baked world to {Data.outputWorldPath} in {sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"WorldMapState: world export failed: {e}");
            return false;
        }
    }

    private byte SampleChunkIndex(Image img, int cx, int cz, int count)
    {
        int lcx = cx - Data.MinChunk.X;
        int lcz = cz - Data.MinChunk.Z;
        if (lcx < 0 || lcx >= img.GetWidth() || lcz < 0 || lcz >= img.GetHeight())
        {
            return 0;
        }
        return ClampIndex((byte)Mathf.RoundToInt(img.GetPixel(lcx, lcz).R * 255f), count);
    }

    private static byte ClampIndex(byte idx, int count)
    {
        if (count <= 0)
        {
            return 0;
        }
        return idx >= count ? (byte)(count - 1) : idx;
    }

    // ---- Shared palette / colours (used by the views) -------------------

    public static Color RegionColor(int idx)
    {
        if (idx <= 0)
        {
            return new Color(0.22f, 0.22f, 0.24f);
        }
        return Color.FromHsv((idx * 0.61803398875f) % 1f, 0.55f, 0.85f);
    }

    public static Color ZoneColor(int idx)
    {
        return Color.FromHsv((idx * 0.61803398875f + 0.13f) % 1f, 0.45f, 0.9f);
    }

    // Hillshade of the RAW (unsnapped) height field: the smooth surface the
    // author is sculpting, so the map reads as landform. Deliberately not the
    // snapped field — a terraced height field has zero gradient across each
    // plateau and would shade as flat slabs; the steps are drawn as edge
    // outlines instead, which is the job they do well.
    // 1 texel == 1 metre, so the gradient is a plain central difference.
    public float ReliefShade(int px, int pz, Vector3 light)
    {
        float hl = ElevationVoxels(px - 1, pz);
        float hr = ElevationVoxels(px + 1, pz);
        float hd = ElevationVoxels(px, pz - 1);
        float hu = ElevationVoxels(px, pz + 1);
        var n = new Vector3(-(hr - hl) * 0.5f, 1f, -(hu - hd) * 0.5f).Normalized();
        return Mathf.Max(n.Dot(light), 0f);
    }

    // Standing water, honouring ShowWater. OPAQUE by design: the elevation band
    // underneath must not read through, or the map says "low ground" and
    // "underwater" in the same colour language. Just two shades — the shallows
    // you can wade, and everything below them.
    public Color WithWater(Color terrain, int px, int pz)
    {
        if (!ShowWater)
        {
            return terrain;
        }
        int depth = WaterSurface(px, pz) - TerrainHeight(px, pz);
        if (depth <= 0)
        {
            return terrain;
        }
        return depth <= Data.shallowWaterDepth ? Data.shallowWaterColor : Data.deepWaterColor;
    }

    // Water is drawn flat, so the painter skips relief shading on it.
    public bool IsSubmerged(int px, int pz)
    {
        return ShowWater && Underwater(px, pz);
    }

    // Colour of one column: the authored hue for its 4-metre band, shaded by
    // which metre of the band it sits on. Both halves of the pair carry meaning,
    // so a 1m step reads as a shade change and a 4m step as a hue change, and
    // neither depends on the ramp being wide enough to see — which is what the
    // old green-to-white hypsometric ramp failed at, since neighbouring steps
    // differed by a few percent across dozens of levels.
    public Color ElevationColor(int px, int pz)
    {
        return ElevationColorAt(SnapVoxels(ElevationVoxels(px, pz)));
    }

    // Same palette, addressed by height rather than by column — the brush cursor
    // shows the height it is about to write, which is not on the map yet.
    public Color ElevationColorAt(int voxelsRelSea)
    {
        Color[] hues = Data.elevationBandHues;
        if (hues == null || hues.Length == 0)
        {
            return new Color(0.5f, 0.5f, 0.5f);
        }
        int v = voxelsRelSea;
        int per = Mathf.Max(1, Data.metersPerBand);

        // Floor division, not C# truncation: heights go negative below the
        // waterline and -1 must land in the band BELOW zero, not in band 0.
        int band = v >= 0 ? v / per : ((v + 1) / per) - 1;
        int within = v - band * per;   // always 0..per-1

        // The authored colour is the band's BASE — its lowest metre — and each
        // metre above lifts every channel by a fraction of that channel's own
        // headroom to white. So a base of (0, 0.4, 0) walks
        // (0,0.4,0) (0.25,0.55,0.25) (0.5,0.7,0.5) (0.75,0.85,0.75): the hue
        // stays recognisably itself while getting steadily paler, and the step
        // is visible even in a channel that started near full.
        Color baseColor = hues[((band % hues.Length) + hues.Length) % hues.Length];
        float lift = within / (float)per;
        return new Color(
            baseColor.R + (1f - baseColor.R) * lift,
            baseColor.G + (1f - baseColor.G) * lift,
            baseColor.B + (1f - baseColor.B) * lift);
    }
}
