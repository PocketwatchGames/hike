using System;
using Godot;

// Describes the broken stone ruins a zone scatters onto land (see
// ZoneGenData.ruins). A "ruin site" is a large patch — up to SiteRadius across —
// strewn with crumbled stone walls and 1x1/2x2/3x3 pillars, each a run/stack of
// VoxelType.Stone voxels. Stone carries the Stone tile and the mesher's
// fully-blocky SharpAxes.All shape (hard edges, zero tile-border noise) by
// default, so ruins read as man-made masonry, not organic terrain.
//
// Tolerant of noisy terrain: the site itself needs no flat ground, and each wall
// / pillar SELF-LEVELS against its local surface — it fills any dip under it with
// a short stone foundation (up to MaxStructureRelief voxels) so the masonry sits
// grounded and level across rolling ground, and simply breaks (leaves a gap)
// where the relief is too steep. WorldGen's PlaceRuins pass picks the anchors;
// this resource owns the geometry + tunables.
//
// Null on a zone = no ruins. SquareMetersPerSpawn == 0 likewise disables them.
[GlobalClass]
public partial class RuinsGenData : Resource
{
    // Average qualifying square meters between ruin-site anchors — inverse of a
    // per-1m² probability (4000 ≈ one site per 4000m² of land). Authored as a
    // friendly integer so the editor spinbox step doesn't round a sub-0.001
    // probability to zero. 0 disables ruins. Note MinSiteSpacing caps the actual
    // count regardless, so this mainly trades "sparse landmark" vs "common".
    [Export(PropertyHint.Range, "0,40000,1,or_greater")] public float squareMetersPerSpawn = 4000f;

    // Half-extent (meters) of one ruin site. Walls and pillars scatter anywhere
    // within this radius of the anchor — 15 gives the ~30x30m spread.
    [Export(PropertyHint.Range, "2,40,1")] public int siteRadius = 15;

    // Minimum spacing (meters) between two site anchors so big sites don't merge
    // into one indistinct rubble field. Keep >= SiteRadius * 2.
    [Export(PropertyHint.Range, "0,256,1")] public float minSiteSpacing = 50f;

    // Max terrain relief (voxels) a single wall/pillar tolerates across its own
    // footprint: dips up to this are filled with a stone foundation; columns
    // farther than this from the structure's base are dropped (a gap). This is
    // what lets ruins straddle noisy ground without floating or burying — raise
    // it for bigger, more aggressively-leveled structures.
    [Export(PropertyHint.Range, "0,12,1")] public int maxStructureRelief = 3;

    // Optional "confined" gate: sample a ring at ConfinementRadius around a
    // candidate and require at least ConfinementMinFraction of it to rise at
    // least ConfinementRise voxels above the anchor — biasing sites into hollows
    // / against cliff steps. ConfinementMinFraction 0 drops the gate (ruins then
    // need only dry land). On noisy terrain even a low fraction is easily met.
    [Export(PropertyHint.Range, "1,48,1")] public int confinementRadius = 12;
    [Export(PropertyHint.Range, "0,16,1")] public int confinementRise = 1;
    [Export(PropertyHint.Range, "0,1,0.05")] public float confinementMinFraction = 0f;

    // Broken walls: how many straight stone runs per site, each WallLength long,
    // WallHeight tall, WallThickness deep.
    [Export(PropertyHint.Range, "0,16,1")] public int wallCountMin = 3;
    [Export(PropertyHint.Range, "0,16,1")] public int wallCountMax = 6;
    [Export(PropertyHint.Range, "1,16,1")] public int wallHeightMin = 3;
    [Export(PropertyHint.Range, "1,16,1")] public int wallHeightMax = 5;
    [Export(PropertyHint.Range, "1,32,1")] public int wallLengthMin = 4;
    [Export(PropertyHint.Range, "1,32,1")] public int wallLengthMax = 12;
    [Export(PropertyHint.Range, "1,4,1")] public int wallThickness = 1;

    // Pillars: how many free-standing stone columns per site. Each picks a square
    // footprint of 1..PillarMaxSize voxels (1x1 / 2x2 / 3x3) and a PillarHeight.
    [Export(PropertyHint.Range, "0,16,1")] public int pillarCountMin = 3;
    [Export(PropertyHint.Range, "0,16,1")] public int pillarCountMax = 8;
    [Export(PropertyHint.Range, "1,16,1")] public int pillarHeightMin = 3;
    [Export(PropertyHint.Range, "1,16,1")] public int pillarHeightMax = 5;
    [Export(PropertyHint.Range, "1,3,1")] public int pillarMaxSize = 3;

    // 0..1 "ruined-ness": chance a wall slice is missing entirely (a gap) and
    // chance a given top voxel is knocked off, so silhouettes crumble instead
    // of reading as clean parapets.
    [Export(PropertyHint.Range, "0,1,0.05")] public float brokenness = 0.4f;

    public bool Enabled => squareMetersPerSpawn > 0f;

    // Stamp one ruin site centered on (originX, originZ). surfaceYAt resolves a
    // column's ground-top Y; isDry gates columns to land above water. RNG is the
    // shared worldgen ruins stream.
    public void Stamp(WorldState ws, int originX, int originZ, Random rng,
        Func<int, int, int> surfaceYAt, Func<int, int, bool> isDry)
    {
        int radius = Mathf.Max(1, siteRadius);
        int radiusSq = radius * radius;

        bool InSite(int x, int z)
        {
            int dx = x - originX;
            int dz = z - originZ;
            return dx * dx + dz * dz <= radiusSq;
        }

        // Stamp one structure column relative to a shared `baseY` (the structure's
        // local ground reference). Fills the dip below baseY with a stone
        // foundation so the column never floats, then raises `bodyHeight` of wall
        // above it. Drops the column when it's off-site, in water, or its ground
        // is more than MaxStructureRelief from baseY (the structure breaks there).
        // Stone's default SharpAxes.All gives the hard, blocky ruin edges.
        void StampColumn(int x, int z, int baseY, int bodyHeight)
        {
            if (bodyHeight <= 0 || !InSite(x, z) || !isDry(x, z)) { return; }
            int sy = surfaceYAt(x, z);
            if (Mathf.Abs(sy - baseY) > maxStructureRelief) { return; }
            // Foundation: fill ground-top+1 .. baseY when the ground dips below.
            for (int y = sy + 1; y <= baseY; y++)
            {
                ws.SetVoxelWorld(x, y, z, VoxelType.Stone);
            }
            // Body rises from whichever is higher so a bump pokes through rather
            // than getting buried.
            int bodyBase = Mathf.Max(baseY, sy);
            for (int k = 0; k < bodyHeight; k++)
            {
                ws.SetVoxelWorld(x, bodyBase + 1 + k, z, VoxelType.Stone);
            }
        }

        // Knock 0..2 voxels off a column's top so the crown looks crumbled.
        int Erode(int height)
        {
            if (rng.NextDouble() < brokenness)
            {
                height -= rng.Next(1, 3);
            }
            return Mathf.Max(1, height);
        }

        int wallSpread = Mathf.Max(1, Mathf.RoundToInt(radius * 0.7f));
        int walls = RandRange(rng, wallCountMin, wallCountMax);
        for (int w = 0; w < walls; w++)
        {
            bool alongX = rng.Next(2) == 0;
            int length = RandRange(rng, wallLengthMin, wallLengthMax);
            int height = RandRange(rng, wallHeightMin, wallHeightMax);
            int thickness = Mathf.Max(1, wallThickness);
            int ox = originX + rng.Next(-wallSpread, wallSpread + 1);
            int oz = originZ + rng.Next(-wallSpread, wallSpread + 1);
            int baseY = surfaceYAt(ox, oz);
            int half = length / 2;
            for (int i = -half; i <= length - half - 1; i++)
            {
                // Broken gap: occasionally drop a whole slice so the wall has holes.
                if (rng.NextDouble() < brokenness * 0.4) { continue; }
                int sliceH = Erode(height);
                for (int t = 0; t < thickness; t++)
                {
                    int x = alongX ? ox + i : ox + t;
                    int z = alongX ? oz + t : oz + i;
                    StampColumn(x, z, baseY, sliceH);
                }
            }
        }

        int pillarSpread = Mathf.Max(1, Mathf.RoundToInt(radius * 0.85f));
        int pillars = RandRange(rng, pillarCountMin, pillarCountMax);
        for (int p = 0; p < pillars; p++)
        {
            int size = Mathf.Clamp(rng.Next(1, Mathf.Clamp(pillarMaxSize, 1, 3) + 1), 1, 3);
            int height = RandRange(rng, pillarHeightMin, pillarHeightMax);
            int ox = originX + rng.Next(-pillarSpread, pillarSpread + 1);
            int oz = originZ + rng.Next(-pillarSpread, pillarSpread + 1);
            int baseY = surfaceYAt(ox, oz);
            for (int fx = 0; fx < size; fx++)
            {
                for (int fz = 0; fz < size; fz++)
                {
                    StampColumn(ox + fx, oz + fz, baseY, Erode(height));
                }
            }
        }
    }

    // Inclusive integer range roll, tolerant of swapped/equal bounds.
    private static int RandRange(Random rng, int min, int max)
    {
        if (max < min) { (min, max) = (max, min); }
        return rng.Next(min, max + 1);
    }
}
