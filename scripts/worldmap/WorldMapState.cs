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

    public WorldState WorldState;  // baked voxels (built on demand at bake time)

    public Image Elevation;        // Rf, per column (normalized height, truth)
    public Image Water;            // Rf, per column (water surface height)
    public Image Region;           // R8, per chunk (region index)
    public Image Zone;             // R8, per chunk (zone index)
    public Image Wind;             // Rgba8, per chunk (R = angle, G = strength; G 0 = unpainted)
    public Image Scatter;          // Rgba8, per column (R = prop set + 1, G = density)
    public Image Ground;           // R8, per column (ground set + 1; 0 = default ground)
    public Image Paving;           // R8, per column (paving block + 1; 0 = none)

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
    private int[,] _topEdit;
    private int[,] _botEdit;

    // Display-only: the cutaway plane the voxel-edit views draw at, in world
    // voxel Y. Deliberately NOT a tool parameter — it is an INSPECTION control,
    // and sweeping it through a corridor is how a 2D map answers "how tall is
    // this". Shared, so switching between Tunnel and Block keeps the same slice
    // on screen. Not part of the document; nothing here is saved.
    public int CutawayY;

    // Is the cutaway actually cutting anything? Parked at the top of the world it
    // is inert, and saying so is what lets a cutting view behave EXACTLY as an
    // uncut one there — stamps included, which otherwise vanish, since a stamp is
    // drawn only where it reaches the plane and nothing reaches the world's roof.
    public bool IsCutAway => CutawayY < Data.WorldMaxY;

    // Display-only: whether the views draw standing water over the terrain.
    // Off shows the bare banded height field, which is what you want while
    // shaping a lake bed or a coast you have already flooded. Not part of the
    // document — nothing here is saved.
    public bool ShowWater = true;

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
        Wind = data.LoadOrCreateWind();
        Scatter = data.LoadOrCreateScatter();
        Ground = data.LoadOrCreateGround();
        Paving = data.LoadOrCreatePaving();
        Placements = LoadOrCreatePlacements(data);
        Mobs = data.LoadOrCreateMobs();
        Scalars = data.LoadOrCreateScalars();
        Tunnels = data.LoadOrCreateTunnels();
        CutawayY = data.WorldMaxY;
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

    // The world's grade discriminator: adjacent columns within it mesh as a
    // slope, beyond it as a wall. Resolved once — TerrainOf walks the document's
    // genData, and this is asked per column by the map preview.
    private int _maxGradeStep = -1;
    public int MaxGradeStep => _maxGradeStep >= 0
        ? _maxGradeStep
        : _maxGradeStep = Mathf.Max(1, WorldGen.TerrainOf(Data.genData).maxGradeStep);

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
        EnsureHeights();
        return _heights[ClampZ(pz) * Data.ImageWidth + ClampX(px)];
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
        if (_heights == null)
        {
            return;
        }
        _heightsDirty = _heightsDirty.Size.X <= 0 ? texelRect : _heightsDirty.Merge(texelRect);
    }

    public void InvalidateAllHeights()
    {
        _heights = null;
        _heightsDirty = default;
    }

    private void EnsureHeights()
    {
        if (_heights == null)
        {
            _heights = new int[Data.ImageWidth * Data.ImageHeight];
            _water = new int[Data.ImageWidth * Data.ImageHeight];
            RebuildHeights(new Rect2I(0, 0, Data.ImageWidth, Data.ImageHeight));
            _heightsDirty = default;
            return;
        }
        if (_heightsDirty.Size.X > 0)
        {
            Rect2I dirty = _heightsDirty;
            _heightsDirty = default;
            RebuildHeights(dirty);
        }
    }

    // Recompute the weathered height field over a region.
    //
    // Three passes, and each exists because of a way the naive version broke a
    // cliff open:
    //
    // 1. SPREAD. A cliff's budget is splatted as a cone that decays a voxel per
    //    roughenTalusRunPerVoxel metres, rather than dumped into the one column
    //    at the wall. Dumping it produced a step as tall as the budget — a 5m
    //    wall became a 2m step plus a 3m wall, and a 2m step is mantleable.
    // 2. CAP. A column may not rise (or fall) so far that it shortens ANY wall
    //    it touches below the band. Talus is computed from one cliff and is
    //    blind to the others the column abuts: at a junction of a 0m, 4m and 6m
    //    plateau, the 6m wall's talus rose 2m at the foot and left the 4m wall
    //    2m tall — free to mantle.
    // 3. RELAX. The cap alone reintroduces steps, because a capped column sits
    //    beside an uncapped one. Both fields are forced 1-Lipschitz afterwards,
    //    which can only reduce them, so the cap still holds.
    private void RebuildHeights(Rect2I rect)
    {
        int w = Data.ImageWidth;
        int h = Data.ImageHeight;
        int spread = Mathf.Clamp(Data.roughenMaxSpreadVoxels, 1, 32);
        Rect2I outer = Clip(Inflate(rect, spread + 1), w, h);
        Rect2I src = Clip(Inflate(outer, spread), w, h);
        // Water first: StandHeight reads it while the erosion below is computed,
        // and it is a straight per-column read with no neighbourhood, so the same
        // rect covers it.
        _water ??= new int[w * h];
        for (int x = src.Position.X; x < src.Position.X + src.Size.X; x++)
        {
            for (int z = src.Position.Y; z < src.Position.Y + src.Size.Y; z++)
            {
                float painted = WaterVoxels(x, z);
                _water[z * w + x] = painted < NoWaterVoxels + 0.5f
                    ? NoWater
                    : ColumnHeight(painted);
            }
        }

        int ow = outer.Size.X;
        int oh = outer.Size.Y;
        var raise = new int[ow * oh];
        var lower = new int[ow * oh];

        for (int sx = src.Position.X; sx < src.Position.X + src.Size.X; sx++)
        {
            for (int sz = src.Position.Y; sz < src.Position.Y + src.Size.Y; sz++)
            {
                ComputeShares(sx, sz, out int lipShare, out int footShare, out int selfRaw);
                if (footShare > 0)
                {
                    Splat(raise, outer, sx, sz, footShare, selfRaw, true);
                }
                if (lipShare > 0)
                {
                    Splat(lower, outer, sx, sz, lipShare, selfRaw, false);
                }
            }
        }

        int band = Mathf.Max(0, Data.roughenKeepBandVoxels);
        for (int x = 0; x < ow; x++)
        {
            for (int z = 0; z < oh; z++)
            {
                int px = outer.Position.X + x;
                int pz = outer.Position.Y + z;
                int i = z * ow + x;
                if (raise[i] > 0 || lower[i] > 0)
                {
                    Caps(px, pz, band, out int maxRaise, out int maxLower);
                    raise[i] = Mathf.Min(raise[i], maxRaise);
                    lower[i] = Mathf.Min(lower[i], maxLower);
                }
            }
        }

        Relax(raise, ow, oh);
        Relax(lower, ow, oh);

        for (int x = 0; x < ow; x++)
        {
            for (int z = 0; z < oh; z++)
            {
                int px = outer.Position.X + x;
                int pz = outer.Position.Y + z;
                int i = z * ow + x;
                int raw = RawHeight(px, pz);
                int value = raw + raise[i] - lower[i];
                // Talus fills the shallows at the foot of a sea cliff but never
                // emerges from them: a shelf that breaks the surface is
                // somewhere to stand, and standing there is what shortens the
                // cliff. Only ever clamps a RAISE on an already-submerged
                // column, so dry ground is untouched by this.
                int water = WaterSurface(px, pz);
                if (water > raw)
                {
                    value = Mathf.Min(value, water);
                }
                _heights[pz * w + px] = Mathf.Clamp(value, Data.WorldMinY, Data.WorldMaxY);
            }
        }
    }

    // What this column owes its cliffs: how far its lip may crumble and how much
    // talus may pile against its foot.
    //
    // Both ends of one cliff sample the split at the FOOT column and take the
    // strength of whichever end is painted, so they agree about the budget: the
    // two shares are the floor and the ceiling of complementary parts of it, and
    // sum to exactly the budget however the noise falls. That is what leaves the
    // band standing, and it means painting either side of a cliff weathers it.
    private void ComputeShares(int px, int pz, out int lipShare, out int footShare, out int raw)
    {
        lipShare = 0;
        footShare = 0;
        raw = StandHeight(px, pz);
        int band = Mathf.Max(0, Data.roughenKeepBandVoxels);
        int minCliff = Mathf.Max(band + 1, Data.roughenMinCliffVoxels);
        float here = RoughnessAt(px, pz);

        int lowest = raw;
        int highest = raw;
        var lowestAt = new Vector2I(px, pz);
        var highestAt = new Vector2I(px, pz);
        for (int d = 0; d < 4; d++)
        {
            int nx = px + NeighbourDx[d];
            int nz = pz + NeighbourDz[d];
            int nh = StandHeight(nx, nz);
            if (nh < lowest)
            {
                lowest = nh;
                lowestAt = new Vector2I(nx, nz);
            }
            if (nh > highest)
            {
                highest = nh;
                highestAt = new Vector2I(nx, nz);
            }
        }

        int drop = raw - lowest;
        if (drop >= minCliff)
        {
            float strength = Mathf.Max(here, RoughnessAt(lowestAt.X, lowestAt.Y));
            int budget = Mathf.FloorToInt((drop - band) * strength + 0.5f);
            lipShare = Mathf.FloorToInt(budget * (1f - RoughenSplit(lowestAt)));
        }
        int rise = highest - raw;
        if (rise >= minCliff)
        {
            float strength = Mathf.Max(here, RoughnessAt(highestAt.X, highestAt.Y));
            int budget = Mathf.FloorToInt((rise - band) * strength + 0.5f);
            footShare = budget - Mathf.FloorToInt(budget * (1f - RoughenSplit(new Vector2I(px, pz))));
        }
    }

    // Cone of influence around one end of a cliff. `below` keeps talus on the
    // low side and crumble on the high side — without it a foot's cone would
    // also raise the lip above it and undo the erosion.
    private void Splat(int[] acc, Rect2I outer, int cx, int cz, int share, int refHeight, bool below)
    {
        int run = Mathf.Max(1, Data.roughenTalusRunPerVoxel);
        int reach = Mathf.Min(share * run, Mathf.Clamp(Data.roughenMaxSpreadVoxels, 1, 32));
        for (int dx = -reach; dx <= reach; dx++)
        {
            int x = cx + dx;
            if (x < outer.Position.X || x >= outer.Position.X + outer.Size.X)
            {
                continue;
            }
            for (int dz = -reach; dz <= reach; dz++)
            {
                int z = cz + dz;
                if (z < outer.Position.Y || z >= outer.Position.Y + outer.Size.Y)
                {
                    continue;
                }
                int dist = Mathf.RoundToInt(Mathf.Sqrt(dx * dx + dz * dz));
                int amount = share - (dist + run - 1) / run;
                if (amount <= 0)
                {
                    continue;
                }
                int rh = StandHeight(x, z);
                if (below ? rh > refHeight : rh < refHeight)
                {
                    continue;
                }
                int i = (z - outer.Position.Y) * outer.Size.X + (x - outer.Position.X);
                acc[i] = Mathf.Max(acc[i], amount);
            }
        }
    }

    // How far this column may move before it shortens one of ITS OWN walls below
    // the band. A wall already shorter than the band contributes a cap of zero,
    // so weathering never touches it at all.
    private void Caps(int px, int pz, int band, out int maxRaise, out int maxLower)
    {
        maxRaise = int.MaxValue;
        maxLower = int.MaxValue;
        int raw = StandHeight(px, pz);
        for (int d = 0; d < 4; d++)
        {
            int nh = StandHeight(px + NeighbourDx[d], pz + NeighbourDz[d]);
            if (nh > raw)
            {
                maxRaise = Mathf.Min(maxRaise, Mathf.Max(0, nh - raw - band));
            }
            else if (nh < raw)
            {
                maxLower = Mathf.Min(maxLower, Mathf.Max(0, raw - nh - band));
            }
        }
        if (maxRaise == int.MaxValue)
        {
            maxRaise = 0;
        }
        if (maxLower == int.MaxValue)
        {
            maxLower = 0;
        }
    }

    // Force the field 1-Lipschitz: no neighbouring pair may differ by more than
    // a voxel, so the talus is a ramp rather than a set of steps. Two sweeps are
    // enough for a 4-connected grid, and both only ever REDUCE, which is what
    // lets this run after the caps without breaking them.
    private static void Relax(int[] field, int w, int h)
    {
        for (int z = 0; z < h; z++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = z * w + x;
                int best = int.MaxValue;
                if (x > 0) { best = Mathf.Min(best, field[i - 1]); }
                if (z > 0) { best = Mathf.Min(best, field[i - w]); }
                if (best != int.MaxValue) { field[i] = Mathf.Min(field[i], best + 1); }
            }
        }
        for (int z = h - 1; z >= 0; z--)
        {
            for (int x = w - 1; x >= 0; x--)
            {
                int i = z * w + x;
                int best = int.MaxValue;
                if (x < w - 1) { best = Mathf.Min(best, field[i + 1]); }
                if (z < h - 1) { best = Mathf.Min(best, field[i + w]); }
                if (best != int.MaxValue) { field[i] = Mathf.Min(field[i], best + 1); }
            }
        }
    }

    private static Rect2I Inflate(Rect2I r, int by)
    {
        return new Rect2I(r.Position.X - by, r.Position.Y - by, r.Size.X + by * 2, r.Size.Y + by * 2);
    }

    private static Rect2I Clip(Rect2I r, int w, int h)
    {
        int x0 = Mathf.Max(0, r.Position.X);
        int z0 = Mathf.Max(0, r.Position.Y);
        int x1 = Mathf.Min(w, r.Position.X + r.Size.X);
        int z1 = Mathf.Min(h, r.Position.Y + r.Size.Y);
        return new Rect2I(x0, z0, Mathf.Max(0, x1 - x0), Mathf.Max(0, z1 - z0));
    }

    private static readonly int[] NeighbourDx = { 1, -1, 0, 0 };
    private static readonly int[] NeighbourDz = { 0, 0, 1, -1 };

    // 0 = the erosion all comes off the top, 1 = it all piles at the base.
    private float RoughenSplit(Vector2I texel)
    {
        _roughNoise ??= new FastNoiseLite
        {
            Seed = Data.roughenNoiseSeed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = Data.roughenNoiseFrequency,
        };
        Vector2I world = WorldXZ(texel);
        return Mathf.Clamp(_roughNoise.GetNoise2D(world.X, world.Y) * 0.5f + 0.5f, 0f, 1f);
    }

    private FastNoiseLite _roughNoise;
    private int[] _heights;

    // Painted water surface per column, floored at the sea. Cached beside the
    // heights and refreshed by the same rebuild, so both are array reads on the
    // paths that ask per texel.
    private int[] _water;

    private Rect2I _heightsDirty;

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
        EnsureHeights();
        return _water[ClampZ(pz) * Data.ImageWidth + ClampX(px)];
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
    // ONE rule, read by the map's ink AND by the bake that files the cascade
    // entities (BuildWaterfallSites). Two copies of it would let the map promise
    // falls the world does not build.
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
        EnsureTopEdits();
        if (edit != EditNone)
        {
            _topEdit[px, pz] = Mathf.Max(_topEdit[px, pz], wy);
            _botEdit[px, pz] = Mathf.Min(_botEdit[px, pz], wy);
        }
        else if (_topEdit[px, pz] == wy || _botEdit[px, pz] == wy)
        {
            // Only a column that just lost its highest or lowest edit rescans,
            // and only its own height.
            int top = Data.WorldMinY - 1;
            int bot = int.MaxValue;
            for (int y = 0; y < Data.VoxelHeight; y++)
            {
                if (Tunnels[px, y, pz] != EditNone)
                {
                    top = Data.WorldMinY + y;
                    bot = Mathf.Min(bot, top);
                }
            }
            _topEdit[px, pz] = top;
            _botEdit[px, pz] = bot;
        }
    }

    // Anything that rewrites the edit layer wholesale — an undo restore, a
    // resize — drops the summary instead of maintaining it.
    public void InvalidateVoxelEdits()
    {
        _topEdit = null;
    }

    private void EnsureTopEdits()
    {
        if (_topEdit != null)
        {
            return;
        }
        _topEdit = new int[Data.ImageWidth, Data.ImageHeight];
        _botEdit = new int[Data.ImageWidth, Data.ImageHeight];
        int floor = Data.WorldMinY - 1;
        for (int px = 0; px < Data.ImageWidth; px++)
        {
            for (int pz = 0; pz < Data.ImageHeight; pz++)
            {
                _topEdit[px, pz] = floor;
                _botEdit[px, pz] = int.MaxValue;
            }
        }
        // In the mask's own memory order (ly is its middle index), scanning UP so
        // the last write per column is its highest edit. The obvious loop —
        // per column, downward until something is found — walks the array
        // against its stride and touches every one of its ~7M bytes as a cache
        // miss.
        for (int px = 0; px < Data.ImageWidth; px++)
        {
            for (int ly = 0; ly < Data.VoxelHeight; ly++)
            {
                for (int pz = 0; pz < Data.ImageHeight; pz++)
                {
                    if (Tunnels[px, ly, pz] != EditNone)
                    {
                        _topEdit[px, pz] = Data.WorldMinY + ly;
                        _botEdit[px, pz] = Mathf.Min(_botEdit[px, pz], Data.WorldMinY + ly);
                    }
                }
            }
        }
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
        EnsureTopEdits();
        int th = TerrainHeight(px, pz);
        int topEdit = _topEdit[ClampX(px), ClampZ(pz)];
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
        EnsureTopEdits();
        int x = ClampX(px);
        int z = ClampZ(pz);
        int th = TerrainHeight(px, pz);

        // An unedited column is solid to its terrain top and air above it, so
        // both answers are one comparison. This is nearly every column on the
        // map, and the walk below would otherwise run per texel per rebuild.
        if (_topEdit[x, z] < Data.WorldMinY)
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
        int stop = Mathf.Max(Data.WorldMinY, Mathf.Min(_botEdit[x, z], th));
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

    // The colour a CUTAWAY view draws at a column: the elevation band of the
    // highest floor under the cut, or the rock colour where the column is solid
    // the whole way down with nothing hollow beneath.
    //
    // A floor found THROUGH rock keeps its exact band and reports `buried`; the
    // painter dithers those cells against the rock colour rather than tinting
    // them, because a tint would move the band into a shade some other height
    // already owns — the one thing the palette exists to prevent. Water is
    // composited only where the cut is open to it, since a floor seen through
    // rock is not under the pool standing on that rock.
    public Color CutawayColorAt(int px, int pz, int clipY, out bool buried)
    {
        int floor = CutawayFloor(px, pz, clipY, out buried);
        if (floor < Data.WorldMinY)
        {
            return Data.cutawayRockColor;
        }
        Color band = ElevationColorAt(floor - SeaLevel);
        return buried ? band : WithWaterOver(band, px, pz, floor, clipY);
    }

    // The surface the MAP draws at a column: the top of the solid world, edits
    // included, cut away above a view's clip level, with standing water on top
    // of it where the view draws water. Nothing but display reads this — the
    // bake stamps from TerrainHeight and the edit layer directly.
    public int DisplaySurface(int px, int pz, bool withWater, int clipY = int.MaxValue)
    {
        int h = SurfaceBelow(px, pz, clipY);
        return withWater ? Mathf.Max(h, Mathf.Min(WaterSurface(px, pz), clipY)) : h;
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

        // The bake's OWN palette instance, handed to the world it builds. It
        // used to bind process-global tables from this background thread while
        // the painter was live on the main one — which is why "one bake at a
        // time" had to be a rule rather than a consequence.
        Blocks.Bind();
        var palette = KitPalette.Build(Data.genData.kitPalette, Data.genData.ZoneGens);

        var ws = new WorldState(Data.MinChunk, Data.MaxChunk, Data.genData.simData, palette);
        BindZoneKits(palette);

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

        // Stamp the authored subscenes over the terrain, before the routes and
        // the scatter: a scene brings its own floor and its own walls, so
        // anything measured off the ground has to see the building already
        // standing. Entities come with it, which is why this precedes the
        // scatter pass that would otherwise plant trees inside it.
        progress?.Invoke(STAMP_END, "Stamping scenes");
        StampPlacements();

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
        // never produces. The bake's SpawnContext hands it the painted field
        // instead (SpawnContextForBake), so the seam reaches exactly this pass
        // instead of every mob placed anywhere in the process.
        progress?.Invoke(STAMP_END, "Scattering entities");
        RescatterColumns(new Rect2I(0, 0, Data.ImageWidth, Data.ImageHeight));

        StampEntities();

        // Cascades, from the same bare-water edges the map inks (SpillsOver) and
        // through worldgen's own placement, so the lip/landing Y convention has
        // one home. Reads the painted layers rather than the stamped voxels, so
        // it does not care where in the bake it runs.
        progress?.Invoke(WRITE_START, "Placing waterfalls");
        WorldGen.PlaceWaterfalls(ws, BuildWaterfallSites());

        // Re-derive the surface SHAPE channel from the finished voxels, the same
        // pass and the same rule worldgen ends on. Without it every painted
        // column keeps the blanket SharpAxes.Y the stamp writes, so a slope
        // meshes as flat treads with 1 m vertical risers: it reads as a ramp
        // from above but the player walks into a wall, since a metre step is
        // below mantleMinRise and no floor is walkable at 90 degrees. Runs after
        // the scenes, the routes and the scatter, because it classifies whatever
        // geometry is actually there.
        progress?.Invoke(WRITE_START, "Grading slopes");
        WorldGen.StampGradeShapes(ws,
            Data.WorldMinX, Data.WorldMinX + Data.ImageWidth - 1,
            Data.WorldMinZ, Data.WorldMinZ + Data.ImageHeight - 1,
            MaxGradeStep);

        // Detail sprites — the grass blades and pebbles that are part of what
        // the ground LOOKS like up close, as opposed to the props standing on
        // it. WORLDGEN'S OWN pass, over the finished voxels, and it runs LAST
        // for the same reason worldgen runs it last: every ground-moving pass
        // before it overwrites the per-voxel channels wholesale, so a subscene
        // stamp left its footprint (and the kit-swapped ground it re-textured)
        // bald. Stamping detail per column during StampColumns was exactly that
        // bug. Paved columns are skipped — the tread is bare by construction —
        // and the dominant-zone kit pick is off, since a painted world assigns
        // kits per column deterministically and has no zone-weight kernel.
        progress?.Invoke(WRITE_START, "Scattering detail");
        WorldGen.StampDetailScatter(ws, Data.genData,
            (wx, wz) => PavingAt(wx - Data.WorldMinX, wz - Data.WorldMinZ) != null, false);

        progress?.Invoke(WRITE_START, "Seeding wind");
        StampWind(ws);

        // Authored spawn, or the world origin when none is placed.
        int spawnX = Placements.hasSpawn ? Placements.spawnXZ.X : 0;
        int spawnZ = Placements.hasSpawn ? Placements.spawnXZ.Y : 0;
        int spawnH = TerrainHeight(spawnX - Data.WorldMinX, spawnZ - Data.WorldMinZ);
        ws.Spawn = new Vector3(spawnX + 0.5f, spawnH + 2f, spawnZ + 0.5f);

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

    // Hand-placed entities, spawned through the SAME path a scattered one takes
    // (SpawnEntryData.TrySpawn with the bake's context), so a chest placed by
    // hand and one rolled by a spawn set differ in position and nothing else.
    //
    // The seed is the column, so re-baking an unchanged document places the same
    // thing twice — an entry that rolls a variant or a loot table must not
    // shuffle between bakes.
    private void StampEntities()
    {
        foreach (EntityPlacement placement in Placements.entities)
        {
            if (placement?.entry == null)
            {
                continue;
            }
            int px = placement.anchorXZ.X - Data.WorldMinX;
            int pz = placement.anchorXZ.Y - Data.WorldMinZ;
            int floor = placement.floorY;
            if (floor == EntityPlacement.OnTheGround)
            {
                // The top of the COLUMN, edits included — not the height field.
                // The two differ wherever something was carved or built, and an
                // entity that says it stands on the ground should stand on the
                // ground that is there: dig a pit under one and it drops in,
                // rather than hanging over the hole at the height the terrain
                // used to be.
                floor = SurfaceBelow(px, pz, int.MaxValue);
                if (floor < Data.WorldMinY)
                {
                    floor = TerrainHeight(px, pz);
                }
            }
            var pos = new Vector3(placement.anchorXZ.X + 0.5f, floor + 1f,
                placement.anchorXZ.Y + 0.5f);
            uint seed = Hash(px, pz, ENTITY_SALT);
            placement.entry.TrySpawn(WorldState, pos, new System.Random((int)seed), SpawnContextForBake());
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
        foreach (SubscenePlacement placement in Placements.placements)
        {
            SubsceneState sub = SubsceneFor(placement);
            if (sub == null)
            {
                continue;
            }
            var anchor = new Vector3(placement.anchorXZ.X, SeatY(placement), placement.anchorXZ.Y);
            SubsceneStamper.StampAll(WorldState, sub, anchor);
            GD.Print($"WorldMapState: stamped {placement.path.GetFile()} at {anchor} "
                + $"(size={sub.Size}, rot={(int)placement.rotation * 90}deg, yOffset={placement.yOffset})");
        }
    }

    // Bake phase boundaries, as a fraction of the whole job. MEASURED: with the
    // lighting pass gone, stamping is nearly all of it and the file write the
    // rest.
    private const float STAMP_START = 0.05f;
    private const float STAMP_END = 0.80f;
    private const float WRITE_START = 0.85f;

    // Map every paintable ground set onto palette slots. Runs after
    // WorldGen.BindActivePalettes, which is what builds that palette.
    private void BindZoneKits(KitPalette kits)
    {
        TerrainKitData[] palette = kits.Kits;

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
            : th - ShoreWaterSurface(px, pz) <= Data.shoreBandVoxels ? kits.Shore : kits.Surface;

        for (int wy = Data.WorldMinY; wy <= Data.WorldMaxY; wy++)
        {
            int desired;
            byte edit = VoxelEdit(px, pz, wy);
            if (edit == EditCarve)
            {
                // Carve removes GROUND; what stands in the space it opens is the
                // water layer's business, exactly as it is above the terrain. So
                // a passage bored below a painted surface comes out flooded —
                // which is the only way to paint water in a tunnel at all, and it
                // is undone the same way it is anywhere else, by erasing the
                // column's water. (Per COLUMN, so a dry passage cannot run under
                // a lake: the erase that drains the tunnel drains the lake too.)
                desired = wy <= wsurf ? Blocks.WaterId : Blocks.AirId;
            }
            else if (wy <= th || edit == EditAdd)
            {
                // Added geometry stands above the painted ground, so it takes
                // the zone's surface kit — or its submerged one where the new
                // voxel sits below a water surface.
                byte kit = wy > th
                    ? (wy <= wsurf ? kits.Submerged : kits.Surface)
                    : th - wy <= Data.surfaceDepthVoxels ? topKit : kits.Cave;
                WorldState.SetTerrainIdWorld(wx, wy, wz, kit);
                desired = WorldState.Kits.BlockFor(kit);
                if (wy == th)
                {
                    // Paving replaces the kit's block on the top voxel only —
                    // paving is a surface, and the rock under a road is still
                    // the hillside's. The kit channel keeps its own value: it is
                    // what the column IS made of, and a road laid over it does
                    // not change which zone's stone is underneath.
                    BlockData paving = PavingAt(px, pz);
                    if (paving != null)
                    {
                        desired = paving.blockId;
                    }
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

    // One metre of lip: the column the water leaves over, the way out over it,
    // and what it lands on. WaterfallLip is the same thing without the landing,
    // which is a property of the site rather than of one lip.
    private readonly struct SpillLip
    {
        public readonly int X;
        public readonly int Z;
        public readonly int DirX;
        public readonly int DirZ;
        public readonly int BottomVoxel;

        public SpillLip(int x, int z, int dirX, int dirZ, int bottomVoxel)
        {
            X = x;
            Z = z;
            DirX = dirX;
            DirZ = dirZ;
            BottomVoxel = bottomVoxel;
        }
    }

    // Every bare water edge on the map, grouped into cascades — the painted
    // world's answer to HeightMap.Waterfalls, which no painted world produces.
    // The drop stays AIR either way; a fall is an entity hanging off the lip.
    //
    // The lip is the column the water pours ONTO, carrying the direction AWAY
    // from the pool that feeds it: that is the contract WaterfallMeshBuilder
    // sweeps its sheet from
    // (it starts the jet half a metre back along that direction, on the face
    // between the two columns), and it is what worldgen's own sites record.
    //
    // Lips group 8-connected and BY THE LEVEL THEY POUR FROM. Connected, because
    // a five-wide sheet is one cascade and wants one effect across it rather
    // than five narrow ones side by side. Diagonally, because an outside corner
    // turns through a diagonal — the two perpendicular strips must reach the
    // same entity or the mesh builder cannot skirt the widening wedge between
    // them. By level, because two pools at different heights spilling past each
    // other are two falls, and merging them would put one sheet's top at the
    // other's water.
    public List<WaterfallSite> BuildWaterfallSites()
    {
        int w = Data.ImageWidth;
        int h = Data.ImageHeight;
        // Keyed by lip column AND pour level: one column can be the low side of
        // two different pools.
        var byCell = new Dictionary<(int Cell, int Top), List<SpillLip>>();
        for (int px = 0; px < w; px++)
        {
            for (int pz = 0; pz < h; pz++)
            {
                if (!Underwater(px, pz))
                {
                    continue;
                }
                for (int d = 0; d < 4; d++)
                {
                    int nx = px + NeighbourDx[d];
                    int nz = pz + NeighbourDz[d];
                    // Bounds, not clamps: the queries clamp to the edge column,
                    // which would invent a spill off the border of the map.
                    if (nx < 0 || nx >= w || nz < 0 || nz >= h || !SpillsOver(px, pz, nx, nz))
                    {
                        continue;
                    }
                    var key = (nz * w + nx, WaterSurface(px, pz));
                    if (!byCell.TryGetValue(key, out List<SpillLip> cell))
                    {
                        cell = new List<SpillLip>();
                        byCell[key] = cell;
                    }
                    // The landing is the lower side's VISIBLE top — the pool it
                    // falls into, or the bed where it lands dry. Same rule
                    // worldgen's sites use, and what lets WaterfallData's
                    // landingDepth carry the sheet under the surface it enters.
                    cell.Add(new SpillLip(nx, nz, NeighbourDx[d], NeighbourDz[d], VisibleSurface(nx, nz)));
                }
            }
        }

        var sites = new List<WaterfallSite>();
        var seen = new HashSet<(int Cell, int Top)>();
        var open = new Queue<(int Cell, int Top)>();
        var members = new List<SpillLip>();
        var cells = new List<int>();
        foreach ((int Cell, int Top) start in byCell.Keys)
        {
            if (!seen.Add(start))
            {
                continue;
            }
            open.Clear();
            open.Enqueue(start);
            members.Clear();
            cells.Clear();
            int bottom = int.MaxValue;
            long sumX = 0;
            long sumZ = 0;
            while (open.Count > 0)
            {
                (int Cell, int Top) key = open.Dequeue();
                int cx = key.Cell % w;
                int cz = key.Cell / w;
                cells.Add(key.Cell);
                sumX += cx;
                sumZ += cz;
                foreach (SpillLip lip in byCell[key])
                {
                    members.Add(lip);
                    // The DEEPEST landing under the sheet: a fall over uneven
                    // ground reaches the bottom of what it spans.
                    bottom = Mathf.Min(bottom, lip.BottomVoxel);
                }
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx;
                        int nz = cz + dz;
                        if (nx < 0 || nx >= w || nz < 0 || nz >= h)
                        {
                            continue;
                        }
                        var next = (nz * w + nx, key.Top);
                        if (byCell.ContainsKey(next) && seen.Add(next))
                        {
                            open.Enqueue(next);
                        }
                    }
                }
            }

            // The centroid of a curved or L-shaped lip line is not one of its
            // own columns — it lands in the pool the fall hangs off, and the
            // entity would file into the chunk there. Snap to the nearest
            // member, the same fix worldgen's sites and the POI resolver make.
            float avgX = sumX / (float)cells.Count;
            float avgZ = sumZ / (float)cells.Count;
            int bestCell = cells[0];
            float bestD = float.MaxValue;
            foreach (int cell in cells)
            {
                float dx = cell % w - avgX;
                float dz = cell / w - avgZ;
                if (dx * dx + dz * dz < bestD)
                {
                    bestD = dx * dx + dz * dz;
                    bestCell = cell;
                }
            }

            var lips = new WaterfallLip[members.Count];
            for (int i = 0; i < lips.Length; i++)
            {
                lips[i] = new WaterfallLip(members[i].X + Data.WorldMinX, members[i].Z + Data.WorldMinZ,
                    members[i].DirX, members[i].DirZ);
            }
            sites.Add(new WaterfallSite(
                new Vector3(bestCell % w + Data.WorldMinX + 0.5f, start.Top,
                    bestCell / w + Data.WorldMinZ + 0.5f),
                bottom, cells.Count, lips));
        }
        return sites;
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
        int best = WaterSurface(px, pz);
        for (int d = 0; d < 4; d++)
        {
            best = Mathf.Max(best, WaterSurface(px + NeighbourDx[d], pz + NeighbourDz[d]));
        }
        return best;
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
    // Paving is resolved HERE rather than in the paving view, so a road shows on
    // every view that draws ground — you cannot lay props or mobs sensibly along
    // a road you cannot see. It wins over the ground set because it is what the
    // surface is actually made of once paved.
    public Color GroundColorAt(int px, int pz)
    {
        BlockData paving = PavingAt(px, pz);
        if (paving != null)
        {
            return WithWater(paving.minimapColor, px, pz);
        }
        int idx = GroundIndexAt(px, pz);
        GroundSetData[] sets = GroundSets;
        Color c = idx >= 0 && idx < sets.Length && sets[idx] != null ? sets[idx].mapColor : UNPAINTED_GROUND;
        return WithWater(c, px, pz);
    }

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
        int ground = WorldGen.FootprintPlateauY(
            (x, z) => TerrainHeight(x - Data.WorldMinX, z - Data.WorldMinZ),
            Data.elevationStepVoxels, origin, sub.Size, out _);
        return ground + placement.yOffset;
    }

    // Top-down colour of a stamp's contents, one entry per footprint column,
    // alpha 0 where the scene has nothing. Built once per (scene, rotation) and
    // cached beside the rotated state — the map asks for this per texel per
    // rebuild, and scanning a building's full height every time would show.
    //
    // Turns a placed stamp from a featureless rectangle into a floor plan, which
    // is what makes a stamp placeable at all: which way the house faces and where
    // its walls are cannot be read off a wash.
    public Color[] SubscenePreview(SubscenePlacement placement)
    {
        return Plan(placement).Colors;
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
        int[] tops = Plan(placement).Tops;
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

    private (Color[] Colors, int[] Tops) Plan(SubscenePlacement placement)
    {
        SubsceneState sub = SubsceneFor(placement);
        if (sub == null)
        {
            return (System.Array.Empty<Color>(), System.Array.Empty<int>());
        }
        var key = (placement.path, (int)placement.rotation);
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
                for (int y = sub.Size.Y - 1; y >= 0; y--)
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

    private readonly System.Collections.Generic.Dictionary<(string, int), (Color[] Colors, int[] Tops)>
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
            (Color[] colors, int[] tops) = Plan(plan.Stamps[i]);
            plan.Colors[i] = colors;
            plan.Tops[i] = tops;
            if (plan.BaseYs != null)
            {
                plan.BaseYs[i] = StampBaseY(plan.Stamps[i]);
            }
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
    // On a CUTAWAY view a stamp is hidden once the plane drops BELOW it: the cut
    // has taken the building away, and its roof plan would otherwise paint over
    // whatever the cut exposed underneath — the passage you are boring under the
    // house. At or above its base it still draws, because you are looking down at
    // something that is still there — footprint wash included, so a plane parked
    // over everything renders exactly what no plane at all would.
    public Color StampColorAt(StampPlan plan, int px, int pz, Color under,
        SubscenePlacement selected)
    {
        if (plan == null)
        {
            return under;
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
            int at = lz * fp.Size.X + lx;
            int[] tops = plan.Tops[i];
            int top = at < tops.Length ? tops[at] : -1;
            if (plan.BaseYs != null && plan.BaseYs[i] > plan.ClipY)
            {
                continue;
            }
            bool isSelected = plan.Stamps[i] == selected;
            if (top >= 0)
            {
                // Selected is lifted toward white rather than recoloured, so the
                // plan stays readable while the selection is obvious.
                Color content = plan.Colors[i][at];
                return isSelected ? content.Lerp(Colors.White, 0.35f) : content;
            }
            Color ink = Data.placementInk;
            return under.Lerp(new Color(ink.R, ink.G, ink.B),
                isSelected ? ink.A * 0.5f : ink.A * 0.2f);
        }
        return under;
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

    // Painted ground index, or -1 where the column keeps its kit's ground.
    public int PavingIndexAt(int px, int pz)
    {
        int idx = Mathf.RoundToInt(Paving.GetPixel(ClampX(px), ClampZ(pz)).R * 255f) - 1;
        return idx >= 0 && idx < PavingBlocks.Length ? idx : -1;
    }

    public BlockData PavingAt(int px, int pz)
    {
        int idx = PavingIndexAt(px, pz);
        return idx >= 0 ? PavingBlocks[idx] : null;
    }

    public void SetPavingAt(int px, int pz, int index)
    {
        Paving.SetPixel(px, pz, new Color(Mathf.Clamp(index + 1, 0, 255) / 255f, 0f, 0f, 1f));
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
            && PavingAt(px, pz) == null
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
        int levelCap = Data.genData.mobLevelCap;
        return _bakeContext ??= new SpawnContext
        {
            SurfaceYAt = (wx, wz) => TerrainHeight(wx - Data.WorldMinX, wz - Data.WorldMinZ),
            IsValidColumn = (wx, wz) => CanSpawnAt(wx - Data.WorldMinX, wz - Data.WorldMinZ),
            IsFlatColumn = (wx, wz) => IsFlatAt(wx - Data.WorldMinX, wz - Data.WorldMinZ),
            // The painted difficulty layer, which is the only thing that knows a
            // mob's level in a world nothing generated.
            MobLevelOverride = (pos, baseLevel) =>
                Mathf.Clamp(baseLevel + MobLevelAtWorld(pos), 0, levelCap),
            // A forge takes its tier from the SAME painted layer. Its own scale
            // matches the mob one by design ("a forge sits at the same tier as
            // monsters in its zone"), and the danger you painted is the only
            // statement about difficulty a painted world contains.
            ForgeLevelOverride = pos => Mathf.Clamp(MobLevelAtWorld(pos), 0, levelCap),
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
        Data.SaveWind(Wind);
        Data.SaveScatter(Scatter);
        Data.SaveGround(Ground);
        Data.SavePaving(Paving);
        SavePlacements();
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
    // Before the wind layer existed the painter skipped this pass altogether, so
    // a baked .hike shipped a subgrid of stored zeros — signed zero, so not a
    // wrong wind, just no wind at all for the grass, the motes and mob drift to
    // read.
    private void StampWind(WorldState ws)
    {
        foreach (ChunkState chunk in ws._chunks.Values)
        {
            if (WindForChunk(chunk.ChunkCoord.X, chunk.ChunkCoord.Z, out Vector3 dir, out float speed))
            {
                WindGen.FillChunkWind(chunk, dir * speed);
            }
            else
            {
                WindGen.ComputeChunkWind(ws, chunk);
            }
        }
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
    public Color WithWater(Color terrain, int px, int pz, int clipY = int.MaxValue)
    {
        // Against the DISPLAYED surface, so a bridge built over a lake reads as
        // the land it is rather than as the water it spans.
        return WithWaterOver(terrain, px, pz, SurfaceBelow(px, pz, clipY), clipY);
    }

    // Same, for a caller that already knows the surface. Worth the overload: it
    // is called per texel per rebuild, and resolving the surface a second time
    // means a second walk down the column.
    public Color WithWaterOver(Color terrain, int px, int pz, int surface, int clipY)
    {
        if (!ShowWater)
        {
            return terrain;
        }
        int depth = Mathf.Min(WaterSurface(px, pz), clipY) - surface;
        if (depth <= 0)
        {
            return terrain;
        }
        // Shallow to deep across waterDeepAtVoxels, so a shoreline reads pale and
        // a lake bed dark: the map says how deep water is, not merely that it is
        // there. Two authored stops rather than a long ramp — the shore is the
        // edge an author aims at, and more stops make that edge harder to find.
        float t = Mathf.Clamp(depth / (float)Mathf.Max(1, Data.waterDeepAtVoxels), 0f, 1f);
        return Data.shallowWaterColor.Lerp(Data.deepWaterColor, t);
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
    // Reads the WEATHERED height, like the step outlines do. Colouring the raw
    // painted height instead left the bands saying one thing and the outlines
    // another, and the bands are the half an author reads a cliff's height from.
    public Color ElevationColor(int px, int pz, int clipY = int.MaxValue)
    {
        return ElevationColorAt(SurfaceBelow(px, pz, clipY) - SeaLevel);
    }

    // Same palette, addressed by height rather than by column — the brush cursor
    // shows the height it is about to write, which is not on the map yet.
    // Memo over the document's whole signed range. The palette is authored and
    // immutable at runtime, and this is asked per texel per rebuild, so the
    // banding math runs once per distinct height instead of ~295k times.
    private Color[] _bandMemo;

    public Color ElevationColorAt(int voxelsRelSea)
    {
        int lo = Mathf.FloorToInt(Data.minElevationVoxels) - 1;
        int hi = Mathf.CeilToInt(Data.maxElevationVoxels) + 1;
        if (voxelsRelSea >= lo && voxelsRelSea <= hi)
        {
            _bandMemo ??= new Color[hi - lo + 1];
            Color memo = _bandMemo[voxelsRelSea - lo];
            if (memo.A > 0f)
            {
                return memo;
            }
            Color made = BandColor(voxelsRelSea);
            _bandMemo[voxelsRelSea - lo] = made;
            return made;
        }
        return BandColor(voxelsRelSea);
    }

    private Color BandColor(int voxelsRelSea)
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
        // headroom to white, so the hue stays recognisably itself while getting
        // steadily paler and the step shows even in a channel that started near
        // full.
        //
        // The band's TOP metre lands at elevationBandMaxBrightness of the way to
        // white and the metres between divide that evenly, which makes the one
        // knob the band's whole contrast range. Note the top metre reaches it
        // exactly — dividing by `per` instead would spend part of the range on a
        // metre that belongs to the next band.
        Color baseColor = hues[((band % hues.Length) + hues.Length) % hues.Length];
        float lift = per > 1
            ? Data.elevationBandMaxBrightness * within / (per - 1)
            : 0f;
        return new Color(
            baseColor.R + (1f - baseColor.R) * lift,
            baseColor.G + (1f - baseColor.G) * lift,
            baseColor.B + (1f - baseColor.B) * lift);
    }
}
