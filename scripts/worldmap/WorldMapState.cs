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
// One layer image plus how many painted texels each of its pixels covers.
public readonly struct RasterLayer
{
    public readonly Godot.Image Image;
    public readonly int TexelsPerPixel;

    public RasterLayer(Godot.Image image, int texelsPerPixel)
    {
        Image = image;
        TexelsPerPixel = texelsPerPixel;
    }
}

public class WorldMapState
{
    public readonly WorldMapData Data;

    // The weathered height/water field derived from the elevation, water and
    // roughness layers. A cache with an invalidation protocol, which is why it
    // is a type and not four loose arrays: every path that edits those layers
    // owes it an Invalidate.
    public readonly TerrainField Field;

    // Highest and lowest edited voxel per column, derived from Tunnels. The
    // second cache with an invalidation protocol; see VoxelEditOverlay.
    public readonly VoxelEditOverlay Edits;

    public Image Elevation;        // Rf, per column (normalized height, truth)
    public Image Water;            // Rf, per column (water surface height)
    public Image Region;           // R8, per chunk (region index)
    public Image Zone;             // R8, per chunk (zone index)
    public Image Wind;             // Rgba8, per chunk (R = angle, G = strength; G 0 = unpainted)
    public Image Scatter;          // Rgba8, per column (R = prop set + 1, G = density)
    public Image Ground;           // R8, per column (ground set + 1; 0 = default ground)
    public Image WaterType;        // R8, per column (waterTypes index + 1; 0 = the zone's)
    // Rgba8, per column: R = paving block + 1 (0 = none), G/B = the world Y it
    // is laid at + 1 (0 = seated on the column's own surface, so it follows
    // ground that later moves).
    public Image Paving;

    // Subscene stamps. A LIST, not a layer: a stamp is an identity, an
    // orientation and a footprint, none of which fit in a per-column byte.
    public WorldMapPlacements Placements;
    public Image Mobs;             // Rgba8, per column (R = mob set + 1, G = density)
    public Image Scalars;          // Rgba8, per column (R = mob level, G = climb)
    // Per-voxel edits to the heightfield: EditCarve removes a voxel the height
    // map would have made solid, EditAdd makes one solid the height map would
    // have left as air. Indexed [px, ly, pz], ly = wy - WorldMinY.
    public byte[,,] Tunnels;

    public const byte EditNone = 0;
    public const byte EditCarve = 1;
    public const byte EditAdd = 2;

    // Highest and lowest world Y carrying a voxel edit, per column (WorldMinY - 1
    // and int.MaxValue where the column has none). Built lazily and maintained by
    // SetVoxelEdit: without them every surface query would scan a column's whole
    // height to learn that nothing was ever carved into it, and the cutaway's
    // walk down through rock would run to bedrock before giving up.

    // World Y of the waterline. Read-only: the elevation layer is signed around
    // it, so shorelines are painted rather than made by sliding the sea.
    public int SeaLevel => Data.seaLevel;

    // "This column holds no water at all", in both encodings.
    //
    // The layer value sits BELOW the lowest surface an author can paint, so
    // nothing paintable collides with it; the world Y sits below the world
    // floor, so it is smaller than every column's ground and every question
    // asked of it — is there water over me, how deep, StandHeight's Max — comes
    // out right with no special case.
    //
    // It exists because there is no waterline any more. The sea used to be a
    // rule (max(SeaLevel, painted)), which made "dry ground below sea level"
    // inexpressible: the sea was wherever the ground was low, whatever the
    // author wanted. Now the water layer is the whole answer, an unpainted
    // column reads as water at seaLevel (a blank layer is zeros, and 0 encodes
    // seaLevel — that IS the prefill), and erasing a column is what digs a dry
    // basin under it.
    public float NoWaterVoxels => Data.minElevationVoxels - 1f;

    public int NoWater => Data.WorldMinY - 1;

    // Palette slots for one zone's kits, resolved once per bake. The per-voxel
    // TerrainId is an index into WorldGen's active kit palette, so this is the
    // translation from "which zone is this chunk" to "which slot does its ground
    // use".
    public WorldMapState(WorldMapData data)
    {
        Data = data;
        Field = new TerrainField(this);
        Edits = new VoxelEditOverlay(this);
        Elevation = data.LoadOrCreateElevation();
        Water = data.LoadOrCreateWater();
        Region = data.LoadOrCreateRegion();
        Zone = data.LoadOrCreateZone();
        Wind = data.LoadOrCreateWind();
        Scatter = data.LoadOrCreateScatter();
        Ground = data.LoadOrCreateGround();
        WaterType = data.LoadOrCreateWaterType();
        Paving = data.LoadOrCreatePaving();
        Placements = LoadOrCreatePlacements(data);
        Mobs = data.LoadOrCreateMobs();
        Scalars = data.LoadOrCreateScalars();
        Tunnels = data.LoadOrCreateTunnels();
    }

    // Where the cutaway starts a session: the top of the world, i.e. NOT CUT.
    // Every view that cuts is then exactly what it would be without one until the
    // plane is lowered, which is what lets the water tool share the mechanism
    // without its ordinary surface map opening full of rock.

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
        RegionData[] regions = Data.regions;
        if (regions == null || index < 0 || index >= regions.Length)
        {
            return $"Region {index}";
        }
        string authored = regions[index]?.displayName.ToString();
        if (!string.IsNullOrEmpty(authored))
        {
            return authored;
        }
        string file = FileStem(regions[index]?.ResourcePath);
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

    // The world's grade discriminator: adjacent columns within it mesh as a
    // slope, beyond it as a wall. Resolved once, since this is asked per column
    // by the map preview.
    private int _maxGradeStep = -1;
    public int MaxGradeStep => _maxGradeStep >= 0
        ? _maxGradeStep
        : _maxGradeStep = Mathf.Max(1, Data.finish?.maxGradeStep ?? 1);

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
        return (TerrainHeight(px, pz) - SeaLevel) / StepVoxels;
    }

    // Elevation as a 0..1 fraction of the document's range, for views that just
    // want "how high is this" (zone brightness, scatter backdrop).
    public float ElevationFraction(int px, int pz)
    {
        float v = TerrainHeight(px, pz) - SeaLevel;
        return Mathf.Clamp(Mathf.InverseLerp(Data.minElevationVoxels, Data.maxElevationVoxels, v), 0f, 1f);
    }

    // The painted column height, before weathering. Erosion is always measured
    // against THIS, never against an already-weathered neighbour, so the model
    // cannot feed on itself.
    public int RawHeight(int px, int pz)
    {
        return ColumnHeight(ElevationVoxels(px, pz));
    }

    // The level a player occupies at a column: the ground, or the water surface
    // where water stands over it.
    //
    // Weathering measures against THIS rather than the raw ground, because a
    // cliff at a shoreline is only as tall as the part standing out of the
    // water. Measured against the seabed, a 6m sea cliff over a -2m bed read as
    // an 8m cliff, drew the budget of one, and piled a talus shelf that surfaced
    // at +3 — turning an unclimbable sea cliff into a ledge reachable by
    // swimming. Painted lakes get the same protection as the sea, since both are
    // just a water surface here.
    public int StandHeight(int px, int pz)
    {
        return Mathf.Max(RawHeight(px, pz), WaterSurface(px, pz));
    }

    // Topmost solid voxel of the column, weathering included.
    //
    // Reads a cached field rather than recomputing: weathering spreads over a
    // neighbourhood, and this is called for every cell of every rebuild and
    // every column of the bake. The cache is also why roughness can be a LAYER
    // instead of an edit — the erosion is derived from the pristine elevation
    // every time it is rebuilt, so painting the same wall twice cannot crumble
    // it.
    public int TerrainHeight(int px, int pz)
    {
        return Field.Height(px, pz);
    }

    // Painted roughness at a column, 0..1. Shares the scalar image: R = mob
    // level, G = climb route, B = roughness.
    public float RoughnessAt(int px, int pz)
    {
        return Scalars.GetPixel(ClampX(px), ClampZ(pz)).B;
    }

    public void SetRoughnessAt(int px, int pz, float strength)
    {
        Color c = Scalars.GetPixel(px, pz);
        Scalars.SetPixel(px, pz, new Color(c.R, c.G, Mathf.Clamp(strength, 0f, 1f), 1f));
    }

    // The painter calls this after every stamp and after undo, with the region
    // that may have moved. Anything that rewrites a whole layer (a resize, a
    // reload) drops the cache instead.
    public void InvalidateHeights(Rect2I texelRect)
    {
        // The fill is global — a notch cut in a rim moves a shoreline on the far
        // side of the lake — so water cannot be updated over a rect and is
        // simply rebuilt.
        Field.Invalidate(texelRect);
    }

    public void InvalidateAllHeights()
    {
        Field.InvalidateAll();
    }

    internal static readonly int[] NeighbourDx = { 1, -1, 0, 0 };
    internal static readonly int[] NeighbourDz = { 0, 0, 1, -1 };

    // 0 = the erosion all comes off the top, 1 = it all piles at the base.
    // Surface of the water at a column, or NoWater where it has been erased.
    // Read from the cache filled beside the heights, since the outline pass asks
    // per edge and the bake per column.
    //
    // THE LAYER IS THE WHOLE ANSWER — there is no waterline folded in. The world
    // starts prefilled with water at seaLevel (an unpainted column's 0 encodes
    // exactly that), land hides the water it stands above, and carving that land
    // away reveals the water that was there the whole time. So a sea is not a
    // rule about low ground; it is the water nobody has erased.
    //
    // PAINTED, not filled. A flood fill was tried and removed: it answered a
    // question the author had already answered by clicking, and it could not
    // tell a lake from a river without being told which it was looking at. A
    // brush that fills a column to the level you have selected says exactly what
    // it does, and showing depth and spill on the map is what replaces the
    // fill's usefulness.
    public int WaterSurface(int px, int pz)
    {
        return Field.Water(px, pz);
    }

    // Water at column A stands above whatever B's top surface is, so it pours
    // over the edge between them. A waterfall — the one thing about painted
    // water depth shading cannot show, and the thing most likely to be an
    // accident.
    //
    // B's side is its VISIBLE surface, which is the whole subtlety: a fall does
    // not need bare rock to land on. A river dropping into a lower pool is the
    // commonest cascade there is, and testing "B is dry" instead — which this
    // did — silently excluded every one of them, leaving the map to draw the
    // lip as an ordinary height step. It read as a line DARKER than the water,
    // which is the tell: the teal is brighter than any water shade, so a dark
    // line at a lip means the edge was never classified as a spill.
    //
    // Two columns of one pool share a surface and are never a spill, so the
    // strict comparison is doing the work an explicit "different bodies" test
    // would otherwise need.
    //
    // The map's INK only. What the bake files as a cascade is measured off the
    // finished voxels instead (WaterfallFinder), which sees drops this cannot —
    // a tunnel breaching a pool, a stamped scene, a hand-edited voxel — so this
    // is the flat preview of a spill, not a promise of a waterfall.
    //
    // Ordered, because which side is the pool decides which way the water
    // leaves; a caller asking "is this edge a spill" asks both ways.
    public bool SpillsOver(int ax, int az, int bx, int bz)
    {
        return Underwater(ax, az) && VisibleSurface(bx, bz) < WaterSurface(ax, az);
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

    // Column holds water at all — standing over its ground, or LATENT beneath
    // it, waiting for the land above to be carved away. The map draws only the
    // standing half; this is what the hover readout reports, so painted water
    // you cannot see yet is still findable.
    public bool HasWater(int px, int pz)
    {
        return WaterSurface(px, pz) > NoWater;
    }

    public byte VoxelEdit(int px, int pz, int wy)
    {
        int ly = wy - Data.WorldMinY;
        if (px < 0 || px >= Data.ImageWidth || pz < 0 || pz >= Data.ImageHeight
            || ly < 0 || ly >= Data.VoxelHeight)
        {
            return EditNone;
        }
        return Tunnels[px, ly, pz];
    }

    public bool IsTunnel(int px, int pz, int wy) => VoxelEdit(px, pz, wy) == EditCarve;

    public bool IsAdded(int px, int pz, int wy) => VoxelEdit(px, pz, wy) == EditAdd;

    public void SetVoxelEdit(int px, int pz, int wy, byte edit)
    {
        int ly = wy - Data.WorldMinY;
        if (px < 0 || px >= Data.ImageWidth || pz < 0 || pz >= Data.ImageHeight
            || ly < 0 || ly >= Data.VoxelHeight)
        {
            return;
        }
        Tunnels[px, ly, pz] = edit;
        Edits.Note(px, pz, wy, edit);
    }

    // Anything that rewrites the edit layer wholesale — an undo restore, a
    // resize — drops the summary instead of maintaining it.
    public void InvalidateVoxelEdits()
    {
        Edits.InvalidateAll();
    }

    // Solid land at a given Y: the height field, minus what has been carved out
    // of it, plus what has been added above it.
    public bool SolidAt(int px, int pz, int wy)
    {
        byte edit = VoxelEdit(px, pz, wy);
        return edit == EditAdd || (edit != EditCarve && wy <= TerrainHeight(px, pz));
    }

    // Topmost solid voxel at or below `clipY` — what a map looking straight down
    // from that level sees. int.MaxValue asks for the world's own top.
    //
    // The edit layer is the only reason this is not just TerrainHeight, so the
    // common case (a column nobody has carved or built on) answers without
    // touching it at all.
    public int SurfaceBelow(int px, int pz, int clipY)
    {
        int th = TerrainHeight(px, pz);
        int topEdit = Edits.Top(ClampX(px), ClampZ(pz));
        if (topEdit < th && th <= clipY)
        {
            return th;
        }
        for (int wy = Mathf.Min(clipY, Mathf.Max(topEdit, th)); wy >= Data.WorldMinY; wy--)
        {
            if (SolidAt(px, pz, wy))
            {
                return wy;
            }
        }
        return Data.WorldMinY - 1;
    }

    // The floor a CUTAWAY view draws at a column: the highest solid voxel with
    // air above it, at or below `clipY`. Where the cut is open that is simply the
    // ground; where the cut passes through rock it is the floor of the highest
    // hollow beneath — so the map sees THROUGH the mountain to the tunnel under
    // it instead of stopping at the rock.
    //
    // `roofed` says which of those it found: true when there is rock between the
    // cut and the floor, which is what the view dims and what the erase refuses
    // to touch. Returns WorldMinY - 1 when the column is solid the whole way
    // down and there is no floor to draw at all.
    public int CutawayFloor(int px, int pz, int clipY, out bool roofed)
    {
        int x = ClampX(px);
        int z = ClampZ(pz);
        int th = TerrainHeight(px, pz);

        // An unedited column is solid to its terrain top and air above it, so
        // both answers are one comparison. This is nearly every column on the
        // map, and the walk below would otherwise run per texel per rebuild.
        if (Edits.Top(x, z) < Data.WorldMinY)
        {
            roofed = clipY < th;
            return roofed ? Data.WorldMinY - 1 : th;
        }

        // int.MaxValue means "the world's own top" (SurfaceBelow documents the
        // same convention and clamps for the same reason): the walk has to START
        // at a Y that can hold a voxel, or it counts down through two billion
        // that cannot before it reaches the ground. Every caller drawing a view
        // that does not cut away passes int.MaxValue, so an edited column under
        // the cursor hung the painter outright.
        int wy = Mathf.Min(clipY, Data.WorldMaxY);
        roofed = SolidAt(px, pz, wy);
        // Below the lowest edit the column is the plain height field, so once the
        // walk gets down there still in rock, nothing under it is hollow.
        int stop = Mathf.Max(Data.WorldMinY, Mathf.Min(Edits.Bottom(x, z), th));
        while (wy >= stop && SolidAt(px, pz, wy))
        {
            wy--;
        }
        if (SolidAt(px, pz, wy))
        {
            return Data.WorldMinY - 1;
        }
        while (wy >= Data.WorldMinY && !SolidAt(px, pz, wy))
        {
            wy--;
        }
        return wy;
    }

    // How far BELOW its free surface a body stands or floats in water. Mirrors
    // ClimbLedgeMarker.WaterStandDrop and WalkabilityGrid's own convention —
    // see StandSurface for why the outlines need it.
    public const int WaterStandDrop = 1;

    // The surface a BODY meets at a column: the top of the solid world, edits
    // included, cut away above a view's clip level, and — where the view draws
    // water — the level a body stands or floats at in it, which is
    // WaterStandDrop below the free surface. Nothing but the step outlines reads
    // this; the bake stamps from TerrainHeight and the edit layer directly.
    //
    // The drop is what makes the outline buckets mean what they look like. They
    // are a traversal legend (1m walk up, 2m mantle, 3m+ wall) and every one of
    // those numbers is measured off the surface a body is held at, so without it
    // a bank one voxel proud of a lake — a real mantle — inks as a walk-up.
    // Every column of one body moves together, so a lake is still one flat sheet
    // outlined only at its shore.
    public int StandSurface(int px, int pz, bool withWater, int clipY = int.MaxValue)
    {
        int h = SurfaceBelow(px, pz, clipY);
        if (!withWater)
        {
            return h;
        }
        int water = Mathf.Min(WaterSurface(px, pz), clipY);
        return water > h ? water - WaterStandDrop : h;
    }

    internal int ClampX(int px) => Mathf.Clamp(px, 0, Data.ImageWidth - 1);
    internal int ClampZ(int pz) => Mathf.Clamp(pz, 0, Data.ImageHeight - 1);

    // ---- Spawn sets -----------------------------------------------------

    public SpawnSetData[] PropSets => Data.propSets ?? System.Array.Empty<SpawnSetData>();

    public SpawnSetData[] MobSets => Data.mobSets ?? System.Array.Empty<SpawnSetData>();

    public int MobLevelCount => Mathf.Max(1, Data.mobLevelCount);

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
    public BlockData[] PavingBlocks => Data.pavingBlocks ?? System.Array.Empty<BlockData>();

    // Every layer image, in a fixed order, with the texel-to-pixel ratio each
    // is indexed at (1 per column, ChunkState.SIZE per chunk). Undo keys tiles
    // by position in this array, so APPEND to it rather than reordering.
    public RasterLayer[] RasterLayers()
    {
        return new[]
        {
            new RasterLayer(Elevation, 1),
            new RasterLayer(Water, 1),
            new RasterLayer(Ground, 1),
            new RasterLayer(Paving, 1),
            new RasterLayer(Scatter, 1),
            new RasterLayer(Mobs, 1),
            new RasterLayer(Scalars, 1),
            new RasterLayer(Region, ChunkState.SIZE),
            new RasterLayer(Zone, ChunkState.SIZE),
            new RasterLayer(Wind, ChunkState.SIZE),
            new RasterLayer(WaterType, 1),
        };
    }

    // Texel -> world XZ. Placements are authored in WORLD coordinates (worldgen
    // reads the same field on the same resource), so the tools convert at the
    // boundary rather than storing a second coordinate system.
    public Vector2I WorldXZ(Vector2I texel)
    {
        return new Vector2I(Data.WorldMinX + texel.X, Data.WorldMinZ + texel.Y);
    }

    public Vector2I TexelXZ(Vector2I worldXZ)
    {
        return new Vector2I(worldXZ.X - Data.WorldMinX, worldXZ.Y - Data.WorldMinZ);
    }

    // ---- Subscene stamps -------------------------------------------------

    private static WorldMapPlacements LoadOrCreatePlacements(WorldMapData data)
    {
        if (!string.IsNullOrEmpty(data.placementsPath) && ResourceLoader.Exists(data.placementsPath))
        {
            var loaded = ResourceLoader.Load<WorldMapPlacements>(data.placementsPath);
            if (loaded != null)
            {
                loaded.placements ??= System.Array.Empty<SubscenePlacement>();
                return loaded;
            }
        }
        return new WorldMapPlacements();
    }

    private void SavePlacements()
    {
        if (string.IsNullOrEmpty(Data.placementsPath))
        {
            return;
        }
        Placements.ResourcePath = Data.placementsPath;
        Error err = ResourceSaver.Save(Placements, Data.placementsPath);
        if (err != Error.Ok)
        {
            GD.PushError($"WorldMapState: could not save placements to {Data.placementsPath}: {err}");
        }
    }

    // Loaded-and-ROTATED subscenes, cached by (path, quarter turns). Rotation
    // happens before anything measures a scene — the footprint the map draws,
    // the ground sample and the stamp must all read the same Size — and every
    // one of those is asked per frame while dragging, so the turn is done once.
    private readonly System.Collections.Generic.Dictionary<(string, int), SubsceneState> _subscenes = new();

    public SubsceneState SubsceneFor(SubscenePlacement placement)
    {
        if (placement == null || string.IsNullOrEmpty(placement.path))
        {
            return null;
        }
        var key = (placement.path, (int)placement.rotation);
        if (_subscenes.TryGetValue(key, out SubsceneState cached))
        {
            return cached;
        }
        SubsceneState sub = null;
        try
        {
            sub = SubsceneRotator.Rotate(SubsceneFile.Read(placement.path), key.Item2);
        }
        catch (System.Exception e)
        {
            GD.PushError($"WorldMapState: subscene '{placement.path}' failed to load: {e.Message}");
        }
        _subscenes[key] = sub;
        return sub;
    }

    // Footprint in TEXEL space (the map's own coordinates), or a zero rect if
    // the scene will not load.
    public Rect2I FootprintOf(SubscenePlacement placement)
    {
        SubsceneState sub = SubsceneFor(placement);
        if (sub == null)
        {
            return new Rect2I();
        }
        int x = Mathf.FloorToInt(placement.anchorXZ.X - sub.Anchor.X) - Data.WorldMinX;
        int z = Mathf.FloorToInt(placement.anchorXZ.Y - sub.Anchor.Z) - Data.WorldMinZ;
        return new Rect2I(x, z, sub.Size.X, sub.Size.Z);
    }

    // The Y a stamp seats at: WorldGen's own rule (the most common ground level
    // across the footprint, ties to the lower) plus the placement's nudge. Used
    // by the bake AND by the tool's alt+click, so the number the author aims at
    // is the number the bake uses.
    public int SeatY(SubscenePlacement placement)
    {
        SubsceneState sub = SubsceneFor(placement);
        if (sub == null)
        {
            return SeaLevel;
        }
        var origin = new Vector3I(
            Mathf.FloorToInt(placement.anchorXZ.X - sub.Anchor.X), 0,
            Mathf.FloorToInt(placement.anchorXZ.Y - sub.Anchor.Z));
        int ground = TerrainMath.FootprintPlateauY(
            (x, z) => TerrainHeight(x - Data.WorldMinX, z - Data.WorldMinZ),
            Data.elevationStepVoxels, origin, sub.Size, out _);
        return ground + placement.yOffset;
    }

    // Top-down colour of a stamp's contents, one entry per footprint column,
    // alpha 0 where the scene has nothing. Built once per (scene, rotation,
    // slice) and cached — the map asks for this per texel per rebuild, and
    // scanning a building's full height every time would show.
    //
    // Turns a placed stamp from a featureless rectangle into a floor plan, which
    // is what makes a stamp placeable at all: which way the house faces and where
    // its walls are cannot be read off a wash.
    public Color[] SubscenePreview(SubscenePlacement placement)
    {
        return Plan(placement, int.MaxValue).Colors;
    }

    // Local Y of the voxel the plan drew at a footprint column, or -1 where the
    // scene authors nothing there. Kept beside the colour rather than derived
    // from it, because a cutaway needs to know how HIGH the plan is, and the
    // colour has already been shaded and cannot be read back as a height.
    public int SubsceneTopAt(SubscenePlacement placement, int px, int pz)
    {
        Rect2I footprint = FootprintOf(placement);
        if (footprint.Size.X <= 0)
        {
            return -1;
        }
        int lx = px - footprint.Position.X;
        int lz = pz - footprint.Position.Y;
        if (lx < 0 || lz < 0 || lx >= footprint.Size.X || lz >= footprint.Size.Y)
        {
            return -1;
        }
        int[] tops = Plan(placement, int.MaxValue).Tops;
        int i = lz * footprint.Size.X + lx;
        return i < tops.Length ? tops[i] : -1;
    }

    // World Y the stamp's local (0, 0, 0) lands on — SubsceneStamper's own
    // corner rule, so the map and the bake agree about how high a stamp sits.
    public int StampBaseY(SubscenePlacement placement)
    {
        SubsceneState sub = SubsceneFor(placement);
        return sub == null ? SeaLevel : Mathf.FloorToInt(SeatY(placement) - sub.Anchor.Y);
    }

    // How many metres tall the scene a stamp places is — the range a cutaway
    // plane can meaningfully walk through it.
    public int StampHeight(SubscenePlacement placement)
    {
        SubsceneState sub = SubsceneFor(placement);
        return sub == null ? 0 : sub.Size.Y;
    }

    // Resolved ONCE per display rebuild, parallel to a StampsIn result: the seat
    // walks the whole footprint, and asking per texel would put that scan inside
    // the map's hottest loop.
    public int[] StampBaseYs(SubscenePlacement[] stamps)
    {
        var seats = new int[stamps.Length];
        for (int i = 0; i < stamps.Length; i++)
        {
            seats[i] = StampBaseY(stamps[i]);
        }
        return seats;
    }

    // `localLevel` is the highest LOCAL y of the scene this plan may draw —
    // int.MaxValue for the whole thing. A cutaway passes the plane translated
    // into the scene's own coordinates, so lowering it walks down through a
    // building's floors instead of showing its roof or nothing at all.
    private (Color[] Colors, int[] Tops) Plan(SubscenePlacement placement, int localLevel)
    {
        SubsceneState sub = SubsceneFor(placement);
        if (sub == null)
        {
            return (System.Array.Empty<Color>(), System.Array.Empty<int>());
        }
        // Clamped to the scene's own top, so every plane at or above the roof
        // shares ONE cache entry with the unclipped plan — otherwise a plane
        // parked over the world would mint an entry per stamp seat.
        int from = Mathf.Min(localLevel, sub.Size.Y - 1);
        if (from < 0)
        {
            return (System.Array.Empty<Color>(), System.Array.Empty<int>());
        }
        var key = (placement.path, (int)placement.rotation, from);
        if (_subscenePreviews.TryGetValue(key, out (Color[] Colors, int[] Tops) cached))
        {
            return cached;
        }

        var colors = new Color[sub.Size.X * sub.Size.Z];
        var tops = new int[sub.Size.X * sub.Size.Z];
        System.Array.Fill(tops, -1);
        BlockCatalog catalog = BlockCatalog.Active;
        float span = Mathf.Max(1, sub.Size.Y - 1);
        for (int x = 0; x < sub.Size.X; x++)
        {
            for (int z = 0; z < sub.Size.Z; z++)
            {
                for (int y = from; y >= 0; y--)
                {
                    if (!sub.PresenceMask[x, y, z])
                    {
                        continue;
                    }
                    BlockData block = catalog?.GetById(sub.Voxels[x, y, z]);
                    if (block == null || !block.solid)
                    {
                        continue;
                    }
                    // Shaded by its own height within the scene, so walls read
                    // brighter than the floor they stand on and the plan has
                    // some relief instead of being flat colour.
                    float shade = Mathf.Lerp(0.70f, 1.15f, y / span);
                    Color c = block.minimapColor;
                    colors[z * sub.Size.X + x] = new Color(
                        Mathf.Clamp(c.R * shade, 0f, 1f),
                        Mathf.Clamp(c.G * shade, 0f, 1f),
                        Mathf.Clamp(c.B * shade, 0f, 1f), 1f);
                    tops[z * sub.Size.X + x] = y;
                    break;
                }
            }
        }
        _subscenePreviews[key] = (colors, tops);
        return (colors, tops);
    }

    // Keyed by the SLICE as well as the scene and its rotation: scrubbing the
    // cutaway through a building mints one entry per metre of that building,
    // which is bounded by its height.
    private readonly System.Collections.Generic.Dictionary<(string, int, int), (Color[] Colors, int[] Tops)>
        _subscenePreviews = new();

    // Topmost stamp covering a texel, or null. Last wins, matching the draw
    // order: what you see on top is what a click grabs.
    public SubscenePlacement PlacementAt(int px, int pz)
    {
        SubscenePlacement[] list = Placements.placements;
        for (int i = list.Length - 1; i >= 0; i--)
        {
            if (list[i] != null && FootprintOf(list[i]).HasPoint(new Vector2I(px, pz)))
            {
                return list[i];
            }
        }
        return null;
    }

    // Everything a display rebuild needs about the stamps it might draw,
    // resolved ONCE per rebuild. Per texel it is then a rect test and two array
    // reads.
    //
    // This exists because a full rebuild is ~295k texels and every one of them
    // was rebuilding a footprint and hashing a (path, rotation) STRING KEY to
    // find the cached plan — 277 ms of a 620 ms rebuild, and it scaled with how
    // many buildings the document holds. Nothing here changes per texel, so
    // nothing here belongs in that loop.
    public sealed class StampPlan
    {
        public SubscenePlacement[] Stamps = System.Array.Empty<SubscenePlacement>();
        public Rect2I[] Footprints = System.Array.Empty<Rect2I>();
        public Color[][] Colors = System.Array.Empty<Color[]>();
        public int[][] Tops = System.Array.Empty<int[]>();
        // World Y of each stamp's local (0,0,0). Null when the view is not
        // cutting, which is also how ClipY == int.MaxValue reads.
        public int[] BaseYs;
        public int ClipY = int.MaxValue;
    }

    // Stamps whose footprint meets a rect, in list order (so the LAST is the
    // topmost), with everything the per-texel composite needs alongside them.
    public StampPlan PlanStamps(Rect2I rect, int clipY = int.MaxValue)
    {
        SubscenePlacement[] list = Placements.placements;
        var hits = new List<SubscenePlacement>();
        var rects = new List<Rect2I>();
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i] == null)
            {
                continue;
            }
            Rect2I footprint = FootprintOf(list[i]);
            if (footprint.Size.X > 0 && footprint.Intersects(rect))
            {
                hits.Add(list[i]);
                rects.Add(footprint);
            }
        }
        var plan = new StampPlan
        {
            Stamps = hits.ToArray(),
            Footprints = rects.ToArray(),
            Colors = new Color[hits.Count][],
            Tops = new int[hits.Count][],
            ClipY = clipY,
            BaseYs = clipY == int.MaxValue ? null : new int[hits.Count],
        };
        for (int i = 0; i < plan.Stamps.Length; i++)
        {
            // The seat FIRST: it is what turns the world-space plane into the
            // scene's own coordinates, and the plan is sliced at that.
            int localLevel = int.MaxValue;
            if (plan.BaseYs != null)
            {
                plan.BaseYs[i] = StampBaseY(plan.Stamps[i]);
                localLevel = clipY - plan.BaseYs[i];
            }
            (Color[] colors, int[] tops) = Plan(plan.Stamps[i], localLevel);
            plan.Colors[i] = colors;
            plan.Tops[i] = tops;
        }
        return plan;
    }

    // Composite the topmost stamp covering a texel over the colour a view
    // returned. `selected` is whatever the active tool has picked, or null on
    // the tools that have no notion of one.
    //
    // The plan wins over the view's own colour where the scene authors
    // something, and washes it where the scene is empty — a courtyard, the gap
    // around a tower — or a stamp's extent would be invisible exactly where its
    // shape is most ambiguous.
    //
    // On a CUTAWAY view the stamp is SLICED at the plane, exactly as the terrain
    // around it is: the plan draws the topmost solid voxel of the scene at or
    // below the cut, so lowering the plane walks down through a building's
    // floors — walls at that level as content, the floor below them wherever the
    // room is open. Only once the plane drops below the stamp's BASE is it
    // hidden outright, which is when the cut has genuinely taken the building
    // away and its plan would paint over the passage you are boring under it. A
    // plane parked over everything renders exactly what no plane at all would.
    // Which stamp the plan resolves to at a texel, and what that stamp says
    // there: `index` into the plan, `top` the local Y it draws (-1 where the
    // scene is empty and only the footprint wash applies). False where no stamp
    // covers the texel at all.
    //
    // Split out from the inking below because it is the whole ANSWER — the
    // colour is one rendering of it. worldmap_check compares partial rebuilds
    // against a full one and wants the answer, not a picture of it: comparing
    // colours made a display concern a dependency of a headless check, and it
    // was also a weaker test, since two different stamps that happen to ink the
    // same colour compare equal.
    public bool StampHitAt(StampPlan plan, int px, int pz, out int index, out int top)
    {
        index = -1;
        top = -1;
        if (plan == null)
        {
            return false;
        }
        for (int i = plan.Stamps.Length - 1; i >= 0; i--)
        {
            Rect2I fp = plan.Footprints[i];
            int lx = px - fp.Position.X;
            int lz = pz - fp.Position.Y;
            if (lx < 0 || lz < 0 || lx >= fp.Size.X || lz >= fp.Size.Y)
            {
                continue;
            }
            if (plan.BaseYs != null && plan.BaseYs[i] > plan.ClipY)
            {
                continue;
            }
            int at = lz * fp.Size.X + lx;
            int[] tops = plan.Tops[i];
            index = i;
            top = at < tops.Length ? tops[at] : -1;
            return true;
        }
        return false;
    }

    public SpawnEntryData[] EntityPalette => Data.entityPalette ?? System.Array.Empty<SpawnEntryData>();

    // Topmost hand-placed entity within `radius` metres of a texel, or null.
    // Entities have no footprint to hit-test against — they are a point — so
    // selection is a proximity test, and the LAST match wins to match the draw
    // order the way the stamp hit-test does.
    public EntityPlacement EntityAt(int px, int pz, int radius)
    {
        Vector2I world = WorldXZ(new Vector2I(px, pz));
        EntityPlacement[] list = Placements.entities;
        for (int i = list.Length - 1; i >= 0; i--)
        {
            if (list[i] == null)
            {
                continue;
            }
            Vector2I d = list[i].anchorXZ - world;
            if (Mathf.Abs(d.X) <= radius && Mathf.Abs(d.Y) <= radius)
            {
                return list[i];
            }
        }
        return null;
    }

    // The spawn is a single point rather than a list, so it gets its own three
    // little queries instead of pretending to be an entity.
    public bool IsSpawnAt(int px, int pz)
    {
        return Placements.hasSpawn && Placements.spawnXZ == WorldXZ(new Vector2I(px, pz));
    }

    public bool IsSpawnNear(int px, int pz, int radius)
    {
        if (!Placements.hasSpawn)
        {
            return false;
        }
        Vector2I d = Placements.spawnXZ - WorldXZ(new Vector2I(px, pz));
        return Mathf.Abs(d.X) <= radius && Mathf.Abs(d.Y) <= radius;
    }

    public void SetSpawn(Vector2I worldXZ)
    {
        Placements.hasSpawn = true;
        Placements.spawnXZ = worldXZ;
    }

    // Which floor an entity dropped at a column should stand on: the one the map
    // is SHOWING there.
    //
    // Where that is the TOP of the column it records OnTheGround and is re-seated
    // from the column at every bake, so it follows ground that moves — including
    // ground carved or built after it was placed, which is why the test is
    // against the top solid voxel rather than against the height field. Only a
    // floor with something above it — a passage, the underside of a deck — needs
    // the absolute Y, because nothing about the column describes where that is
    // and re-seating would put the entity on the roof.
    public int FloorForEntity(int px, int pz, int clipY)
    {
        int floor = CutawayFloor(px, pz, clipY, out _);
        return floor < Data.WorldMinY || floor == SurfaceBelow(px, pz, int.MaxValue)
            ? EntityPlacement.OnTheGround
            : floor;
    }

    public void AddEntity(EntityPlacement placement)
    {
        var list = new System.Collections.Generic.List<EntityPlacement>(Placements.entities) { placement };
        Placements.entities = list.ToArray();
    }

    public void RemoveEntity(EntityPlacement placement)
    {
        var list = new System.Collections.Generic.List<EntityPlacement>(Placements.entities);
        list.Remove(placement);
        Placements.entities = list.ToArray();
    }

    public void AddPlacement(SubscenePlacement placement)
    {
        var list = new System.Collections.Generic.List<SubscenePlacement>(Placements.placements) { placement };
        Placements.placements = list.ToArray();
    }

    public void RemovePlacement(SubscenePlacement placement)
    {
        var list = new System.Collections.Generic.List<SubscenePlacement>(Placements.placements);
        list.Remove(placement);
        Placements.placements = list.ToArray();
    }

    // "Lie on whatever surface is under me" — the paving twin of
    // EntityPlacement.OnTheGround, and the level every road laid on open ground
    // keeps. A layer written before levels existed reads as this everywhere,
    // since its G/B are zero.
    public const int PavedOnSurface = int.MinValue;

    // Painted paving index, or -1 where the column keeps its kit's own block.
    public int PavingIndexAt(int px, int pz)
    {
        return PavingIndexOf(Paving.GetPixel(ClampX(px), ClampZ(pz)));
    }

    public BlockData PavingAt(int px, int pz)
    {
        int idx = PavingIndexAt(px, pz);
        return idx >= 0 ? PavingBlocks[idx] : null;
    }

    // Which FLOOR a column's paving lies on: an absolute world Y, or
    // PavedOnSurface where it rides the top of the column.
    public int PavingLevelAt(int px, int pz)
    {
        return PavingLevelOf(Paving.GetPixel(ClampX(px), ClampZ(pz)));
    }

    // The paving at a given floor, or null. There is ONE paving per column — the
    // layer is per column, as water and climb routes are — so this only ever
    // asks whether the column's paving was laid HERE. That is also the whole
    // limit of the model: a road through a passage and a road on the hill above
    // it cannot share a column, exactly as the erase that drains a passage
    // drains the lake over it.
    public BlockData PavingAtFloor(int px, int pz, int floorY)
    {
        Color cell = Paving.GetPixel(ClampX(px), ClampZ(pz));
        int idx = PavingIndexOf(cell);
        if (idx < 0)
        {
            return null;
        }
        int level = PavingLevelOf(cell);
        return (level == PavedOnSurface ? SurfaceBelow(px, pz, int.MaxValue) : level) == floorY
            ? PavingBlocks[idx]
            : null;
    }

    // The world Y a column's paving lies at, or WorldMinY - 1 where it has
    // none. A surface-seated road resolves against the top SOLID voxel, so it
    // rides a deck built over it and drops into a hole carved under it.
    public int PavedYAt(int px, int pz)
    {
        Color cell = Paving.GetPixel(ClampX(px), ClampZ(pz));
        if (PavingIndexOf(cell) < 0)
        {
            return Data.WorldMinY - 1;
        }
        int level = PavingLevelOf(cell);
        return level == PavedOnSurface ? SurfaceBelow(px, pz, int.MaxValue) : level;
    }

    // Paving lying on the column's OWN surface, or null. What every map drawn
    // from above shows, and what keeps the scatter and the detail sprites off a
    // road: paving on a floor under the surface — a passage, the ground beneath
    // an arch — belongs to that floor and says nothing about the hillside over
    // it.
    public BlockData SurfacePavingAt(int px, int pz)
    {
        return PavingAt(px, pz) != null
            ? PavingAtFloor(px, pz, SurfaceBelow(px, pz, int.MaxValue))
            : null;
    }

    // The floor a paving stroke at a column should be laid on: the one the map
    // is SHOWING there. False where the cut exposes no floor at all — solid rock
    // has nothing to pave, and the map draws it as such.
    //
    // Where that floor is the TOP of the column the level is PavedOnSurface and
    // the bake re-seats it on every bake, so a road follows ground that later
    // moves — including ground carved or built after it was laid, which is why
    // the test is against the top solid voxel and not against the height field.
    // Only a floor with something above it needs the absolute Y, because nothing
    // about the column describes where that is and re-seating would put the road
    // on the roof. The same split EntityPlacement.floorY makes, for the same
    // reason.
    public bool TryPavingLevel(int px, int pz, int clipY, out int level)
    {
        int floor = CutawayFloor(px, pz, clipY, out _);
        level = PavedOnSurface;
        if (floor < Data.WorldMinY)
        {
            return false;
        }
        if (floor != SurfaceBelow(px, pz, int.MaxValue))
        {
            level = floor;
        }
        return true;
    }

    public void SetPavingAt(int px, int pz, int index, int level = PavedOnSurface)
    {
        // An erase clears the level with the block: a column holding a level and
        // no paving is a value nothing can see and nothing can clear.
        int ly = index < 0 || level == PavedOnSurface
            ? 0
            : Mathf.Clamp(level - Data.WorldMinY + 1, 0, 0xFFFF);
        Paving.SetPixel(px, pz, new Color(
            Mathf.Clamp(index + 1, 0, 255) / 255f,
            (ly & 0xFF) / 255f,
            (ly >> 8) / 255f,
            1f));
    }

    private int PavingIndexOf(Color cell)
    {
        int idx = Mathf.RoundToInt(cell.R * 255f) - 1;
        return idx >= 0 && idx < PavingBlocks.Length ? idx : -1;
    }

    // Two channels, because a document may span more than 255 voxels of height
    // and one that does would otherwise wrap a road onto a floor nobody paved.
    private int PavingLevelOf(Color cell)
    {
        int ly = Mathf.RoundToInt(cell.G * 255f) | (Mathf.RoundToInt(cell.B * 255f) << 8);
        return ly <= 0 ? PavedOnSurface : Data.WorldMinY + ly - 1;
    }

    // Painted ground index, or -1 where the column inherits its zone's kits.
    // Which authored water type this column was painted with, or -1 for none —
    // in which case the column keeps whatever its ZONE authors, which is what
    // every document did before the layer existed.
    public int WaterTypeIndexAt(int px, int pz)
    {
        return Mathf.RoundToInt(WaterType.GetPixel(ClampX(px), ClampZ(pz)).R * 255f) - 1;
    }

    // The painted water block for a column, or -1. Resolved against the
    // document's palette; an entry that is not a water block is refused rather
    // than stamped, since it would turn a lake into rock.
    public int PaintedWaterBlockAt(int px, int pz)
    {
        int idx = WaterTypeIndexAt(px, pz);
        BlockData[] types = Data.waterTypes;
        if (idx < 0 || types == null || idx >= types.Length)
        {
            return -1;
        }
        BlockData b = types[idx];
        return b != null && b.render == EBlockRender.Water ? b.blockId : -1;
    }

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
    // The comparison is the inverted-unit form of SpawnListRow.RollAreaChance
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
        SpawnListRow[] rows = set.RowsFlat;
        if (rows != null)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] != null
                    && AreaRoll(Hash(px, pz, ENTITY_SALT + (uint)i), rows[i].squareMetersPerSpawn, density))
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

    // Ground anything may be placed on — the painter's half of worldgen's
    // IsGrassyAt, column for column:
    //
    //   dry            — with no waterline left, a basin dug below seaLevel and
    //                    erased of its water is ordinary ground and scatters
    //                    like any other.
    //   not a grade    — worldgen's IsFlatDryGrassAt tests Height == Plateau,
    //                    and a ramp column sits below its cell's top, so a
    //                    generated hillside grows nothing. Without this the
    //                    painter planted trees down every slope worldgen skips.
    //   not breached   — worldgen also insists the surface voxel is solid with
    //                    air above it, because a cave can carve through the
    //                    ground and leave the props floating over the hole. The
    //                    painted document's version of that hole is a tunnel
    //                    carved at the column's own surface height.
    //   not paved, not built — the road pass deletes the scatter standing in
    //                    its tread, and a placement reserves its footprint
    //                    (MarkNoSpawn) before anything scatters. A tree growing
    //                    out of a road, or inside a house, is the same mistake
    //                    twice.
    //
    // Ordered by what a test costs: two array reads, then the edit layer, then
    // the layer image and the placement list.
    //
    // The edit test is "the ground here is still the painted ground" — it fails
    // both where the top voxel was carved away (a hole, and the scatter would
    // hang over it) and where something was built above it (a bridge deck, and
    // the scatter would grow underneath it).
    public bool CanSpawnAt(int px, int pz)
    {
        return !Underwater(px, pz)
            && !IsGradeAt(px, pz)
            && SurfaceBelow(px, pz, int.MaxValue) == TerrainHeight(px, pz)
            && SurfacePavingAt(px, pz) == null
            && PlacementAt(px, pz) == null;
    }

    // Is this column part of a graded SLOPE — what StampGradeShapes will mesh as
    // a plane rather than as a terrace?
    //
    // The RULE is worldgen's own HeightMap.AxisIsGrade, fed the painted heights.
    // A second copy of "what counts as a slope" would drift from the pass that
    // actually meshes them, and the map would stop agreeing with the bake about
    // which ground is walkable.
    //
    // Deliberately NOT the 8-neighbour equality IsFlatAt uses: that is the
    // stricter RequireFlatTerrain test, and applying it here would strip the
    // scatter off every terrace edge in the world. Worldgen plants right up to a
    // cliff top, because a crisp wall is not a slope.
    public bool IsGradeAt(int px, int pz)
    {
        int h = TerrainHeight(px, pz);
        int step = MaxGradeStep;
        return HeightMap.AxisIsGrade(h, TerrainHeight(px - 1, pz), TerrainHeight(px + 1, pz), step)
            || HeightMap.AxisIsGrade(h, TerrainHeight(px, pz - 1), TerrainHeight(px, pz + 1), step);
    }

    // Independent salts: the two slots must roll independently, or every tree
    // would stand in a tuft of grass and every gap would be bare.
    // Practical maximum of the Perlin fields the sets use, measured across the
    // whole map on every authored set (0.67..0.75).
    internal const uint TREE_SALT = 0x9E37u;
    internal const uint GRASS_SALT = 0x2545u;
    internal const uint CHUNK_SALT = 0x7F4Au;
    internal const uint ENTITY_SALT = 0x85EBu;

    // Public because worldmap_check asks it of every hand-placed entity: this is
    // the one TrySpawn gate answerable without a built world, and a rejection by
    // it is silent.
    public bool IsFlatAt(int px, int pz)
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

    internal static uint Hash(int x, int z, uint salt = 0u)
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

    internal static float ToFloat01(uint h) => (h & 0xFFFFFFu) / 16777216f;

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
        Data.SaveWind(Wind);
        Data.SaveScatter(Scatter);
        Data.SaveGround(Ground);
        Data.SaveWaterType(WaterType);
        Data.SavePaving(Paving);
        SavePlacements();
        Data.SaveMobs(Mobs);
        Data.SaveScalars(Scalars);
        Data.SaveTunnels(Tunnels);
        GD.Print("WorldMapState: saved layers");
    }

    // Materialize the painted document into a WorldState and write the .hike.
    // Returns false if it could not (no output path, or the bake threw).
    //
    // THREE steps, because the middle one cannot run where the other two want to.
    // The sun flood is baked into the file now (nothing relights a world on load
    // ---- Painted wind ---------------------------------------------------
    //
    // The layer is per chunk, R = compass angle over a full turn, G = strength
    // with 0 reserved for UNPAINTED. Angle is stored rather than a vector pair
    // because it is what the tool edits and what the view draws; the bake is the
    // only place it becomes a velocity.

    // Painted wind for the chunk containing column texel (px, pz). False when
    // the author has not painted here — the caller falls back to the chunk's
    // zone, which is what every wind was before this layer existed.
    public bool WindAtColumn(int px, int pz, out float angleRadians, out float strength01)
    {
        Vector2I ct = Data.ColumnTexelToChunkTexel(px, pz);
        return WindAtChunkTexel(ct.X, ct.Y, out angleRadians, out strength01);
    }

    public bool WindAtChunkTexel(int lcx, int lcz, out float angleRadians, out float strength01)
    {
        angleRadians = 0f;
        strength01 = 0f;
        if (Wind == null || lcx < 0 || lcx >= Wind.GetWidth() || lcz < 0 || lcz >= Wind.GetHeight())
        {
            return false;
        }
        Color c = Wind.GetPixel(lcx, lcz);
        int strengthByte = Mathf.RoundToInt(c.G * 255f);
        if (strengthByte <= 0)
        {
            return false;
        }
        angleRadians = Mathf.RoundToInt(c.R * 255f) / 256f * Mathf.Tau;
        strength01 = (strengthByte - 1) / 254f;
        return true;
    }

    // Painted wind for a world CHUNK coordinate, resolved to a velocity. False
    // leaves the outputs untouched and means "this chunk inherits its zone".
    public bool WindForChunk(int cx, int cz, out Vector3 direction, out float speed)
    {
        direction = Vector3.Zero;
        speed = 0f;
        int lcx = cx - Data.MinChunk.X;
        int lcz = cz - Data.MinChunk.Z;
        if (!WindAtChunkTexel(lcx, lcz, out float angle, out float strength01))
        {
            return false;
        }
        direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        speed = strength01 * Mathf.Max(0f, Data.windPaintMaxSpeed);
        return true;
    }

    public void SetWindAtColumn(int px, int pz, float angleRadians, float strength01)
    {
        Vector2I ct = Data.ColumnTexelToChunkTexel(px, pz);
        if (Wind == null || ct.X < 0 || ct.X >= Wind.GetWidth() || ct.Y < 0 || ct.Y >= Wind.GetHeight())
        {
            return;
        }
        int angleByte = Mathf.PosMod(Mathf.RoundToInt(angleRadians / Mathf.Tau * 256f), 256);
        int strengthByte = 1 + Mathf.RoundToInt(Mathf.Clamp(strength01, 0f, 1f) * 254f);
        Wind.SetPixel(ct.X, ct.Y, new Color(angleByte / 255f, strengthByte / 255f, 0f, 1f));
    }

    public void ClearWindAtColumn(int px, int pz)
    {
        Vector2I ct = Data.ColumnTexelToChunkTexel(px, pz);
        if (Wind == null || ct.X < 0 || ct.X >= Wind.GetWidth() || ct.Y < 0 || ct.Y >= Wind.GetHeight())
        {
            return;
        }
        Wind.SetPixel(ct.X, ct.Y, new Color(0f, 0f, 0f, 1f));
    }

    // Seed every chunk's wind velocity subgrid. Painted chunks take the layer's
    // direction and strength; the rest fall back to their zone's prevailing
    // direction, which is what WorldGen does for every chunk it bakes.
    //
    internal static byte ClampIndex(byte idx, int count)
    {
        if (count <= 0)
        {
            return 0;
        }
        return idx >= count ? (byte)(count - 1) : idx;
    }

}
