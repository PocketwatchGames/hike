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
    // The two prop layers, per column: R = prop list index + 1 (0 = none).
    // No density channel — placement is direct and one prop per column, so a
    // painted column is a furnished column and nothing thins it but CanSpawnAt.
    public Image CollidableProps;
    public Image DestructibleProps;
    public Image Ground;           // R8, per column (ground set + 1; 0 = default ground)
    public Image WaterType;        // R8, per column (waterTypes index + 1; 0 = the zone's)
    // Rgba8, per column: R = paving block + 1 (0 = none), G/B = the world Y it
    // is laid at + 1 (0 = seated on the column's own surface, so it follows
    // ground that later moves).
    public Image Paving;

    // Subscene stamps. A LIST, not a layer: a stamp is an identity, an
    // orientation and a footprint, none of which fit in a per-column byte.
    public WorldMapPlacements Placements;

    // What this document can paint, resolved once. Every palette is discovered
    // from disk (WorldMapPaletteSource.Table) and the slot each resource
    // occupies is fixed by the ledger — which is why these are resolved at
    // construction and then never re-read: an index in a raster must mean the
    // same thing for the whole session, and the bake runs on a snapshot.
    public WorldMapPalettes Palettes;
    public ZoneData[] Zones;
    public RegionData[] Regions;
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
        CollidableProps = data.LoadOrCreateCollidableProps();
        DestructibleProps = data.LoadOrCreateDestructibleProps();
        Ground = data.LoadOrCreateGround();
        WaterType = data.LoadOrCreateWaterType();
        Paving = data.LoadOrCreatePaving();
        Placements = LoadOrCreatePlacements(data);
        Palettes = LoadOrCreatePalettes(data);
        Zones = WorldMapPaletteSource.Resolve<ZoneData>(WorldMapPaletteSource.Zones, Palettes);
        Regions = WorldMapPaletteSource.Resolve<RegionData>(WorldMapPaletteSource.Regions, Palettes);
        GroundSets = WorldMapPaletteSource.Resolve<GroundSetData>(WorldMapPaletteSource.GroundSets, Palettes);
        PropLists = WorldMapPaletteSource.Resolve<PropListData>(WorldMapPaletteSource.PropLists, Palettes);
        MobSets = WorldMapPaletteSource.Resolve<SpawnSetData>(WorldMapPaletteSource.MobSets, Palettes);
        WaterTypes = WorldMapPaletteSource.Resolve<BlockData>(WorldMapPaletteSource.WaterTypes, Palettes);
        PavingBlocks = WorldMapPaletteSource.Resolve<BlockData>(WorldMapPaletteSource.PavingBlocks, Palettes);
        EntityPalette = WorldMapPaletteSource.Resolve<SpawnEntryData>(WorldMapPaletteSource.Entities, Palettes);
        Presets = WorldMapPaletteSource.Resolve<PaintPresetData>(WorldMapPaletteSource.Presets, Palettes);
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
        ZoneData[] zones = Zones;
        if (index < 0 || index >= zones.Length)
        {
            return $"Zone {index}";
        }
        string file = FileStem(zones[index]?.ResourcePath);
        return string.IsNullOrEmpty(file) ? $"Zone {index}" : file;
    }

    public string RegionName(int index)
    {
        RegionData[] regions = Regions;
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

    public int RegionCount => Regions.Length;
    public int ZoneCount => Zones.Length;

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
        InvalidatePropFill();
        // The fill is global — a notch cut in a rim moves a shoreline on the far
        // side of the lake — so water cannot be updated over a rect and is
        // simply rebuilt.
        Field.Invalidate(texelRect);
    }

    public void InvalidateAllHeights()
    {
        InvalidatePropFill();
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
        InvalidatePropFill();
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

    // Both prop layers index this one palette; which layer a list was painted
    // on is what it means, not which list it is.
    public readonly PropListData[] PropLists;

    public readonly SpawnSetData[] MobSets;

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

    public readonly GroundSetData[] GroundSets;

    public readonly PaintPresetData[] Presets;

    // Ground unpainted anywhere: deliberately a flat neutral rather than a guess
    // at the zone's kits, so it is obvious at a glance which ground you have
    // actually authored and which is still inherited.
    public readonly BlockData[] PavingBlocks;

    // Water blocks a column may be painted with. The RASTER's slot 0 still means
    // "whatever the zone says"; these are the explicit overrides above it.
    public readonly BlockData[] WaterTypes;

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
            new RasterLayer(CollidableProps, 1),
            new RasterLayer(DestructibleProps, 1),
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

    private static WorldMapPalettes LoadOrCreatePalettes(WorldMapData data)
    {
        if (!string.IsNullOrEmpty(data.palettesPath) && ResourceLoader.Exists(data.palettesPath))
        {
            var loaded = ResourceLoader.Load<WorldMapPalettes>(data.palettesPath);
            if (loaded != null)
            {
                // Duplicated because a resolve APPENDS newly discovered slots,
                // and Godot hands every loader the one cached instance — a bake
                // snapshot would otherwise be writing into the live document's
                // ledger while the painter is using it.
                return (WorldMapPalettes)loaded.Duplicate(true);
            }
        }
        return new WorldMapPalettes();
    }

    private void SavePalettes()
    {
        if (string.IsNullOrEmpty(Data.palettesPath))
        {
            return;
        }
        Palettes.ResourcePath = Data.palettesPath;
        Error err = ResourceSaver.Save(Palettes, Data.palettesPath);
        if (err != Error.Ok)
        {
            GD.PushError($"WorldMapState: could not save palettes to {Data.palettesPath}: {err}");
        }
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

    public readonly SpawnEntryData[] EntityPalette;

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
        BlockData[] types = WaterTypes;
        if (idx < 0 || idx >= types.Length)
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

    // The painted mob set at a column, or null. The raster stores index+1 so 0
    // can mean "nothing painted here".
    public SpawnSetData MobSetAt(int px, int pz, out float density)
    {
        Color cell = Mobs.GetPixel(ClampX(px), ClampZ(pz));
        int idx = Mathf.RoundToInt(cell.R * 255f) - 1;
        density = cell.G;
        return idx >= 0 && idx < MobSets.Length && density > 0f ? MobSets[idx] : null;
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

    // --- Props: a size-ordered fill over the measured collision ---------------
    //
    // A painted region is FURNISHED largest-first, then its EDGE BAND is sealed.
    // Those are two different contracts and only the second is coverage: the
    // reason to paint props is to say where the player cannot walk, and a
    // barrier with a lane through it is not a barrier — but that is a claim
    // about the rim, which is the only part anyone can reach. (Worldgen's own
    // noise scatter is untouched; that is scenery grown by rule and lives on
    // SpawnSetData.)
    //
    // Each pass is one size class, spaced against ITS OWN class by the props'
    // DRAWN radius, so trees get canopy room while a bush may still stand at a
    // trunk's foot. The seal pass then covers the band with the widest COLLISION
    // that fits — a different order, because a tree is the largest thing in a
    // forest list and seals one column where a bush half its size seals seven.
    // Taking the smallest that fits instead is what ringed every region in
    // pebbles. See docs/prop-fill.md.
    //
    // The fill is per CHUNK and seeded by the chunk, which is what keeps it
    // replayable: a map preview cannot re-run a whole-world pass to find what
    // stands under the cursor, but it can re-run one chunk. Each chunk covers
    // its OWN columns, so the union covers everything and the only cost at a
    // seam is a prop from one chunk overlapping a prop from the next.
    //
    // WHERE a prop stands is decided without reference to any order — see
    // FillWork.Wins. A greedy walk restarts its spacing at every chunk boundary,
    // and with a 3.7 m canopy in a 16 m chunk that is most of the chunk.
    // Nothing anywhere uses a running Random: it could not be replayed from a
    // column, and the map preview has to reach the same answer the bake does.

    public PropListData PaintedCollidableAt(int px, int pz) => PaintedPropList(CollidableProps, px, pz);

    public PropListData PaintedDestructibleAt(int px, int pz) => PaintedPropList(DestructibleProps, px, pz);

    private PropListData PaintedPropList(Image layer, int px, int pz)
    {
        int idx = PaintedPropIndex(layer, px, pz);
        return idx >= 0 ? PropLists[idx] : null;
    }

    // Out of bounds is UNPAINTED, not the border column repeated: ClampX/ClampZ
    // would let a footprint fit by reaching off the map onto a copy of the edge.
    private int PaintedPropIndex(Image layer, int px, int pz)
    {
        if (px < 0 || px >= Data.ImageWidth || pz < 0 || pz >= Data.ImageHeight)
        {
            return -1;
        }
        int idx = Mathf.RoundToInt(layer.GetPixel(px, pz).R * 255f) - 1;
        return idx >= 0 && idx < PropLists.Length && PropLists[idx] != null ? idx : -1;
    }

    // One prop the fill placed: what it is, how it is turned, where in its
    // column it stands and how big it grew. The pose is carried rather than
    // implied because the footprint was measured AT it — move a placed prop and
    // the columns the map calls blocked stop being the ones that are.
    public readonly struct PaintedProp
    {
        public readonly int List;
        public readonly int Scene;
        // Radians, free rather than stepped.
        public readonly float Yaw;
        // Metres off the column's centre, both axes in -0.5..0.5.
        public readonly Vector2 Offset;
        // Uniform, always >= 1: see PropListData.scaleJitter for why it only
        // ever grows.
        public readonly float Scale;

        public PaintedProp(int list, int scene, float yaw, Vector2 offset, float scale)
        {
            List = list;
            Scene = scene;
            Yaw = yaw;
            Offset = offset;
            Scale = scale;
        }
    }

    // Does a prop STAND in this column (is it a fill origin), and which?
    public bool CollidablePropAt(int px, int pz, out PaintedProp prop)
        => PropOriginAt(false, px, pz, out prop);

    public bool DestructiblePropAt(int px, int pz, out PaintedProp prop)
        => PropOriginAt(true, px, pz, out prop);

    // Is this column INSIDE some prop's collision — i.e. blocked? The map draws
    // this, because it is the answer painting props is for.
    public bool CollidableCoversAt(int px, int pz) => PropCoverAt(false, px, pz);

    public bool DestructibleCoversAt(int px, int pz) => PropCoverAt(true, px, pz);

    private bool PropOriginAt(bool destructible, int px, int pz, out PaintedProp prop)
    {
        PropFill fill = FillFor(destructible, FloorDiv(px, ChunkState.SIZE), FloorDiv(pz, ChunkState.SIZE));
        return fill.Origins.TryGetValue(LocalCell(px, pz), out prop);
    }

    private bool PropCoverAt(bool destructible, int px, int pz)
    {
        PropFill fill = FillFor(destructible, FloorDiv(px, ChunkState.SIZE), FloorDiv(pz, ChunkState.SIZE));
        return fill.Own[LocalCell(px, pz)];
    }

    private static int LocalCell(int px, int pz)
        => Mod(px, ChunkState.SIZE) * ChunkState.SIZE + Mod(pz, ChunkState.SIZE);

    private sealed class PropFill
    {
        public readonly System.Collections.Generic.Dictionary<int, PaintedProp> Origins = new();

        // Columns nothing in the list could stand in without reaching outside
        // the painted region. Recorded rather than inferred, because from
        // outside the fill they are indistinguishable from a hole in a barrier —
        // and one is the author's to fix by painting wider or adding a smaller
        // prop, while the other is a bug.
        public readonly bool[] NoFit = new bool[ChunkState.SIZE * ChunkState.SIZE];

        // What the fill may not place into: this layer's own props plus, for the
        // breakable layer, everything the blocking one already covers.
        public readonly bool[] Covered = new bool[ChunkState.SIZE * ChunkState.SIZE];

        // What THIS layer's props cover, which is what the map draws and what
        // the check counts. Kept apart from Covered so a breakable layer under a
        // wood does not report the wood's coverage as its own.
        public readonly bool[] Own = new bool[ChunkState.SIZE * ChunkState.SIZE];
    }

    private readonly System.Collections.Generic.Dictionary<(bool, int, int), PropFill> _propFills = new();

    // Locked because the bake runs on a worker thread while the painter keeps
    // drawing on the main one, and both resolve props through here.
    private readonly object _propFillLock = new();

    private PropFill FillFor(bool destructible, int cx, int cz)
    {
        var key = (destructible, cx, cz);
        lock (_propFillLock)
        {
            if (_propFills.TryGetValue(key, out PropFill cached))
            {
                return cached;
            }
            PropFill fill = BuildFill(destructible, cx, cz);
            _propFills[key] = fill;
            return fill;
        }
    }

    // Called from FillFor under the lock, and re-enters it for the layer
    // underneath. Safe: a C# lock is re-entrant and the recursion is one deep,
    // since the blocking layer never asks for the breakable one.
    //
    // The fill is SIZE-ORDERED, largest first, and each pass spaces its props
    // against the ones IT placed rather than against everything. That ordering
    // is the whole shape of the result:
    //
    //   - the big pass runs first and everywhere, so the trees are laid down
    //     before anything else has taken the ground, and they are spaced by
    //     their CANOPIES rather than by their trunks — which is what "room to
    //     breathe" is, and what a trunk-sized exclusion cannot express;
    //   - each later pass spaces only against its own class, so a bush may
    //     stand at a tree's foot. The skirt of undergrowth around a trunk falls
    //     out of the ordering instead of being authored into the tree;
    //   - what stops any two props sharing ground is COVERED, which is
    //     collision and not canopy. Canopies are meant to overlap.
    //
    // A last pass then seals the edge band, ignoring spacing entirely, because
    // that band is the one place the contract is coverage.
    private PropFill BuildFill(bool destructible, int cx, int cz)
    {
        var fill = new PropFill();
        Image layer = destructible ? DestructibleProps : CollidableProps;
        uint salt = destructible ? DESTRUCTIBLE_SALT : COLLIDABLE_SALT;
        int size = ChunkState.SIZE;
        int baseX = cx * size;
        int baseZ = cz * size;

        // Most chunks are painted with nothing at all, and everything below
        // this costs a depth field over the chunk and its surroundings.
        if (!AnyPropPainted(layer, baseX, baseZ))
        {
            return fill;
        }

        // The breakable layer starts from what the blocking layer already
        // covers: a bramble inside a tree trunk is the same mistake as two trees
        // in one column, and blocking wins because it is the one the player
        // cannot clear.
        if (destructible)
        {
            System.Array.Copy(FillFor(false, cx, cz).Covered, fill.Covered, fill.Covered.Length);
        }

        var work = new FillWork(this, layer, salt, baseX, baseZ);
        for (int pass = 0; pass < work.Classes.Count; pass++)
        {
            // Several rounds of the same class, each refused by what the last
            // one reserved. One round is a maximal independent set, which
            // settles at roughly two thirds of the props the same minimum
            // separation would allow; the later rounds are dart throws into the
            // gaps it left. Order-free like the first, and nothing moves - which
            // is what a relaxation would have needed, and it would have had to
            // be stored rather than derived.
            for (int round = 0; round < work.SpacingRounds(pass); round++)
            {
                work.RunSpacingPass(fill, pass, round);
            }
        }
        work.RunSealPass(fill);
        return fill;
    }

    private bool AnyPropPainted(Image layer, int baseX, int baseZ)
    {
        int size = ChunkState.SIZE;
        for (int lx = 0; lx < size; lx++)
        {
            for (int lz = 0; lz < size; lz++)
            {
                if (PaintedPropIndex(layer, baseX + lx, baseZ + lz) >= 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // One chunk's fill in progress: the depth field it reads, the size classes
    // it walks, and the per-class spacing masks. Separate from PropFill because
    // none of it survives the build — what a fill IS afterwards is the props and
    // the coverage.
    private sealed class FillWork
    {
        private const int Size = ChunkState.SIZE;

        private readonly WorldMapState _map;
        private readonly Image _layer;
        private readonly uint _salt;
        private readonly int _baseX;
        private readonly int _baseZ;

        // Metres from the nearest unpainted column, over the chunk AND a halo
        // around it, so a column near the chunk edge knows how deep inside the
        // REGION it is rather than how deep inside the chunk — and so the
        // spacing test can look at neighbours belonging to the next chunk.
        private int[] _field;
        private int _halo;
        private int _fieldW;

        // What earlier passes have RESERVED, over the padded extent:
        //   _reservedR[c]  = max over earlier winners w of (radius(w) - dist(w, c))
        //   _reservedBy[c] = the radius that produced that maximum
        // so a candidate of radius r is too close exactly when _reservedR + r > 0.
        //
        // Padded, and stamped from winners rather than from PLACEMENTS, because
        // a placement is order-dependent and a winner is not. That is the whole
        // point: cross-class spacing checked against this chunk's placements
        // alone was blind at every chunk boundary, which is where every
        // remaining violation was — 46 of 46, measured.
        // [0] understory, [1] canopy. Separate because the two storeys reserve
        // from their own kind ONLY — a bush does not push a tree away and a tree
        // does not push a bush away, which is what an understory is.
        private readonly float[][] _reservedR = new float[2][];

        // Per pass: may a prop of this pass stand here at all? Precomputed over
        // the padded extent because the spacing test reads it once per cell in a
        // disc, and the underlying questions (is this painted, can a prop go
        // here) are the expensive ones.
        private bool[] _eligible;

        // Scene indices grouped by drawn size, largest class first. Shared by
        // every list in the chunk would be wrong — a class belongs to a list —
        // so this is built for the list of the chunk's first painted column and
        // rebuilt whenever a cell names a different one.
        public readonly System.Collections.Generic.List<int[]> Classes = new();

        // Which storey each entry of Classes belongs to. Canopy classes come
        // first and in full, then understory: the trees go in before anything
        // else has taken the ground, and a scene that fills both storeys
        // (a pine) appears in one class of each.
        private readonly System.Collections.Generic.List<bool> _classCanopy = new();

        // The same scenes bucketed by COLLISION radius instead, widest first —
        // the order the seal pass wants. The two orders genuinely differ: a tree
        // is the largest thing in a forest list and the narrowest thing in it.
        private readonly System.Collections.Generic.List<int[]> _sealClasses = new();

        // Every scene, smallest DRAWN radius first - the order the seal falls
        // back through once it has to break spacing.
        private int[] _byDrawnAscending = System.Array.Empty<int>();

        private PropListData _classesFor;

        // How many props of each scene this chunk has already placed, which is
        // what varietyPressure pushes against.
        private readonly System.Collections.Generic.Dictionary<(int, int), int> _used = new();

        private readonly System.Collections.Generic.List<Vector2I> _columns = new();

        public FillWork(WorldMapState map, Image layer, uint salt, int baseX, int baseZ)
        {
            _map = map;
            _layer = layer;
            _salt = salt;
            _baseX = baseX;
            _baseZ = baseZ;
            BuildDepthField();
            for (int tier = 0; tier < 2; tier++)
            {
                _reservedR[tier] = new float[_fieldW * _fieldW];
                for (int i = 0; i < _reservedR[tier].Length; i++)
                {
                    _reservedR[tier][i] = float.NegativeInfinity;
                }
            }
            EnsureClasses(FirstPaintedList());
        }

        // A chamfer distance transform over the chunk plus a halo, seeded from
        // every column the layer does NOT paint. Two sweeps, 3-4 weights, so a
        // diagonal costs about what a diagonal is worth; divided back to metres
        // at the end.
        //
        // The halo is what makes this a property of the region rather than of
        // the chunk. Without it every chunk boundary reads as a region edge and
        // the seal pass fences the inside of a wood along it.
        private void BuildDepthField()
        {
            PropListData list = FirstPaintedList();
            // Deep enough for two reservations plus the jitter between them,
            // or a prop at the chunk edge cannot see what the next chunk has
            // already reserved and the spacing lapses at every boundary.
            int reach = list == null
                ? 8
                : Mathf.Max(list.barrierDepthMeters + list.densityRampMeters,
                    2 * Mathf.CeilToInt(MaxSpacingRadius(list) + list.positionJitter) + 2);
            _halo = Mathf.Clamp(reach, 4, MaxDepthHalo);
            int w = Size + _halo * 2;
            _fieldW = w;
            var field = new int[w * w];
            const int Far = int.MaxValue / 4;
            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < w; z++)
                {
                    bool painted = _map.PaintedPropIndex(
                        _layer, _baseX - _halo + x, _baseZ - _halo + z) >= 0;
                    field[x * w + z] = painted ? Far : 0;
                }
            }
            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < w; z++)
                {
                    int best = field[x * w + z];
                    best = Mathf.Min(best, Neighbour(field, w, x - 1, z) + 3);
                    best = Mathf.Min(best, Neighbour(field, w, x, z - 1) + 3);
                    best = Mathf.Min(best, Neighbour(field, w, x - 1, z - 1) + 4);
                    best = Mathf.Min(best, Neighbour(field, w, x + 1, z - 1) + 4);
                    field[x * w + z] = best;
                }
            }
            for (int x = w - 1; x >= 0; x--)
            {
                for (int z = w - 1; z >= 0; z--)
                {
                    int best = field[x * w + z];
                    best = Mathf.Min(best, Neighbour(field, w, x + 1, z) + 3);
                    best = Mathf.Min(best, Neighbour(field, w, x, z + 1) + 3);
                    best = Mathf.Min(best, Neighbour(field, w, x + 1, z + 1) + 4);
                    best = Mathf.Min(best, Neighbour(field, w, x - 1, z + 1) + 4);
                    field[x * w + z] = best;
                }
            }
            _field = field;
        }

        // Metres inside the painted region, for a cell given in chunk-local
        // coordinates that may lie in the halo (so negative, or past Size).
        private int Depth(int lx, int lz)
        {
            int x = Mathf.Clamp(lx + _halo, 0, _fieldW - 1);
            int z = Mathf.Clamp(lz + _halo, 0, _fieldW - 1);
            return _field[x * _fieldW + z] / 3;
        }

        // The widest reservation any prop in the list makes, which bounds how
        // far the pairwise test has to look.
        private static float MaxSpacingRadius(PropListData list)
        {
            float widest = 0f;
            for (int i = 0; i < list.SceneCount; i++)
            {
                widest = Mathf.Max(widest,
                    Mathf.Max(list.Reservation(i, true), list.Reservation(i, false)));
            }
            return widest;
        }

        private static int Neighbour(int[] field, int w, int x, int z)
            => x < 0 || x >= w || z < 0 || z >= w ? int.MaxValue / 4 : field[x * w + z];

        private PropListData FirstPaintedList()
        {
            for (int lx = 0; lx < Size; lx++)
            {
                for (int lz = 0; lz < Size; lz++)
                {
                    int idx = _map.PaintedPropIndex(_layer, _baseX + lx, _baseZ + lz);
                    if (idx >= 0)
                    {
                        return _map.PropLists[idx];
                    }
                }
            }
            return null;
        }

        // Scene indices bucketed by drawn radius, largest first. A new bucket
        // starts wherever the radius falls off a step, so the classes follow the
        // sizes an author actually put in the list instead of a fixed count.
        private void EnsureClasses(PropListData list)
        {
            if (list == null || ReferenceEquals(list, _classesFor))
            {
                return;
            }
            _classesFor = list;
            BuildClasses(list);
            BucketByCollision(list, _sealClasses);
            var ascending = new int[list.SceneCount];
            var drawn = new float[list.SceneCount];
            for (int i = 0; i < ascending.Length; i++)
            {
                ascending[i] = i;
                drawn[i] = list.ShapeOf(i)?.VisualRadius ?? 0f;
            }
            System.Array.Sort(drawn, ascending);
            _byDrawnAscending = ascending;
        }

        private void BuildClasses(PropListData list)
        {
            Classes.Clear();
            _classCanopy.Clear();
            AddTierClasses(list, canopy: true);
            AddTierClasses(list, canopy: false);
        }

        // One storey's scenes, bucketed by what they RESERVE (not by what they
        // draw), largest first. On the raw drawn radius an oak and a birch
        // landed in different classes while the cap had already made their
        // reservations identical - so they ran as separate passes competing for
        // the same ground, the oak pass took every slot, and no birch was ever
        // placed in the wood.
        private void AddTierClasses(PropListData list, bool canopy)
        {
            var members = new System.Collections.Generic.List<int>();
            for (int i = 0; i < list.SceneCount; i++)
            {
                if (list.FillsTier(i, canopy))
                {
                    members.Add(i);
                }
            }
            if (members.Count == 0)
            {
                return;
            }
            var order = members.ToArray();
            var keys = new float[order.Length];
            for (int i = 0; i < order.Length; i++)
            {
                keys[i] = -list.Reservation(order[i], canopy);
            }
            System.Array.Sort(keys, order);
            var bucket = new System.Collections.Generic.List<int>();
            float bucketTop = 0f;
            for (int i = 0; i < order.Length; i++)
            {
                float radius = -keys[i];
                if (bucket.Count > 0 && radius < bucketTop * ClassSizeStep)
                {
                    Classes.Add(bucket.ToArray());
                    _classCanopy.Add(canopy);
                    bucket.Clear();
                }
                if (bucket.Count == 0)
                {
                    bucketTop = radius;
                }
                bucket.Add(order[i]);
            }
            if (bucket.Count > 0)
            {
                Classes.Add(bucket.ToArray());
                _classCanopy.Add(canopy);
            }
        }

        // The seal's own order: widest COLLISION first, and UNDERSTORY only.
        //
        // Widest first because a tree is the largest thing in a forest list and
        // seals one column where a bush half its size seals seven, so the widest
        // that fits is the whole of "as few props as possible".
        //
        // Understory only because sealing is undergrowth's job. A barrier is
        // made of the low storey and trees stand IN it — letting the seal reach
        // for a tree stood a pine's crown against a maple, since a pine has the
        // widest collision in the list. A list with no understory in it falls
        // back to everything, or it could not seal at all.
        private static void BucketByCollision(PropListData list,
            System.Collections.Generic.List<int[]> into)
        {
            into.Clear();
            var members = new System.Collections.Generic.List<int>();
            for (int i = 0; i < list.SceneCount; i++)
            {
                if (list.FillsTier(i, false))
                {
                    members.Add(i);
                }
            }
            if (members.Count == 0)
            {
                for (int i = 0; i < list.SceneCount; i++)
                {
                    members.Add(i);
                }
            }
            int count = members.Count;
            var order = members.ToArray();
            var keys = new float[count];
            for (int i = 0; i < count; i++)
            {
                keys[i] = -(list.ShapeOf(order[i])?.CollisionRadius ?? 0f);
            }
            System.Array.Sort(keys, order);
            var bucket = new System.Collections.Generic.List<int>();
            float bucketTop = 0f;
            for (int i = 0; i < count; i++)
            {
                float radius = -keys[i];
                if (bucket.Count > 0 && radius < bucketTop * ClassSizeStep)
                {
                    into.Add(bucket.ToArray());
                    bucket.Clear();
                }
                if (bucket.Count == 0)
                {
                    bucketTop = radius;
                }
                bucket.Add(order[i]);
            }
            if (bucket.Count > 0)
            {
                into.Add(bucket.ToArray());
            }
        }

        // One size class, everywhere in the chunk it is wanted.
        //
        // Where a prop of this class stands is decided WITHOUT reference to any
        // order — a cell wins if it out-ranks every rival within the class's
        // radius, which is a question any chunk answers the same way about any
        // cell. That is what makes the spacing survive a chunk boundary: a
        // greedy walk decides from whatever it happened to place first, so each
        // chunk started its stand afresh and two oaks could end up a metre apart
        // across a seam. Roughly three quarters of a chunk's cells lie within an
        // oak's canopy of one, so this was most of them.
        //
        // The radius is the CLASS's, not the chosen scene's, so the geometry is
        // settled before anything picks a scene — which is what lets the scene
        // choice keep its running variety count without that count feeding back
        // into where things stand.
        public int SpacingRounds(int pass)
        {
            PropListData list = FirstPaintedList();
            EnsureClasses(list);
            return list == null || pass >= Classes.Count ? 0 : Mathf.Max(1, list.spacingRounds);
        }

        public void RunSpacingPass(PropFill fill, int pass, int round)
        {
            PropListData classList = FirstPaintedList();
            EnsureClasses(classList);
            if (classList == null || pass >= Classes.Count)
            {
                return;
            }
            uint passSalt = _salt + (uint)pass * 977u + (uint)round * 31771u;
            bool canopy = _classCanopy[pass];
            float single = ClassDrawnRadius(classList, Classes[pass], canopy);
            float radius = PairDistance(classList, single);
            BuildEligibility(classList, passSalt, radius);
            foreach (int cell in _map.FillOrder(_baseX / Size, _baseZ / Size, passSalt))
            {
                int lx = cell / Size;
                int lz = cell % Size;
                if (fill.Covered[cell] || !Wins(lx, lz, radius, passSalt))
                {
                    continue;
                }
                int px = _baseX + lx;
                int pz = _baseZ + lz;
                int listIdx = _map.PaintedPropIndex(_layer, px, pz);
                if (listIdx < 0)
                {
                    continue;
                }
                PropListData list = _map.PropLists[listIdx];
                EnsureClasses(list);
                if (pass >= Classes.Count)
                {
                    continue;
                }
                int scene = ChooseInClass(list, listIdx, Classes[pass],
                    ToFloat01(Hash(px, pz, passSalt + 1u)));
                if (scene >= 0)
                {
                    TryPlace(fill, list, listIdx, scene, canopy, cell, lx, lz, px, pz, passSalt);
                }
            }
            StampPassReservations(classList, canopy, single, radius, passSalt);
            _eligible = null;
        }

        // The widest reservation any scene of this class makes. Per CLASS and
        // not per scene so the geometry is settled before anything picks a
        // scene, which is what lets the scene choice keep its variety count
        // without that count feeding back into where things stand.
        private static float ClassDrawnRadius(PropListData list, int[] sceneClass, bool canopy)
        {
            float widest = 0f;
            for (int i = 0; i < sceneClass.Length; i++)
            {
                widest = Mathf.Max(widest, list.Reservation(sceneClass[i], canopy));
            }
            return widest;
        }

        // How far apart two props of the same radius must stand: each reserves
        // its own, so the gap is the SUM - plus the jitter both ends may spend
        // walking toward each other, which is otherwise subtracted from every
        // gap in the world. A 2 m reservation with 0.35 m of wander at each end
        // put birches 1.3 m apart.
        private static float PairDistance(PropListData list, float single)
            => 2f * single + 2f * list.positionJitter;

        // Write this pass's winners into the reservation field, for every cell
        // near enough to the chunk that a later pass could be refused by it.
        private void StampPassReservations(PropListData list, bool canopy, float single,
            float radius, uint passSalt)
        {
            float[] field = _reservedR[canopy ? 1 : 0];
            float claim = single + list.positionJitter;
            int spread = Mathf.CeilToInt(claim + MaxSpacingRadius(list) + list.positionJitter);
            int from = Mathf.Max(0, _halo - spread);
            int to = Mathf.Min(_fieldW, _halo + Size + spread);
            for (int x = from; x < to; x++)
            {
                for (int z = from; z < to; z++)
                {
                    if (!WinsAt(x, z, radius, passSalt))
                    {
                        continue;
                    }
                    for (int dx = -spread; dx <= spread; dx++)
                    {
                        for (int dz = -spread; dz <= spread; dz++)
                        {
                            int ox = x + dx;
                            int oz = z + dz;
                            if (ox < 0 || ox >= _fieldW || oz < 0 || oz >= _fieldW)
                            {
                                continue;
                            }
                            float reach = claim - Mathf.Sqrt(dx * dx + dz * dz);
                            int c = ox * _fieldW + oz;
                            if (reach > field[c])
                            {
                                field[c] = reach;
                            }
                        }
                    }
                }
            }
        }

        // Which cells of the padded extent could carry a prop of this pass. The
        // density roll lives here rather than at placement so that a cell thinned
        // out by it does not go on to suppress its neighbours.
        private void BuildEligibility(PropListData classList, uint passSalt, float radius)
        {
            _eligible = new bool[_fieldW * _fieldW];
            for (int x = 0; x < _fieldW; x++)
            {
                for (int z = 0; z < _fieldW; z++)
                {
                    int px = _baseX - _halo + x;
                    int pz = _baseZ - _halo + z;
                    int listIdx = _map.PaintedPropIndex(_layer, px, pz);
                    if (listIdx < 0 || !_map.CanPlacePropAt(px, pz))
                    {
                        continue;
                    }
                    PropListData list = _map.PropLists[listIdx];
                    float density = DensityAt(list, Depth(x - _halo, z - _halo));
                    _eligible[x * _fieldW + z] =
                        ToFloat01(Hash(px, pz, passSalt + 5u)) < density;
                }
            }
        }

        // Does this cell out-rank every eligible cell within `radius`? Ranked by
        // hash, with the coordinates breaking a tie, so the comparison is a
        // strict order and exactly one cell of any pair can win.
        private bool Wins(int lx, int lz, float radius, uint passSalt)
            => WinsAt(lx + _halo, lz + _halo, radius, passSalt);

        // The same question in PADDED coordinates, so a cell belonging to the
        // next chunk can be asked it too.
        private bool WinsAt(int x0, int z0, float radius, uint passSalt)
        {
            if (!_eligible[x0 * _fieldW + z0])
            {
                return false;
            }
            int lx = x0 - _halo;
            int lz = z0 - _halo;
            uint mine = Hash(_baseX + lx, _baseZ + lz, passSalt + 7u);
            int reach = Mathf.CeilToInt(radius);
            float radiusSq = radius * radius;
            for (int dx = -reach; dx <= reach; dx++)
            {
                for (int dz = -reach; dz <= reach; dz++)
                {
                    if ((dx == 0 && dz == 0) || dx * dx + dz * dz > radiusSq)
                    {
                        continue;
                    }
                    int x = lx + dx + _halo;
                    int z = lz + dz + _halo;
                    if (x < 0 || x >= _fieldW || z < 0 || z >= _fieldW
                        || !_eligible[x * _fieldW + z])
                    {
                        continue;
                    }
                    uint theirs = Hash(_baseX + lx + dx, _baseZ + lz + dz, passSalt + 7u);
                    if (theirs > mine || (theirs == mine && (dx < 0 || (dx == 0 && dz < 0))))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        // The edge band, and only the edge band, must come out solid — that is
        // what painting a region MEANS. Spacing is off here by construction: a
        // gap in a barrier is not breathing room.
        public void RunSealPass(PropFill fill)
        {
            uint sealSalt = _salt + 4231u;
            foreach (int cell in _map.FillOrder(_baseX / Size, _baseZ / Size, sealSalt))
            {
                if (fill.Covered[cell])
                {
                    continue;
                }
                int lx = cell / Size;
                int lz = cell % Size;
                int px = _baseX + lx;
                int pz = _baseZ + lz;
                int listIdx = _map.PaintedPropIndex(_layer, px, pz);
                if (listIdx < 0 || !_map.CanPlacePropAt(px, pz))
                {
                    continue;
                }
                PropListData list = _map.PropLists[listIdx];
                if (Depth(lx, lz) > list.barrierDepthMeters)
                {
                    continue;
                }
                // WIDEST COLLISION first, which is a different order from the
                // spacing passes' widest CANOPY: a tree is the biggest thing in
                // the list and seals a single column, where a bush half its size
                // seals seven. Sealing with the widest thing that fits is the
                // whole of "as few props as possible" — the alternative, taking
                // the smallest that fits, is what ringed every region in pebbles.
                EnsureClasses(list);
                bool placed = false;
                for (int i = 0; i < _sealClasses.Count && !placed; i++)
                {
                    int scene = ChooseInClass(list, listIdx, _sealClasses[i],
                        ToFloat01(Hash(px, pz, sealSalt + 1u)));
                    placed = scene >= 0 && TryPlace(fill, list, listIdx, scene,
                        SealTier(list, scene), cell, lx, lz, px, pz, sealSalt,
                        respectReservation: false);
                }
                // Nothing the weighted picks offered fits. Try every understory
                // scene, widest collision first and un-jittered: one pick per
                // bucket samples a handful, and a jitter is what pushes a
                // footprint over the region's edge, so a column can read as
                // "too tight" while something would have sat in it.
                for (int i = 0; i < _sealClasses.Count && !placed; i++)
                {
                    int[] bucket = _sealClasses[i];
                    // Entered at a hashed offset rather than always at [0]. The
                    // sweep takes the first member that fits, so a fixed start
                    // means one scene of each bucket does all the sealing: six
                    // bushes of identical width came out 240 / 51 / 54 / 54 /
                    // 51 / 49.
                    int start = (int)(Hash(px, pz, sealSalt + 8u) % (uint)bucket.Length);
                    for (int j = 0; j < bucket.Length && !placed; j++)
                    {
                        int scene = bucket[(start + j) % bucket.Length];
                        placed = TryPlace(fill, list, listIdx, scene, SealTier(list, scene),
                            cell, lx, lz, px, pz, sealSalt,
                            allowJitter: false, respectReservation: false);
                    }
                }
                // Still nothing: a column of the barrier that no undergrowth
                // fits. EVERYTHING is on the table now, canopy included, taken
                // least visually intrusive first — a gap plugged with a pebble
                // is invisible where the same gap plugged with a tree is not.
                // This is a true last resort; when it was reachable in the
                // ordinary case it filled whole regions with the smallest thing
                // in the list (872 pebbles and 451 of the smallest bush, out of
                // 1404 props).
                for (int i = 0; i < _byDrawnAscending.Length && !placed; i++)
                {
                    placed = TryPlace(fill, list, listIdx, _byDrawnAscending[i],
                        SealTier(list, _byDrawnAscending[i]), cell, lx, lz, px, pz, sealSalt,
                        allowJitter: false, respectReservation: false);
                }
                if (!placed)
                {
                    // Nothing in the list is small enough for this column. Left
                    // uncovered on purpose: a prop that reaches outside the
                    // painted region puts a boulder in the road beside the wood.
                    fill.NoFit[cell] = true;
                }
            }
        }

        // Pose the prop, check it stays inside the painted region, and write it
        // in. Returns whether anything was placed.
        private bool TryPlace(PropFill fill, PropListData list, int listIdx, int scene,
            bool canopy, int cell, int lx, int lz, int px, int pz, uint passSalt,
            bool allowJitter = true, bool respectReservation = true)
        {
            float yaw = ToFloat01(Hash(px, pz, passSalt + 2u)) * Mathf.Tau;
            float wander = allowJitter ? list.positionJitter : 0f;
            var offset = new Vector2(
                (ToFloat01(Hash(px, pz, passSalt + 3u)) - 0.5f) * 2f * wander,
                (ToFloat01(Hash(px, pz, passSalt + 4u)) - 0.5f) * 2f * wander);
            list.Rasterize(scene, yaw, offset, _columns);
            foreach (Vector2I column in _columns)
            {
                // Painted, not placeable: a lake or a paved square the author
                // painted over is still inside the region they drew, and
                // refusing to reach across one would open a lane through the
                // barrier at every puddle.
                if (_map.PaintedPropIndex(_layer, px + column.X, pz + column.Y) < 0)
                {
                    return false;
                }
                // No two props may COLLIDE in the same column. Canopies are
                // meant to interlock and spacing is what tunes that, but two
                // solid volumes sharing ground is a bush growing through a
                // trunk, and no amount of spacing tuning hides it.
                //
                // The rule is deliberately about collision and not about the
                // drawn radius: gating on what is DRAWN would push every bush
                // clear of every canopy and there would be no understory at all.
                int cx = lx + column.X;
                int cz = lz + column.Y;
                if (cx >= 0 && cx < Size && cz >= 0 && cz < Size && fill.Covered[cx * Size + cz])
                {
                    return false;
                }
            }

            if (respectReservation && Reserved(list, scene, canopy, lx, lz))
            {
                return false;
            }

            float scale = 1f + ToFloat01(Hash(px, pz, passSalt + 6u)) * list.scaleJitter;
            fill.Origins[cell] = new PaintedProp(listIdx, scene, yaw, offset, scale);
            _used[(listIdx, scene)] = _used.GetValueOrDefault((listIdx, scene)) + 1;
            foreach (Vector2I column in _columns)
            {
                int ox = lx + column.X;
                int oz = lz + column.Y;
                // A column past the chunk edge belongs to the next chunk's fill,
                // which covers it itself. Dropping it is what keeps a fill a
                // pure function of its own chunk.
                if (ox >= 0 && ox < Size && oz >= 0 && oz < Size)
                {
                    fill.Covered[ox * Size + oz] = true;
                    fill.Own[ox * Size + oz] = true;
                }
            }
            return true;
        }

        // Is this cell inside a reservation an EARLIER pass made, and is this
        // prop too big to count as understory beneath whatever made it?
        //
        // The understory exception is what keeps this from banning undergrowth:
        // something far smaller than what it stands under is not competing with
        // it for room, and only collision keeps those two apart.
        // A scene the seal reaches for answers to whichever storey it fills;
        // one that fills both takes the understory's model, which is the denser
        // of the two and so the one that lets a gap actually be closed.
        private static bool SealTier(PropListData list, int scene)
            => !list.FillsTier(scene, false);

        private bool Reserved(PropListData list, int scene, bool canopy, int lx, int lz)
        {
            float claimed = _reservedR[canopy ? 1 : 0][(lx + _halo) * _fieldW + (lz + _halo)];
            return !float.IsNegativeInfinity(claimed)
                && claimed + list.Reservation(scene, canopy) + list.positionJitter > 0f;
        }

        // Full density inside the edge band, falling to the list's interior
        // density over the ramp past it. Smooth, because a step here is a line
        // drawn around every painted region at exactly the band depth.
        private static float DensityAt(PropListData list, int depth)
        {
            if (depth <= list.barrierDepthMeters || list.densityRampMeters <= 0)
            {
                return depth <= list.barrierDepthMeters ? 1f : list.interiorDensity;
            }
            float t = Mathf.Clamp(
                (depth - list.barrierDepthMeters) / (float)list.densityRampMeters, 0f, 1f);
            return Mathf.Lerp(1f, list.interiorDensity, t * t * (3f - 2f * t));
        }

        // A weighted pick within one size class, biased toward scenes this chunk
        // has not used yet. Without the bias a patch small enough to take in at
        // a glance is mostly whichever scene the weights favour, however many
        // the author put in the list.
        private int ChooseInClass(PropListData list, int listIdx, int[] sceneClass, float roll)
        {
            float total = 0f;
            for (int i = 0; i < sceneClass.Length; i++)
            {
                total += EffectiveWeight(list, listIdx, sceneClass[i]);
            }
            if (total <= 0f)
            {
                return -1;
            }
            float target = Mathf.Clamp(roll, 0f, 0.999999f) * total;
            for (int i = 0; i < sceneClass.Length; i++)
            {
                target -= EffectiveWeight(list, listIdx, sceneClass[i]);
                if (target < 0f)
                {
                    return sceneClass[i];
                }
            }
            return sceneClass[sceneClass.Length - 1];
        }

        private float EffectiveWeight(PropListData list, int listIdx, int scene)
        {
            float weight = list.WeightOf(scene);
            return weight / (1f + list.varietyPressure * _used.GetValueOrDefault((listIdx, scene)));
        }
    }

    // How far a depth field is worth carrying past a chunk, in metres. Past this
    // the answer stops changing anything: the density ramp has arrived and every
    // column reads as interior.
    private const int MaxDepthHalo = 40;

    // A size class ends where the drawn radius drops below this fraction of the
    // class's largest, so classes follow the sizes in the list rather than a
    // count someone picked.
    private const float ClassSizeStep = 0.6f;

    // Is this column far enough inside a painted region that the barrier does
    // not depend on it? Eight probes at the list's band depth — the four axes
    // and the four diagonals — all of which have to land on ground the same
    // layer would fill. Probing rather than eroding a mask keeps this a pure
    // function of the column, which is what lets a chunk's fill stay
    // reproducible from its own coordinates.
    //
    // The probe asks the RASTER and the water, not the whole of CanPlacePropAt:
    // a paved square or an entity's footprint inside a wood is a hole in the
    // barrier either way, and walking the placement list eight times per
    // candidate column is what that accuracy would cost.
    private bool IsPropInterior(Image layer, int px, int pz, int depth)
    {
        int diagonal = Mathf.Max(1, Mathf.RoundToInt(depth * 0.7071f));
        for (int i = 0; i < InteriorProbeX.Length; i++)
        {
            int step = i < 4 ? depth : diagonal;
            int qx = px + InteriorProbeX[i] * step;
            int qz = pz + InteriorProbeZ[i] * step;
            // Off the map is an edge: a region running to the world boundary is
            // still a region whose rim someone can walk along.
            if (qx < 0 || qx >= Data.ImageWidth || qz < 0 || qz >= Data.ImageHeight
                || PaintedPropIndex(layer, qx, qz) < 0 || Underwater(qx, qz))
            {
                return false;
            }
        }
        return true;
    }

    // Axes first, then diagonals — the step differs, so the order is read by
    // IsPropInterior rather than being incidental.
    private static readonly int[] InteriorProbeX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] InteriorProbeZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

    // Nothing in this column's list fits here without reaching outside the
    // painted region — the author's to fix, by painting wider or by adding a
    // smaller prop to the list.
    public bool CollidableNoFitAt(int px, int pz)
    {
        PropFill fill = FillFor(false, FloorDiv(px, ChunkState.SIZE), FloorDiv(pz, ChunkState.SIZE));
        return fill.NoFit[LocalCell(px, pz)];
    }

    // Public because worldmap_check has to tell a clearing the fill MEANT to
    // leave from a hole in a barrier, which is a bug.
    public bool CollidableInteriorAt(int px, int pz)
    {
        PropListData list = PaintedCollidableAt(px, pz);
        return list != null && IsPropInterior(CollidableProps, px, pz, list.barrierDepthMeters);
    }

    // The chunk's cells ordered by a hash of each — a stable shuffle,
    // reproducible from (cx, cz) alone.
    private int[] FillOrder(int cx, int cz, uint salt)
    {
        int n = ChunkState.SIZE * ChunkState.SIZE;
        var cells = new int[n];
        var keys = new uint[n];
        for (int i = 0; i < n; i++)
        {
            cells[i] = i;
            keys[i] = Hash(cx * ChunkState.SIZE + i / ChunkState.SIZE,
                cz * ChunkState.SIZE + i % ChunkState.SIZE, salt + 2u);
        }
        System.Array.Sort(keys, cells);
        return cells;
    }

    // Where a painted prop's base sits, in world Y.
    //
    // A flat column's drawn top is half a voxel above the surface voxel's top
    // face — the mesher's shallow-Y smoothing — which is what PropSurfaceLift
    // is and why a prop anchored at the face alone is buried. On a GRADE the
    // mesher stops snapping that vertex to the face and averages the cell's edge
    // crossings instead, so the drawn surface runs as a plane through the column
    // and the flat anchor floats a prop off the downhill side of it.
    //
    // The estimate is that plane at the column centre: the mean of the facing
    // surfaces around it, which is the same average the mesher takes. A
    // neighbour outside the grade window is a WALL — the mesher keeps the
    // surface crisp against it and it says nothing about this column's height —
    // so it is left out rather than allowed to drag a clifftop prop down.
    //
    // Two clamps, and both are the point of the exercise:
    //   - Never LIFT above the flat anchor. Where the ground rises into the
    //     column the prop embeds into it, which is the honest answer for a
    //     barrier: a prop standing proud of the hill has a gap under it.
    //   - Never sink past the surface voxel's top face. The prop's own Y is what
    //     picks the cell the nav grid marks blocked (PathBlockerRasterizer takes
    //     floor(Y)), so half a voxel lower is the last position that still marks
    //     the AIR cell a mob would walk through. Sink past it and the barrier
    //     stops blocking anything, which is the one failure worth clamping for.
    public float PropSeatY(int px, int pz)
    {
        int h = TerrainHeight(px, pz);
        int step = MaxGradeStep;
        int sum = h;
        int n = 1;
        for (int d = 0; d < 4; d++)
        {
            int nh = TerrainHeight(px + NeighbourDx[d], pz + NeighbourDz[d]);
            if (Mathf.Abs(nh - h) <= step)
            {
                sum += nh;
                n++;
            }
        }
        float drop = Mathf.Clamp(sum / (float)n - h, -PropMaxEmbed, 0f);
        return h + PropSurfaceLift + drop;
    }

    // The drawn surface of a flat column, over its surface voxel's top face.
    private const float PropSurfaceLift = 1.5f;

    // How far into the ground a prop may be seated. Exactly the lift, so the
    // deepest seat is the top face itself — see PropSeatY.
    private const float PropMaxEmbed = 0.5f;

    // Re-read every prop list and its scenes from disk, then drop the fills
    // derived from them.
    //
    // NOTHING about the fill is authored or stored — the raster holds only which
    // list covers a column, and which props that takes is worked out afresh
    // every time it is asked. So a wider tree trunk or a scene added to a list
    // needs no edit to the document at all: it needs the answer recomputed,
    // which is what this is. The bake runs it first, so re-saving the map is the
    // whole workflow.
    public void RefreshPropAssets()
    {
        foreach (PropListData list in PropLists)
        {
            list?.Refresh();
        }
        InvalidatePropFill();
    }

    // The fill reads the prop rasters, the terrain under them and the placement
    // list, so almost any edit can change it and it is dropped wholesale rather
    // than by rect. It is cheap to rebuild — one chunk at a time, on demand —
    // and a stale one would silently bake props that are not on the map.
    public void InvalidatePropFill()
    {
        lock (_propFillLock)
        {
            _propFills.Clear();
        }
    }

    // --- Mobs: placement, matching WorldGen's own list rates -----------------
    //
    // Does anything spawn at this column, and from which palette entry? Drives
    // the map's mob dots; returns -1 for nothing.
    public int PreviewMobAt(int px, int pz)
    {
        SpawnSetData set = MobSetAt(px, pz, out float density);
        if (set == null || !CanSpawnAt(px, pz))
        {
            return -1;
        }
        SpawnListRow[] rows = set.RowsFlat;
        if (rows == null)
        {
            return -1;
        }
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] != null
                && AreaRoll(Hash(px, pz, ENTITY_SALT + (uint)i), rows[i].squareMetersPerSpawn, density))
            {
                return IndexOfSet(MobSets, set);
            }
        }
        return -1;
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
        return !IsGradeAt(px, pz) && !InBlockingRegion(px, pz) && CanPlacePropAt(px, pz);
    }

    // Is this column inside a painted BLOCKING region? Nothing spawns in one.
    //
    // The whole region, not merely the columns a prop ended up standing in: an
    // interior clearing is ground the fill left bare precisely because nobody
    // can reach it, and a mob spawned there is a mob walled in for the life of
    // the world. The same goes for the gaps between trunks along the edge —
    // whatever fits between them is not somewhere to put an encounter.
    //
    // The BREAKABLE layer deliberately does not count. It is passable by
    // construction — that is what makes it breakable, and tall grass is walked
    // straight through — so treating it the same way would sterilize every
    // meadow of wildlife.
    //
    // Runtime ambient spawners need no equivalent: NightMobSpawner and
    // FairySpawner both pick from a reachability flood out of the player
    // (NavigationGoals.CollectReachableStandableCells), and a sealed interior is
    // unreachable once the props are in it — props block the nav grid through
    // PropSimState.GetPathBlockerCells.
    public bool InBlockingRegion(int px, int pz)
        => PaintedPropIndex(CollidableProps, px, pz) >= 0;

    // The breakable twin, for the map to draw. NOT a spawn gate — see above.
    public bool InBreakableRegion(int px, int pz)
        => PaintedPropIndex(DestructibleProps, px, pz) >= 0;

    // The same gate MINUS the grade clause, for a prop the author put there by
    // hand. Everything left is "there is no ground here" or "something else owns
    // this column"; the grade clause alone is a statement of TASTE, inherited
    // from a scatter pass that did not want lone trees down a generated
    // hillside. A painted barrier wants exactly the opposite — a wall that stops
    // at the foot of a slope is a wall with a way around it.
    //
    // The cost is that a prop on a graded column is seated at that column's own
    // top rather than on the plane the mesher draws, so it sits slightly into
    // the slope. A visible seam is worth less than a gap you can walk through.
    public bool CanPlacePropAt(int px, int pz)
    {
        return !Underwater(px, pz)
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

    // Independent salts: the two prop layers pick their scene, rotation and
    // jitter off these, so sharing one would stand the same tree and the same
    // bush at every column the layers both cover.
    internal const uint COLLIDABLE_SALT = 0x9E37u;
    internal const uint DESTRUCTIBLE_SALT = 0x2545u;
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
        Data.SaveCollidableProps(CollidableProps);
        Data.SaveDestructibleProps(DestructibleProps);
        Data.SaveGround(Ground);
        Data.SaveWaterType(WaterType);
        Data.SavePaving(Paving);
        SavePlacements();
        // The ledger has already grown by whatever this session discovered;
        // saving is what makes those slots permanent.
        SavePalettes();
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
