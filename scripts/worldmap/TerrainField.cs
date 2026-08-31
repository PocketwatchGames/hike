using Godot;

// The weathered height and water fields, derived from a document's elevation,
// water and roughness layers and cached per column.
//
// A CACHE WITH AN INVALIDATION PROTOCOL, which is the whole reason it is its own
// type: it is rebuilt over a dirty rect, and a path that edits those layers
// without calling Invalidate gets a silently wrong answer rather than an error.
// Kept as loose arrays on WorldMapState, that contract had nowhere to live.
//
// Erosion is always measured against the PRISTINE painted height, never against
// an already-weathered neighbour, so the model cannot feed on itself and
// painting the same wall twice cannot crumble it.
public class TerrainField
{
    private readonly WorldMapState Map;

    public TerrainField(WorldMapState map)
    {
        Map = map;
    }

    // Topmost solid voxel of the column, weathering included.
    public int Height(int px, int pz)
    {
        EnsureHeights();
        return _heights[Map.ClampZ(pz) * Map.Data.ImageWidth + Map.ClampX(px)];
    }

    // Surface of the water at a column, or Map.NoWater where it has been erased.
    public int Water(int px, int pz)
    {
        EnsureHeights();
        return _water[Map.ClampZ(pz) * Map.Data.ImageWidth + Map.ClampX(px)];
    }

    // Merge a region into the pending rebuild. The water fill is GLOBAL — a notch
    // cut in a rim moves a shoreline on the far side of the lake — so water
    // cannot be updated over a rect and is simply rebuilt with it.
    public void Invalidate(Rect2I texelRect)
    {
        if (_heights == null)
        {
            return;
        }
        _heightsDirty = _heightsDirty.Size.X <= 0 ? texelRect : _heightsDirty.Merge(texelRect);
    }

    // Drop the cache outright — for anything that rewrites a whole layer (a
    // resize, a reload).
    public void InvalidateAll()
    {
        _heights = null;
        _heightsDirty = default;
    }

    private void EnsureHeights()
    {
        if (_heights == null)
        {
            _heights = new int[Map.Data.ImageWidth * Map.Data.ImageHeight];
            _water = new int[Map.Data.ImageWidth * Map.Data.ImageHeight];
            RebuildHeights(new Rect2I(0, 0, Map.Data.ImageWidth, Map.Data.ImageHeight));
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
        int w = Map.Data.ImageWidth;
        int h = Map.Data.ImageHeight;
        int spread = Mathf.Clamp(Map.Data.roughenMaxSpreadVoxels, 1, 32);
        Rect2I outer = Clip(Inflate(rect, spread + 1), w, h);
        Rect2I src = Clip(Inflate(outer, spread), w, h);
        // Water first: Map.StandHeight reads it while the erosion below is computed,
        // and it is a straight per-column read with no neighbourhood, so the same
        // rect covers it.
        _water ??= new int[w * h];
        for (int x = src.Position.X; x < src.Position.X + src.Size.X; x++)
        {
            for (int z = src.Position.Y; z < src.Position.Y + src.Size.Y; z++)
            {
                float painted = Map.WaterVoxels(x, z);
                _water[z * w + x] = painted < Map.NoWaterVoxels + 0.5f
                    ? Map.NoWater
                    : Map.ColumnHeight(painted);
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

        int band = Mathf.Max(0, Map.Data.roughenKeepBandVoxels);
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
                int raw = Map.RawHeight(px, pz);
                int value = raw + raise[i] - lower[i];
                // Talus fills the shallows at the foot of a sea cliff but never
                // emerges from them: a shelf that breaks the surface is
                // somewhere to stand, and standing there is what shortens the
                // cliff. Only ever clamps a RAISE on an already-submerged
                // column, so dry ground is untouched by this.
                int water = Map.WaterSurface(px, pz);
                if (water > raw)
                {
                    value = Mathf.Min(value, water);
                }
                _heights[pz * w + px] = Mathf.Clamp(value, Map.Data.WorldMinY, Map.Data.WorldMaxY);
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
        raw = Map.StandHeight(px, pz);
        int band = Mathf.Max(0, Map.Data.roughenKeepBandVoxels);
        int minCliff = Mathf.Max(band + 1, Map.Data.roughenMinCliffVoxels);
        float here = Map.RoughnessAt(px, pz);

        int lowest = raw;
        int highest = raw;
        var lowestAt = new Vector2I(px, pz);
        var highestAt = new Vector2I(px, pz);
        for (int d = 0; d < 4; d++)
        {
            int nx = px + WorldMapState.NeighbourDx[d];
            int nz = pz + WorldMapState.NeighbourDz[d];
            int nh = Map.StandHeight(nx, nz);
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
            float strength = Mathf.Max(here, Map.RoughnessAt(lowestAt.X, lowestAt.Y));
            int budget = Mathf.FloorToInt((drop - band) * strength + 0.5f);
            lipShare = Mathf.FloorToInt(budget * (1f - RoughenSplit(lowestAt)));
        }
        int rise = highest - raw;
        if (rise >= minCliff)
        {
            float strength = Mathf.Max(here, Map.RoughnessAt(highestAt.X, highestAt.Y));
            int budget = Mathf.FloorToInt((rise - band) * strength + 0.5f);
            footShare = budget - Mathf.FloorToInt(budget * (1f - RoughenSplit(new Vector2I(px, pz))));
        }
    }

    // Cone of influence around one end of a cliff. `below` keeps talus on the
    // low side and crumble on the high side — without it a foot's cone would
    // also raise the lip above it and undo the erosion.
    private void Splat(int[] acc, Rect2I outer, int cx, int cz, int share, int refHeight, bool below)
    {
        int run = Mathf.Max(1, Map.Data.roughenTalusRunPerVoxel);
        int reach = Mathf.Min(share * run, Mathf.Clamp(Map.Data.roughenMaxSpreadVoxels, 1, 32));
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
                int rh = Map.StandHeight(x, z);
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
        int raw = Map.StandHeight(px, pz);
        for (int d = 0; d < 4; d++)
        {
            int nh = Map.StandHeight(px + WorldMapState.NeighbourDx[d], pz + WorldMapState.NeighbourDz[d]);
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

    private float RoughenSplit(Vector2I texel)
    {
        _roughNoise ??= new FastNoiseLite
        {
            Seed = Map.Data.roughenNoiseSeed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = Map.Data.roughenNoiseFrequency,
        };
        Vector2I world = Map.WorldXZ(texel);
        return Mathf.Clamp(_roughNoise.GetNoise2D(world.X, world.Y) * 0.5f + 0.5f, 0f, 1f);
    }

    private FastNoiseLite _roughNoise;
    private int[] _heights;

    // Painted water surface per column, floored at the sea. Cached beside the
    // heights and refreshed by the same rebuild, so both are array reads on the
    // paths that ask per texel.
    private int[] _water;

    private Rect2I _heightsDirty;
}
