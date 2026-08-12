using System;
using System.Collections.Generic;
using Godot;

// The CELLULAR approach's placed landforms — offshore islands, mesas, quarries,
// craters, terraced stairs and land bridges. Part of CellularTerrainGen; see
// CellularTerrainGen.cs for the pipeline these hang off and CellularTerrainData
// for the knobs.
//
// Two rules run through all of it.
//
// ONE PASS, not one per landform. Mesas, quarries, craters and stairs are the
// same operation — pick cells matching a topological rule over the cell graph,
// move them, pin what was moved — so they share PlaceLandforms and differ only
// in the rule and the effect. A fifth landform is a rule and an effect, not
// another pass.
//
// BY COUNT, never by probability per cell. A per-cell chance gives a world that
// is 3 mesas at one seed and 30 at the next, and an author who wants "three
// mesas in this world" has no way to say so. Counts are also what makes these
// notable: the whole point of a mesa is that there are not many.
public partial class CellularTerrainGen
{
    // ------------------------------------------------------- offshore islands

    // One island or sea stack, as a disc added to the CONTINENT MASK.
    private struct IslandDisc
    {
        public float Cx;        // local column coords
        public float Cz;
        public float Radius;
        public bool SeaStack;
    }

    // Attempts made per island before giving up on it. The candidate rules are
    // strict (at sea, in a distance band off the shore, inside the edge ring,
    // clear of the others), so a rejected sample is the normal case and this
    // only has to be large enough that a legal site is found when one exists.
    private const int ISLAND_PLACEMENT_ATTEMPTS = 6000;

    // Add each island's falloff to the continent mask, BEFORE the mask decides
    // land from sea. That ordering is the entire design: the partition, the
    // median flattening, the quantizeStep lattice, the coastal relief taper and
    // the sliver merge then all treat an island as ordinary land, so it comes
    // out terraced like the mainland for nothing. A height stamp applied
    // afterwards would sit outside every one of those and read as a foreign
    // object dropped in the sea.
    //
    // `coastBase` is modified in place; `stackTaper` receives the sea stacks'
    // taper-skip weight (see below).
    private List<IslandDisc> PlaceOffshoreIslands(float[,] coastBase, float[,] reliefH,
        float[,] stackTaper, int worldMinX, int worldMinZ, float edgeMargin, CellularTerrainData cd)
    {
        var placed = new List<IslandDisc>();
        int islandCount = Math.Max(0, cd.islandCount);
        int stackCount = Math.Max(0, cd.seaStackCount);
        if (islandCount + stackCount == 0) { return placed; }

        int sizeX = coastBase.GetLength(0);
        int sizeZ = coastBase.GetLength(1);

        // Distance to the nearest land, in columns. An island has to sit in an
        // authored band off the shore — close enough to read as belonging to
        // this coast, far enough to be its own thing — and a multi-source flood
        // from every land column answers that for the whole map in one walk.
        var shoreDistance = new int[sizeX, sizeZ];
        var queue = new Queue<int>();
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                bool land = coastBase[lx, lz] >= 0f;
                shoreDistance[lx, lz] = land ? 0 : int.MaxValue;
                if (land) { queue.Enqueue(lx * sizeZ + lz); }
            }
        }
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int lx = idx / sizeZ;
            int lz = idx % sizeZ;
            int next = shoreDistance[lx, lz] + 1;
            for (int d = 0; d < 4; d++)
            {
                int nx = lx + NEIGHBOUR_DX[d];
                int nz = lz + NEIGHBOUR_DZ[d];
                if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                if (shoreDistance[nx, nz] <= next) { continue; }
                shoreDistance[nx, nz] = next;
                queue.Enqueue(nx * sizeZ + nz);
            }
        }
        // A world with no land at all leaves every distance at MaxValue, which
        // would pass the "far enough from shore" test everywhere.
        bool anyLand = shoreDistance[0, 0] != int.MaxValue || queue.Count > 0;
        for (int lx = 0; lx < sizeX && !anyLand; lx++)
        {
            for (int lz = 0; lz < sizeZ && !anyLand; lz++)
            {
                anyLand = shoreDistance[lx, lz] == 0;
            }
        }
        if (!anyLand) { return placed; }

        var rng = new Random(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_ISLAND));
        float spacing = Math.Max(1f, cd.islandSpacingMeters);
        float shoreMin = Math.Min(cd.islandShoreDistanceMin, cd.islandShoreDistanceMax);
        float shoreMax = Math.Max(cd.islandShoreDistanceMin, cd.islandShoreDistanceMax);

        int islandsPlaced = 0;
        int stacksPlaced = 0;
        // Counted per rejection reason rather than summarised as "could not
        // place": four strict tests compound here, and the difference between
        // "there is no open sea left" and "the edge ring swallowed every site"
        // is the difference between two completely different knobs.
        int rejectLand = 0;
        int rejectShore = 0;
        int rejectEdge = 0;
        int rejectRelief = 0;
        int rejectSpacing = 0;
        for (int i = 0; i < islandCount + stackCount; i++)
        {
            // Islands first, so the big discs get first pick of the open water
            // and the stacks fill in around them rather than the reverse.
            bool seaStack = i >= islandCount;
            float radius = Math.Max(2f, seaStack ? cd.seaStackRadiusMeters : cd.islandRadiusMeters);

            bool found = false;
            for (int attempt = 0; attempt < ISLAND_PLACEMENT_ATTEMPTS && !found; attempt++)
            {
                int lx = rng.Next(sizeX);
                int lz = rng.Next(sizeZ);

                // Genuinely at sea, by a margin. Without it an "island" can be
                // seeded onto the mainland's own shelf, where the falloff just
                // fattens the coastline into a lumpy peninsula.
                if (coastBase[lx, lz] > -cd.islandSeaMargin) { rejectLand++; continue; }

                int toShore = shoreDistance[lx, lz];
                if (toShore < shoreMin || toShore > shoreMax) { rejectShore++; continue; }

                // Strictly inside the guaranteed ocean ring. That ring is the
                // promise that the world is bounded by water on all sides, and
                // an island is not allowed to eat into it — measured as the same
                // Chebyshev distance the ring itself is built from.
                float toEdge = Math.Min(
                    Math.Min(lx, sizeX - 1 - lx),
                    Math.Min(lz, sizeZ - 1 - lz));
                if (toEdge < edgeMargin + radius) { rejectEdge++; continue; }

                // A stack's height is chosen by picking its SITE, not by
                // clamping the result: the relief under the whole disc has to
                // sit inside the authored band. Below it, skipping the coastal
                // taper buys nothing and the stack comes out as flat as an
                // islet; above it, an untapered site is free to stand over the
                // tallest mountain in the world and take a chunk layer with it.
                if (seaStack && ReliefPeak(reliefH, lx, lz, radius) < cd.seaStackMinRelief)
                {
                    rejectRelief++;
                    continue;
                }

                bool clear = true;
                foreach (IslandDisc other in placed)
                {
                    float dx = other.Cx - lx;
                    float dz = other.Cz - lz;
                    if (dx * dx + dz * dz < spacing * spacing) { clear = false; break; }
                }
                if (!clear) { rejectSpacing++; continue; }

                var disc = new IslandDisc { Cx = lx, Cz = lz, Radius = radius, SeaStack = seaStack };
                StampIsland(disc, cd.islandMaskPeak, coastBase, stackTaper);
                placed.Add(disc);
                found = true;
                if (seaStack) { stacksPlaced++; } else { islandsPlaced++; }
            }
        }

        GD.Print($"[CellularTerrain] offshore: {islandsPlaced}/{islandCount} islands"
            + $" (r {cd.islandRadiusMeters}m), {stacksPlaced}/{stackCount} sea stacks"
            + $" (r {cd.seaStackRadiusMeters}m), shore band {shoreMin}-{shoreMax}v;"
            + $" samples rejected: {rejectLand} not at sea, {rejectShore} outside the shore band,"
            + $" {rejectEdge} inside the edge ring, {rejectRelief} too flat for a stack,"
            + $" {rejectSpacing} too close to another");
        return placed;
    }

    // Tallest relief anywhere under a disc. The MAX and not the centre sample:
    // relief is a ridged field, so a site whose centre is quiet can still have a
    // crest a few columns away, and it is the crest that decides how the stack
    // comes out.
    private static float ReliefPeak(float[,] reliefH, int cx, int cz, float radius)
    {
        int sizeX = reliefH.GetLength(0);
        int sizeZ = reliefH.GetLength(1);
        int r = Mathf.CeilToInt(radius);
        float peak = 0f;
        for (int lx = Math.Max(0, cx - r); lx <= Math.Min(sizeX - 1, cx + r); lx++)
        {
            for (int lz = Math.Max(0, cz - r); lz <= Math.Min(sizeZ - 1, cz + r); lz++)
            {
                int dx = lx - cx;
                int dz = lz - cz;
                if (dx * dx + dz * dz > radius * radius) { continue; }
                if (reliefH[lx, lz] > peak) { peak = reliefH[lx, lz]; }
            }
        }
        return peak;
    }

    // Pull one disc of the mask TOWARD a target value, hardest at the centre and
    // not at all at the rim. Toward a target rather than adding a fixed amount:
    // see CellularTerrainData.islandMaskPeak, where an additive version put every
    // island past the coastal taper band and cost the world a chunk layer.
    //
    // The falloff is SMOOTH rather than a step, which is what lets the existing
    // shoreBand build a real coast around the island — a hard rim would put the
    // whole shore transition inside one column and the island would meet the
    // water as a wall on every side.
    private static void StampIsland(IslandDisc disc, float peak, float[,] coastBase,
        float[,] stackTaper)
    {
        int sizeX = coastBase.GetLength(0);
        int sizeZ = coastBase.GetLength(1);
        int loX = Math.Max(0, Mathf.FloorToInt(disc.Cx - disc.Radius));
        int hiX = Math.Min(sizeX - 1, Mathf.CeilToInt(disc.Cx + disc.Radius));
        int loZ = Math.Max(0, Mathf.FloorToInt(disc.Cz - disc.Radius));
        int hiZ = Math.Min(sizeZ - 1, Mathf.CeilToInt(disc.Cz + disc.Radius));

        for (int lx = loX; lx <= hiX; lx++)
        {
            for (int lz = loZ; lz <= hiZ; lz++)
            {
                float dx = lx - disc.Cx;
                float dz = lz - disc.Cz;
                float t = Mathf.Sqrt(dx * dx + dz * dz) / disc.Radius;
                if (t >= 1f) { continue; }
                float falloff = 1f - Mathf.SmoothStep(0f, 1f, t);
                // Never LOWER the mask: two discs overlapping, or a disc whose
                // rim clips ground already above the target, must not carve sea
                // out of land that was already there.
                coastBase[lx, lz] = Math.Max(coastBase[lx, lz],
                    Mathf.Lerp(coastBase[lx, lz], peak, falloff));
                if (disc.SeaStack)
                {
                    // A sea stack keeps its full relief where the mainland
                    // coast would have had it tapered away, so it meets the
                    // water as a tall rock instead of an islet. Written as a
                    // weight rather than a flag so the skip fades out with the
                    // disc — the rim still gets a normal shore.
                    stackTaper[lx, lz] = Math.Max(stackTaper[lx, lz], falloff);
                }
            }
        }
    }

    // ------------------------------------------------------ placed landforms

    // What the landform pass changed, in the terms its two consumers need: the
    // cells no ramp may cut, and the named places worth registering.
    private sealed class LandformResult
    {
        public bool[] NoRamp;       // per cell — a cutting through it undoes it
        public bool[] IsLandform;   // per cell — claimed, and no-carve for caves
        public List<(string Name, int Cell)> Named = new();
        // Filled by ResolveLandformPois once the height field is final.
        public List<KeyValuePair<string, Vector3>> Pois = new();
    }

    // Pick N cells matching a rule, apply an effect, claim them. Same shape as
    // StampFlattenCells, and deliberately so.
    //
    // Every landform here obeys one shared invariant: it may not leave a wall
    // taller than maxCellStep. That is a playability rule rather than an
    // aesthetic one — past three storeys a cliff stops reading as something the
    // player can judge — and it is the reason each rule below tests its
    // neighbours BEFORE moving anything rather than clamping afterwards.
    //
    // PINNING is asymmetric on purpose, and the asymmetry is not obvious.
    // `Pinned` survives into BuildWaterways, where it means "no lake may flood
    // this and no breach may cut it". That is right for a mesa, which is a local
    // high point and can never be a sink. It is WRONG for anything pit-shaped: a
    // pinned quarry or crater is a sink the breach pass cannot drain, so the
    // fill never settles, the river above it dead-ends, and the log fills with
    // "breach stopped at pinned ground". Those are left unpinned, which lets the
    // water pass do the right thing with them on its own — flood one that
    // catches enough rain, notch the rim of one that does not.
    private LandformResult PlaceLandforms(CellPartition p, List<CellEdge> edges,
        CellularTerrainData cd, int worldMinX, int worldMinZ)
    {
        var result = new LandformResult
        {
            NoRamp = new bool[p.Count],
            IsLandform = new bool[p.Count],
        };

        // Adjacency, and the shared border with each neighbour. Built once —
        // every rule below is a statement about a cell and its neighbours.
        var adjacency = new List<int>[p.Count];
        for (int c = 0; c < p.Count; c++) { adjacency[c] = new List<int>(); }
        foreach (CellEdge e in edges)
        {
            adjacency[e.A].Add(e.B);
            adjacency[e.B].Add(e.A);
        }

        // Candidate order, hashed off each cell's own stable key rather than its
        // index. The index depends on the order BuildPartition happened to meet
        // columns, so ordering by it would move every landform whenever an
        // unrelated pass changed the scan; the key does not.
        var order = new List<int>(p.Count);
        for (int c = 0; c < p.Count; c++) { order.Add(c); }
        int landformSeed = WorldGen.DeriveSeed(_worldSeed, SEED_SALT_LANDFORM);
        order.Sort((a, b) =>
        {
            float ha = Hash01(p.Key[a], landformSeed);
            float hb = Hash01(p.Key[b], landformSeed);
            int cmp = ha.CompareTo(hb);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        // A cell is claimed once. Neighbours of a claimed cell are claimed too,
        // so two landforms never share a wall — abutting mesas read as one
        // lumpy massif rather than as two mesas.
        var claimed = new bool[p.Count];
        void Claim(int c)
        {
            claimed[c] = true;
            result.IsLandform[c] = true;
            result.NoRamp[c] = true;
            foreach (int n in adjacency[c]) { claimed[n] = true; }
        }

        // The world's existing ceiling. No landform may raise ground above it,
        // which is a budget rule rather than an aesthetic one: FitVerticalExtent
        // sizes the world to the finished height field, so one mesa lifted over
        // the tallest peak adds a whole chunk layer to every column in the world
        // — measured, +8 over a 16-voxel peak took the world from 4 chunks tall
        // to 5, a 25% rise in resident chunks for one landmark. Features are
        // meant to ADD to this terrain, not reshape it. Capping also puts mesas
        // in the middle of the elevation range, where a flat top stands out
        // against the ground around it instead of merging into the high country.
        float ceiling = 0f;
        for (int c = 0; c < p.Count; c++)
        {
            if (p.Flat[c] > ceiling) { ceiling = p.Flat[c]; }
        }

        float step = Math.Max(1, cd.quantizeStep);
        int mesas = PlaceMesas(p, adjacency, order, claimed, result, cd, step, ceiling, Claim);
        int quarries = PlaceQuarries(p, adjacency, order, claimed, result, cd, step, Claim);
        int craters = PlaceCraters(p, adjacency, order, claimed, result, cd, step, ceiling, Claim);
        int stairs = PlaceTerracedStairs(p, adjacency, order, claimed, result, cd, step, Claim);

        GD.Print($"[CellularTerrain] landforms (world ceiling {ceiling}v):"
            + $" {mesas}/{cd.mesaCount} mesas (+{cd.mesaRaise}v),"
            + $" {quarries}/{cd.quarryCount} quarries (-{cd.quarryDrop}v),"
            + $" {craters}/{cd.craterCount} craters (rim +{cd.craterRimRaise}v, floor -{cd.craterDepth}v),"
            + $" {stairs}/{cd.terraceStairCount} stair flights (<={cd.terraceStairCells} cells)");
        return result;
    }

    // Is this cell usable as the seed of a landform at all? Dry, not the
    // authored village, not already spoken for, and with enough neighbours to
    // have a shape worth reasoning about.
    private static bool IsLandformCandidate(CellPartition p, List<int>[] adjacency, bool[] claimed, int c)
    {
        return !claimed[c] && !p.Pinned[c] && p.Flat[c] >= 0f && adjacency[c].Count >= 2;
    }

    // Neighbour extremes, and whether every one of them is ordinary dry ground.
    // A landform reaching into the sea or into the village is the one case
    // where "raise it clear of its neighbours" produces nonsense.
    //
    // Tests IsLandform, NOT `claimed`. The two differ by exactly one ring:
    // `claimed` also covers every cell that merely NEIGHBOURS a landform, which
    // is the right bar for choosing a seed (two landforms sharing a wall read as
    // one lumpy massif) and much too strict for the walls of one. Using
    // `claimed` here meant the first landform placed sterilised two rings of the
    // graph around itself, and with six of them going down in sequence the later
    // rules had nothing legal left — measured, it is why no quarry was ever
    // placed in a world with three mesas in it.
    private static bool NeighbourBand(CellPartition p, List<int>[] adjacency, LandformResult result,
        int c, out float lo, out float hi)
    {
        lo = float.MaxValue;
        hi = float.MinValue;
        foreach (int n in adjacency[c])
        {
            if (p.Pinned[n] || result.IsLandform[n] || p.Flat[n] < 0f) { return false; }
            if (p.Flat[n] < lo) { lo = p.Flat[n]; }
            if (p.Flat[n] > hi) { hi = p.Flat[n]; }
        }
        return lo <= hi;
    }

    // MESA — a cell already standing at or above all its neighbours, lifted
    // clear of them and denied a ramp. Measured from the TALLEST neighbour so
    // the shortest wall is exactly the authored raise; the spread test bounds
    // how much taller the others can be, which is what keeps the whole thing
    // inside maxCellStep.
    private static int PlaceMesas(CellPartition p, List<int>[] adjacency, List<int> order,
        bool[] claimed, LandformResult result, CellularTerrainData cd, float step, float ceiling,
        Action<int> claim)
    {
        int raise = Mathf.RoundToInt(Math.Max(step, cd.mesaRaise) / step) * (int)step;
        int placed = 0;
        foreach (int c in order)
        {
            if (placed >= cd.mesaCount) { break; }
            if (!IsLandformCandidate(p, adjacency, claimed, c)) { continue; }
            if (!NeighbourBand(p, adjacency, result, c, out float lo, out float hi)) { continue; }
            if (p.Flat[c] < hi) { continue; }
            if (hi - lo > cd.landformNeighbourSpread) { continue; }

            float top = hi + raise;
            if (top > ceiling) { continue; }
            if (top - lo > cd.maxCellStep) { continue; }

            p.Flat[c] = top;
            // Pinned is safe here and only here: a mesa is a local maximum, so
            // the water pass can never want to flood it or breach through it.
            p.Pinned[c] = true;
            claim(c);
            result.Named.Add(($"mesa_{placed}", c));
            placed++;
        }
        return placed;
    }

    // QUARRY — the mesa rule inverted, but asking for LEVEL COUNTRY rather than
    // for a hollow, and that difference is load-bearing rather than a tuning
    // convenience. The strict inversion — a cell at or below every neighbour —
    // placed nothing at all, and the counters say why: of 290 cells, 150 are
    // seabed, 83 touch the sea, and 55 of the remaining 57 have some neighbour
    // lower than they are. That is not an accident of tuning, it is what ridged
    // relief does — it only ever ADDS to a base level, so peaks are everywhere
    // and true basins are almost nowhere.
    //
    // Asking instead that the cell and all its neighbours sit inside one band is
    // the honest rule anyway: a mesa is FOUND, a quarry is DUG, and digging into
    // level ground is exactly what a quarry is.
    //
    // The floor is kept at or above the waterline: below it the column fills
    // with SEA water even well inland, which is neither a quarry nor a lake.
    private static int PlaceQuarries(CellPartition p, List<int>[] adjacency, List<int> order,
        bool[] claimed, LandformResult result, CellularTerrainData cd, float step, Action<int> claim)
    {
        int drop = Mathf.RoundToInt(Math.Max(step, cd.quarryDrop) / step) * (int)step;
        int placed = 0;
        // Counted per reason. Four tests compound, and "no cell is a local
        // minimum" and "every local minimum is too near the waterline to drop"
        // want opposite fixes.
        int rejectSeed = 0;
        int rejectBand = 0;
        int rejectNotLevel = 0;
        int rejectSpread = 0;
        int rejectWaterline = 0;
        foreach (int c in order)
        {
            if (placed >= cd.quarryCount) { break; }
            if (!IsLandformCandidate(p, adjacency, claimed, c)) { rejectSeed++; continue; }
            if (!NeighbourBand(p, adjacency, result, c, out float lo, out float hi)) { rejectBand++; continue; }
            if (hi - lo > cd.landformNeighbourSpread) { rejectSpread++; continue; }
            // The cell itself inside the same band as its neighbours — level
            // country, not a shoulder with the ground falling away on one side.
            if (p.Flat[c] > hi || p.Flat[c] < lo - cd.landformNeighbourSpread)
            {
                rejectNotLevel++;
                continue;
            }

            float floor = Math.Min(p.Flat[c], lo) - drop;
            if (floor < 0f || hi - floor > cd.maxCellStep) { rejectWaterline++; continue; }

            p.Flat[c] = floor;
            // Deliberately NOT pinned — see PlaceLandforms. A quarry is a sink,
            // and the water pass has to stay free to flood it or notch its rim.
            claim(c);
            result.Named.Add(($"quarry_{placed}", c));
            placed++;
        }
        if (placed < cd.quarryCount)
        {
            GD.Print($"[CellularTerrain]   quarries short by {cd.quarryCount - placed}; cells rejected:"
                + $" {rejectSeed} unusable as a seed, {rejectBand} with a wet/landform neighbour,"
                + $" {rejectNotLevel} not level with their neighbours, {rejectSpread} with neighbours"
                + $" spread over {cd.landformNeighbourSpread}v, {rejectWaterline} too near the"
                + $" waterline to drop {drop}v");
        }
        return placed;
    }

    // CRATER — a ring of cells raised around a dropped floor. The one landform
    // that genuinely needs the cell GRAPH rather than a single cell, and the
    // reason the rules here are expressed over neighbours at all.
    //
    // The rim is levelled to ONE height before it is raised, so it reads as a
    // single lip; leaving each ring cell at its own level plus the raise gives a
    // ragged edge that reads as hills that happen to surround a hollow.
    private static int PlaceCraters(CellPartition p, List<int>[] adjacency, List<int> order,
        bool[] claimed, LandformResult result, CellularTerrainData cd, float step, float ceiling,
        Action<int> claim)
    {
        int rimRaise = Mathf.RoundToInt(Math.Max(step, cd.craterRimRaise) / step) * (int)step;
        int depth = Mathf.RoundToInt(Math.Max(step, cd.craterDepth) / step) * (int)step;
        int placed = 0;
        foreach (int c in order)
        {
            if (placed >= cd.craterCount) { break; }
            if (!IsLandformCandidate(p, adjacency, claimed, c)) { continue; }
            if (adjacency[c].Count < 3) { continue; }
            if (!NeighbourBand(p, adjacency, result, c, out float lo, out float hi)) { continue; }
            if (hi - lo > cd.landformNeighbourSpread) { continue; }

            float rim = hi + rimRaise;
            float floor = rim - depth;
            if (rim > ceiling) { continue; }
            if (floor < 0f || floor >= p.Flat[c]) { continue; }

            // The rim's OUTWARD walls. Raising the ring makes a wall against
            // everything outside it too, and those cells were never examined by
            // the spread test — without this a crater on a shoulder of high
            // ground leaves a 20-voxel face on its downhill side.
            bool ok = true;
            foreach (int r in adjacency[c])
            {
                foreach (int o in adjacency[r])
                {
                    if (o == c || result.IsLandform[o] || p.Pinned[o]) { continue; }
                    if (adjacency[c].Contains(o)) { continue; }
                    if (p.Flat[o] < 0f || rim - p.Flat[o] > cd.maxCellStep) { ok = false; break; }
                }
                if (!ok) { break; }
            }
            if (!ok) { continue; }

            foreach (int r in adjacency[c])
            {
                p.Flat[r] = rim;
                result.NoRamp[r] = true;
                result.IsLandform[r] = true;
            }
            p.Flat[c] = floor;
            // Unpinned floor AND unpinned rim: a crater is a sink, and the fill
            // has to be able to breach it. A pinned rim is a sink with no
            // outlet, which never settles.
            foreach (int r in adjacency[c]) { claim(r); }
            claim(c);
            result.Named.Add(($"crater_{placed}", c));
            placed++;
        }
        return placed;
    }

    // TERRACED STAIRS — a CHAIN of adjacent cells each set exactly one
    // quantizeStep below the last, walked through the cell graph. The only
    // landform here that is a route rather than a place: a 2-voxel lip is a step
    // the player takes unaided, so a flight of them climbs ground that would
    // otherwise need a ramp cut through it.
    //
    // Staged and only committed once the chain is long enough to read as a
    // flight. A two-cell "flight" is just a wall with an extra terrace at the
    // bottom, and abandoning one halfway would leave exactly that.
    private static int PlaceTerracedStairs(CellPartition p, List<int>[] adjacency, List<int> order,
        bool[] claimed, LandformResult result, CellularTerrainData cd, float step, Action<int> claim)
    {
        const int MinFlightCells = 3;
        int placed = 0;
        var chain = new List<int>();
        var levels = new List<float>();
        foreach (int c in order)
        {
            if (placed >= cd.terraceStairCount) { break; }
            if (!IsLandformCandidate(p, adjacency, claimed, c)) { continue; }

            chain.Clear();
            levels.Clear();
            chain.Add(c);
            levels.Add(p.Flat[c]);
            var inChain = new HashSet<int> { c };
            float level = p.Flat[c];

            while (chain.Count < cd.terraceStairCells)
            {
                float target = level - step;
                if (target < 0f) { break; }

                // The neighbour already closest to the next tread. Choosing the
                // nearest keeps the flight following ground that was already
                // descending, so the stair is cut into a slope rather than
                // stamped across level country.
                int best = -1;
                float bestDelta = float.MaxValue;
                foreach (int n in adjacency[chain[chain.Count - 1]])
                {
                    if (inChain.Contains(n) || claimed[n] || p.Pinned[n] || p.Flat[n] < 0f) { continue; }
                    float delta = Math.Abs(p.Flat[n] - target);
                    if (delta > cd.maxCellStep) { continue; }
                    if (delta < bestDelta) { bestDelta = delta; best = n; }
                }
                if (best < 0) { break; }

                // The tread's walls against everything OFF the flight. A step
                // set to the lattice is free to be 20 voxels above the cell
                // beside it if that cell was never part of the chain.
                bool ok = true;
                foreach (int o in adjacency[best])
                {
                    if (inChain.Contains(o)) { continue; }
                    if (p.Flat[o] < 0f || Math.Abs(target - p.Flat[o]) > cd.maxCellStep) { ok = false; break; }
                }
                if (!ok) { break; }

                chain.Add(best);
                levels.Add(target);
                inChain.Add(best);
                level = target;
            }

            if (chain.Count < MinFlightCells) { continue; }
            for (int i = 0; i < chain.Count; i++)
            {
                p.Flat[chain[i]] = levels[i];
                claim(chain[i]);
            }
            result.Named.Add(($"stair_{placed}", chain[0]));
            placed++;
        }
        return placed;
    }

    // Columns no ramp may cut, projected from the cells the landform pass
    // claimed. Consumed by CutRamps, which is the only place the test can be
    // exact: a ramp's length is not known until CutRamps has grown it against
    // the final field, and testing the longest it COULD become instead threw
    // away half the network (measured: 33 of 63 ramps, against six landforms).
    private static bool[,] BuildNoRampColumns(CellPartition p, LandformResult landforms)
    {
        int sizeX = p.Index.GetLength(0);
        int sizeZ = p.Index.GetLength(1);
        var mask = new bool[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                mask[lx, lz] = landforms.NoRamp[p.Index[lx, lz]];
            }
        }
        return mask;
    }

    // Resolve each named landform to a real column and register it for
    // WorldState.PointsOfInterest. The CENTROID is not usable directly — a
    // cell's shape is arbitrary and a concave one puts its centroid outside
    // itself — so this takes the cell's own column nearest to it.
    private static void ResolveLandformPois(LandformResult landforms, CellPartition p,
        int[,] height, int worldMinX, int worldMinZ)
    {
        if (landforms.Named.Count == 0) { return; }
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);

        var bestX = new int[landforms.Named.Count];
        var bestZ = new int[landforms.Named.Count];
        var bestD = new float[landforms.Named.Count];
        for (int i = 0; i < bestD.Length; i++) { bestD[i] = float.MaxValue; bestX[i] = -1; }

        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                int cell = p.Index[lx, lz];
                for (int i = 0; i < landforms.Named.Count; i++)
                {
                    if (landforms.Named[i].Cell != cell) { continue; }
                    float dx = lx + worldMinX - p.CentroidX[cell];
                    float dz = lz + worldMinZ - p.CentroidZ[cell];
                    float d = dx * dx + dz * dz;
                    if (d < bestD[i]) { bestD[i] = d; bestX[i] = lx; bestZ[i] = lz; }
                }
            }
        }

        for (int i = 0; i < landforms.Named.Count; i++)
        {
            if (bestX[i] < 0) { continue; }
            landforms.Pois.Add(new KeyValuePair<string, Vector3>(landforms.Named[i].Name, new Vector3(
                bestX[i] + worldMinX + 0.5f,
                height[bestX[i], bestZ[i]] + 1f,
                bestZ[i] + worldMinZ + 0.5f)));
        }
    }

    // --------------------------------------------------------- cliff erosion

    // Erode the outermost metre or two of the world's tall cliffs.
    //
    // Every cell border here is ONE vertical drop the whole way along it, so a
    // tall wall is a single flat plane — and the tallest of them, the coastal
    // ones falling from the land to the sea floor, are the largest bare surfaces
    // in the world. This bites a shallow, STEPPED band back from the lip along
    // part of each face.
    //
    // The point is as much what it LEAVES as what it takes. The mask is a smooth
    // low-frequency field rather than a per-column roll, so bites come in runs
    // and vary in depth within a run; the stretches it skips are untouched lip
    // standing proud between them. The bitten runs read as eroded gullies and
    // small terraces, and the runs left alone as the fingers and ridges between.
    // A per-column chance instead gives a serrated edge that reads as noise on a
    // wall which is, overall, still one plane.
    //
    // A direct per-column write for the same reason land bridges are: expressed
    // as cells, MergeSlivers would fold a three-column bite away and
    // RelaxCellWalls would level it back into the terrace it came from. It keeps
    // every invariant it touches — a bite is a whole number of quantizeSteps
    // down, so the lattice holds — and it refuses to touch ramps (the only
    // non-lattice ground), the authored village, or a landform.
    private int ErodeCliffFaces(int[,] height, int[,] plateau, float[,] ramp, CellPartition p,
        LandformResult landforms, bool[,] noErode, CellularTerrainData cd,
        int worldMinX, int worldMinZ)
    {
        if (cd.cliffErosionMinDrop <= 0 || cd.cliffErosionAmount <= 0f) { return 0; }
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int step = Math.Max(1, cd.quantizeStep);

        // THREE independent fields, because the three decisions are independent
        // and sharing one would correlate them: every cut would be the deepest
        // one, every deep cut would also be a ledge, and every ledge would sit at
        // the same height — a pattern rather than erosion.
        var cutNoise = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_CLIFF_EROSION),
            cd.cliffErosionFrequency, 2);
        var modeNoise = WorldGen.MakePerlin(
            WorldGen.DeriveSeed(_worldSeed, SEED_SALT_CLIFF_EROSION + 2), cd.cliffModeFrequency, 2);
        var stepNoise = WorldGen.MakePerlin(
            WorldGen.DeriveSeed(_worldSeed, SEED_SALT_CLIFF_EROSION + 3), cd.cliffStepFrequency, 2);

        // Chosen against a SNAPSHOT of the heights. Reading the live field would
        // let a cut at one column change whether its neighbour still looks like
        // a cliff lip, so the band would creep inward one column per row and eat
        // the terrace behind it.
        var before = (int[,])height.Clone();
        var target = new int[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++) { target[lx, lz] = int.MinValue; }
        }

        int lips = 0;
        int ledged = 0;
        int retreated = 0;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                if (!ErodibleGround(lx, lz, p, landforms, ramp)) { continue; }
                if (noErode[lx, lz]) { continue; }
                int top = before[lx, lz];

                // The lip, and which way the ground falls away from it.
                int lowest = int.MaxValue;
                int outX = 0;
                int outZ = 0;
                for (int d = 0; d < 4; d++)
                {
                    int nx = lx + NEIGHBOUR_DX[d];
                    int nz = lz + NEIGHBOUR_DZ[d];
                    if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                    if (before[nx, nz] >= lowest) { continue; }
                    // A RAMP beside a cliff is a cutting, not the cliff's base:
                    // its columns are the world's only off-lattice ground, and
                    // taking one as the base put a pure cut off the lattice too.
                    // Three columns in one run, which the counter below caught.
                    if (!float.IsNaN(ramp[nx, nz])) { continue; }
                    lowest = before[nx, nz];
                    outX = NEIGHBOUR_DX[d];
                    outZ = NEIGHBOUR_DZ[d];
                }
                if (lowest == int.MaxValue) { continue; }

                int drop = top - lowest;
                if (drop < cd.cliffErosionMinDrop) { continue; }
                lips++;

                // Is this lip cut at all? The ones that are not are what leave
                // the cliff its FINGERS, and the un-cut stretches standing proud
                // between the cut ones are as much the effect as the cuts.
                float amount = Math.Clamp(cd.cliffErosionAmount
                    + (lowest < WorldGen.WATER_LEVEL ? cd.cliffErosionCoastalBoost : 0f), 0f, 1f);
                float n = Noise01(cutNoise, lx + worldMinX, lz + worldMinZ);
                if (n > amount) { continue; }
                // How far under the threshold this lip sits, 0..1 — deeper
                // erosion where the field is stronger, which is what keeps the
                // cut depth varying along a face instead of alternating.
                float bite = amount > 0f ? 1f - n / amount : 1f;

                // TWO shapes, and no others. There is deliberately no sloped
                // shape: a slope across a cut this narrow resolves to a run of
                // 1-voxel steps, which read as near-invisible ledges rather than
                // as a grade — the exact thing they were meant to avoid.
                //
                //   PURE CUT — the band drops straight to the cliff base, so the
                //              edge retreats in plan and the wall keeps its whole
                //              height. NO horizontal surface is introduced.
                //   LEDGE    — the band drops to one flat height, kept at least
                //              cliffLedgeTopClearance below the top so the cliff
                //              keeps a real face above it.
                //
                // A wall too short to hold a ledge under that clearance can only
                // be cut, which is also the right answer for it: any horizontal
                // surface part-way down a 4-voxel wall is a ledge to climb, and a
                // wall that short exists to stop the player.
                int highestLedge = top - Math.Max(0, cd.cliffLedgeTopClearance);
                int lowestLedge = lowest + step;
                bool ledge = highestLedge >= lowestLedge
                    && drop >= cd.cliffShapedMinDrop
                    && Noise01(modeNoise, lx + worldMinX, lz + worldMinZ)
                        < Math.Clamp(cd.cliffLedgeShare, 0f, 1f);

                int cutBack;
                int cutY;
                if (ledge)
                {
                    cutBack = Mathf.RoundToInt(Mathf.Lerp(
                        cd.cliffLedgeWidthMin, cd.cliffLedgeWidthMax, bite));
                    // Which lattice level the ledge sits at, rolled over the
                    // band between the base and the top clearance — so a tall
                    // cliff has real choice and a short one has exactly one.
                    int levels = (highestLedge - lowestLedge) / step + 1;
                    int pick = Math.Clamp(
                        Mathf.FloorToInt(Noise01(stepNoise, lx + worldMinX, lz + worldMinZ) * levels),
                        0, levels - 1);
                    cutY = lowestLedge + pick * step;
                    ledged++;
                }
                else
                {
                    cutBack = Mathf.RoundToInt(Mathf.Lerp(
                        cd.cliffCutDepthMin, cd.cliffCutDepthMax, bite));
                    cutY = lowest;
                    retreated++;
                }
                cutBack = Math.Max(1, cutBack);

                for (int i = 0; i < cutBack; i++)
                {
                    int nx = lx - outX * i;
                    int nz = lz - outZ * i;
                    if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { break; }
                    // Stop at the back of the terrace: this erodes THIS face, not
                    // a trench across whatever is behind it.
                    if (before[nx, nz] != top) { break; }
                    if (!ErodibleGround(nx, nz, p, landforms, ramp)) { break; }
                    // A reserved cave mouth stops the cut dead rather than being
                    // skipped over: cutting past it would leave the reserved
                    // columns as an island of un-eroded lip.
                    if (noErode[nx, nz]) { break; }
                    if (cutY >= top) { continue; }
                    // Where two lips reach the same column the SHALLOWER cut
                    // wins, so a narrow spur between two faces is not eroded
                    // twice into a notch.
                    if (target[nx, nz] == int.MinValue || cutY > target[nx, nz])
                    {
                        target[nx, nz] = cutY;
                    }
                }
            }
        }

        int bitten = 0;
        int tooTall = 0;
        int offLattice = 0;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                int cut = target[lx, lz];
                if (cut == int.MinValue || cut >= height[lx, lz]) { continue; }

                // Lowering a lip DEEPENS the wall from anything standing above
                // it, and that wall was inside maxCellStep before the cut.
                // Measured: without this the world grew a handful of 16-voxel
                // faces where a cut was taken at the foot of a terrace already a
                // full step up. Neighbours are read at their FINAL height, since
                // one of them may be cut too.
                bool ok = true;
                for (int d = 0; d < 4 && ok; d++)
                {
                    int nx = lx + NEIGHBOUR_DX[d];
                    int nz = lz + NEIGHBOUR_DZ[d];
                    if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                    int nh = target[nx, nz] != int.MinValue ? target[nx, nz] : before[nx, nz];
                    ok = nh - cut <= cd.maxCellStep;
                }
                if (!ok) { tooTall++; continue; }

                height[lx, lz] = cut;
                // Both shapes leave FLAT ground — a ledge is a flat position and
                // a pure cut lands on the terrace below — so both read as flat
                // and the scatter dresses them. Nothing this pass writes is off
                // the quantizeStep lattice any more, which the counter below
                // asserts rather than assumes.
                plateau[lx, lz] = cut;
                if (cut % step != 0) { offLattice++; }
                bitten++;
            }
        }
        GD.Print($"[CellularTerrain] cliff erosion: {bitten} columns cut off {lips} cliff-lip"
            + $" columns taller than {cd.cliffErosionMinDrop}v — {retreated} lips cut straight"
            + $" back {cd.cliffCutDepthMin}-{cd.cliffCutDepthMax}m, {ledged} ledged"
            + $" {cd.cliffLedgeWidthMin}-{cd.cliffLedgeWidthMax}m wide at least"
            + $" {cd.cliffLedgeTopClearance}v below the top, the rest left as fingers"
            + $" (amount {cd.cliffErosionAmount:F2} +{cd.cliffErosionCoastalBoost:F2} on sea"
            + $" cliffs, ledge share {cd.cliffLedgeShare:F2}); {offLattice} columns off the"
            + $" {step}v lattice (must be 0); {tooTall} refused for leaving a wall over"
            + $" {cd.maxCellStep}v");
        return bitten;
    }


    // Perlin remapped to [0,1]. The three erosion fields all want a unit roll
    // rather than a signed one, and doing it in three places invited one of them
    // to be written -1..1 by accident.
    private static float Noise01(FastNoiseLite noise, int wx, int wz)
    {
        return 0.5f * (noise.GetNoise2D(wx, wz) + 1f);
    }


    // Scatter rubble at the foot of the eroded faces: single columns raised ONE
    // quantizeStep, in a broken apron rather than a continuous bank. This is
    // where the bitten material went, and it is the other half of making a cliff
    // read as eroded — a clean line where a face meets flat ground reads as cut
    // stone however much the top of it has been nibbled.
    //
    // Runs AFTER the water pass and skips any column carrying a river or a lake.
    // The water was routed over the ground as it stood; a 2-voxel block dropped
    // into a channel afterwards would dam it, and nothing downstream re-runs the
    // fill to notice.
    //
    // One step, not one voxel: the lattice invariant says non-ramp land sits on
    // multiples of quantizeStep, and a 1-voxel block would be the only ground in
    // the world that does not.
    private int ScatterTalus(int[,] height, int[,] plateau, int[,] water, float[,] ramp,
        CellPartition p, LandformResult landforms, CellularTerrainData cd,
        int worldMinX, int worldMinZ)
    {
        if (cd.cliffTalusCoverage <= 0f) { return 0; }
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int step = Math.Max(1, cd.quantizeStep);
        var mask = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_CLIFF_EROSION + 1),
            cd.cliffTalusFrequency, 2);

        var before = (int[,])height.Clone();
        int placed = 0;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                if (!ErodibleGround(lx, lz, p, landforms, ramp)) { continue; }
                if (water != null && water[lx, lz] != HeightMap.NoWater) { continue; }
                if (before[lx, lz] < WorldGen.WATER_LEVEL) { continue; }

                // At the foot of a real face: something beside it stands at
                // least the erosion threshold above.
                int tallest = before[lx, lz];
                for (int d = 0; d < 4; d++)
                {
                    int nx = lx + NEIGHBOUR_DX[d];
                    int nz = lz + NEIGHBOUR_DZ[d];
                    if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                    if (before[nx, nz] > tallest) { tallest = before[nx, nz]; }
                }
                if (tallest - before[lx, lz] < cd.cliffErosionMinDrop) { continue; }

                float n = 0.5f * (mask.GetNoise2D(lx + worldMinX, lz + worldMinZ) + 1f);
                if (n > Math.Clamp(cd.cliffTalusCoverage, 0f, 1f)) { continue; }
                // Never up to or past the face it sits under — rubble is debris
                // at the bottom, not a step halfway up.
                if (before[lx, lz] + step >= tallest) { continue; }

                height[lx, lz] = before[lx, lz] + step;
                plateau[lx, lz] = height[lx, lz];
                placed++;
            }
        }
        GD.Print($"[CellularTerrain] talus: {placed} rubble columns raised {step}v at the foot of"
            + $" faces over {cd.cliffErosionMinDrop}v (coverage {cd.cliffTalusCoverage:F2})");
        return placed;
    }

    // Ground erosion may touch: ordinary terrain on the lattice. Ramps are the
    // world's only sloped ground and the only columns off the lattice, so
    // eroding one both breaks the invariant and puts a step through the middle
    // of a route; the village and the landforms are authored shapes that erosion
    // would eat into.
    private static bool ErodibleGround(int lx, int lz, CellPartition p,
        LandformResult landforms, float[,] ramp)
    {
        if (!float.IsNaN(ramp[lx, lz])) { return false; }
        int cell = p.Index[lx, lz];
        return !p.Pinned[cell] && !landforms.IsLandform[cell];
    }

    // ------------------------------------------------------------ land bridges

    // One bridge deck: a straight ribbon between two abutments, arched over the
    // gap, with the air under it carved out. Kept on the generator so the carve
    // stays derivable from data written before chunk fill ever starts.
    private struct BridgeRibbon
    {
        public float X0, Z0;    // world column coords of the two abutments
        public float X1, Z1;
        public float HalfWidth;
        public int DeckY;       // the abutment level the arch is measured from
        public int[] Deck;      // deck level per column along the span
        public bool[] Sloped;   // that column is mid-grade, not a flat tread
    }

    // Attempts made per bridge. Same reasoning as the island count: the pair
    // rule is strict, so most samples are rejected.
    private const int BRIDGE_PLACEMENT_ATTEMPTS = 4000;

    // How far the abutment walk may travel from a centroid before giving up on
    // a pair. It only bounds the search: a gap further out than this is one some
    // other pair of cells is better placed to bridge.
    private const int BRIDGE_ABUTMENT_SEARCH_COLUMNS = 128;

    // A land bridge is NOT a cell feature, and the two reasons are the whole
    // design. MergeSlivers folds any cell under minCellColumns into its
    // longest-bordered neighbour, so a ribbon narrow enough to read as a bridge
    // would simply be eaten; and RelaxCellWalls lowers any cell standing more
    // than maxCellStep above a neighbour, which is exactly what a deck over a
    // gap does, so the relaxation would drag the deck down into the gap it
    // spans. Written instead as a direct per-column height write after both
    // passes have run — the same ordering trick CutRamps already depends on.
    //
    // The heightfield is single-valued per column, so only the DECK can live in
    // Height; the air under it has to come from the carve. What that buys for
    // free is worth knowing: DeriveSurface re-derives Surface from the finished
    // voxels, so the deck becomes the surface and every placement pass puts its
    // props and mobs on TOP of the bridge rather than in the void underneath.
    //
    // Two cell CENTROIDS pick a candidate; they are not the bridge. The segment
    // between them is trimmed to the gap it crosses before anything is measured
    // or stamped — see TrimToGap for why a length limit on the raw centroids is
    // the wrong measurement.
    private List<BridgeRibbon> BuildLandBridges(CellPartition p, int[,] height, int[,] plateau,
        int[,] water, LandformResult landforms, CellularTerrainData cd, int worldMinX, int worldMinZ)
    {
        var ribbons = new List<BridgeRibbon>();
        if (cd.landBridgeCount <= 0) { return ribbons; }

        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        float halfWidth = Math.Max(1f, cd.bridgeWidthMeters * 0.5f);
        float spanMin = Math.Min(cd.bridgeSpanMin, cd.bridgeSpanMax);
        float spanMax = Math.Max(cd.bridgeSpanMin, cd.bridgeSpanMax);
        float step = Math.Max(1, cd.quantizeStep);

        // Cells that can hold an abutment: dry, unclaimed, not the village.
        var ends = new List<int>();
        for (int c = 0; c < p.Count; c++)
        {
            if (p.Pinned[c] || landforms.IsLandform[c] || p.Flat[c] < 0f) { continue; }
            ends.Add(c);
        }
        if (ends.Count < 2) { return ribbons; }

        // Adjacency, so a "bridge" is never built between two cells that already
        // share a border — that is a wall, not a gap.
        var neighbours = new HashSet<long>();
        foreach (CellEdge e in BuildCellGraph(p, worldMinX, worldMinZ))
        {
            neighbours.Add(((long)e.A << 32) | (uint)e.B);
        }
        bool Adjacent(int a, int b)
        {
            int lo = Math.Min(a, b);
            int hi = Math.Max(a, b);
            return neighbours.Contains(((long)lo << 32) | (uint)hi);
        }

        var rng = new Random(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_LANDFORM + 1));
        var used = new HashSet<int>();
        int placed = 0;
        int rejectedGap = 0;
        int rejectedBlocked = 0;
        int rejectedSpan = 0;

        for (int attempt = 0; attempt < BRIDGE_PLACEMENT_ATTEMPTS && placed < cd.landBridgeCount; attempt++)
        {
            int a = ends[rng.Next(ends.Count)];
            int b = ends[rng.Next(ends.Count)];
            if (a == b || used.Contains(a) || used.Contains(b)) { continue; }
            if (Adjacent(a, b)) { continue; }

            // Within one step of the same level: the arch is measured from ONE
            // abutment level, so two ends at genuinely different heights would
            // need a deck that climbs as well as arches.
            if (Math.Abs(p.Flat[a] - p.Flat[b]) > step) { continue; }

            float ax = p.CentroidX[a];
            float az = p.CentroidZ[a];
            float bx = p.CentroidX[b];
            float bz = p.CentroidZ[b];
            float centroidSpan = Mathf.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));

            // The centroids bound the gap between them, so a pair closer than
            // the minimum span cannot hold one, and a pair further apart than
            // the walk can cover from both ends cannot be trimmed to one.
            if (centroidSpan < spanMin
                || centroidSpan > spanMax + 2 * BRIDGE_ABUTMENT_SEARCH_COLUMNS) { continue; }

            int deckY = WorldGen.WATER_LEVEL + Mathf.RoundToInt(Math.Max(p.Flat[a], p.Flat[b]));
            if (!TrimToGap(ax, az, bx, bz, deckY, height, cd, worldMinX, worldMinZ,
                    out float x0, out float z0, out float x1, out float z1))
            {
                rejectedGap++;
                continue;
            }

            float span = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
            if (span < spanMin || span > spanMax) { rejectedSpan++; continue; }

            int[] deck = BuildArchProfile(deckY, Mathf.CeilToInt(span) + 1, rng, cd,
                out bool[] sloped);
            var ribbon = new BridgeRibbon
            {
                X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, HalfWidth = halfWidth, DeckY = deckY,
                Deck = deck, Sloped = sloped,
            };

            if (!RibbonSpansAGap(ribbon, height, water, p, landforms, cd, worldMinX, worldMinZ,
                    out bool blocked))
            {
                if (blocked) { rejectedBlocked++; } else { rejectedGap++; }
                continue;
            }

            ribbons.Add(ribbon);
            used.Add(a);
            used.Add(b);
            placed++;
        }

        // Stamp the decks. Done after every ribbon is chosen so two crossing
        // ribbons cannot validate against each other's half-written ground.
        //
        // The ground each column stood at is banked as the deck goes down: the
        // carve under the bridge needs it, and once Height holds the deck there
        // is no way left to recover it.
        _bridgeGroundUnder = new int[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++) { _bridgeGroundUnder[lx, lz] = int.MinValue; }
        }
        foreach (BridgeRibbon r in ribbons)
        {
            ForEachRibbonColumn(r, sizeX, sizeZ, worldMinX, worldMinZ, (lx, lz, i) =>
            {
                int deckY = r.Deck[i];
                if (height[lx, lz] >= deckY) { return; }
                if (_bridgeGroundUnder[lx, lz] == int.MinValue)
                {
                    _bridgeGroundUnder[lx, lz] = height[lx, lz];
                }
                height[lx, lz] = deckY;
                // Same rule ramps follow: Plateau under Height marks ground that
                // is not flat, which is what keeps scatter off the graded part
                // of the arch. Treads are flat ground and read as such.
                plateau[lx, lz] = r.Sloped[i] ? deckY - 1 : deckY;
            });
        }

        // An arch is only as good as its mix, and this is where a bad one shows:
        // no graded risers means no tread ever had room to spread (the spans are
        // too short, or the grade too steep for them), all graded means the arch
        // has no crisp step left in it.
        int stepped = 0;
        int graded = 0;
        foreach (BridgeRibbon r in ribbons)
        {
            for (int i = 1; i < r.Deck.Length; i++)
            {
                if (Math.Abs(r.Deck[i] - r.Deck[i - 1]) >= step) { stepped++; }
            }
            bool previous = false;
            foreach (bool s in r.Sloped)
            {
                if (s && !previous) { graded++; }
                previous = s;
            }
        }
        GD.Print($"[CellularTerrain] bridge deck risers: {stepped} stepped, {graded} graded"
            + $" over {ribbons.Count} decks");

        GD.Print($"[CellularTerrain] land bridges: {placed}/{cd.landBridgeCount} placed"
            + $" (gap span {spanMin}-{spanMax}v, deck {cd.bridgeWidthMeters}m wide,"
            + $" {cd.bridgeThickness}v thick, arch {cd.bridgeArchGrade:F2} of span);"
            + $" rejected {rejectedGap} with no gap under them, {rejectedSpan} with a gap"
            + $" outside the span limits, {rejectedBlocked} blocked by ground, water"
            + " or a landform");
        return ribbons;
    }

    // Does this ribbon actually span something? Requires a real gap under the
    // middle of it, and refuses anything the deck cannot simply sit over.
    //
    // Inland WATER is a refusal rather than a special case. A deck stamped into
    // Height over a column carrying a river surface leaves Water below Height,
    // which breaks the invariant every consumer of that channel relies on — the
    // shore-kit bands would sand the deck, the scatter would call it submerged,
    // and roads would refuse to cross it. The SEA is fine and left alone: sea
    // columns carry NoWater, the global waterline covers them, and a bridge over
    // open water between two coastal cells is exactly the shape wanted.
    private static bool RibbonSpansAGap(BridgeRibbon r, int[,] height, int[,] water, CellPartition p,
        LandformResult landforms, CellularTerrainData cd, int worldMinX, int worldMinZ,
        out bool blocked)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int gapColumns = 0;
        int columns = 0;
        bool bad = false;

        ForEachRibbonColumn(r, sizeX, sizeZ, worldMinX, worldMinZ, (lx, lz, i) =>
        {
            columns++;
            if (height[lx, lz] > r.Deck[i]) { bad = true; return; }
            if (water != null && water[lx, lz] != HeightMap.NoWater) { bad = true; return; }
            int cell = p.Index[lx, lz];
            if (p.Pinned[cell] || landforms.IsLandform[cell]) { bad = true; return; }
            if (r.DeckY - height[lx, lz] >= cd.bridgeGapDepth) { gapColumns++; }
        });

        blocked = bad;
        if (bad || columns == 0) { return false; }

        // At least a third of the ribbon has to be over open air, or the "gap"
        // is a shallow dip and the bridge reads as a causeway laid on the ground.
        return gapColumns * 3 >= columns;
    }

    // Trim the segment between two cell centroids down to the gap it crosses,
    // returning the two abutments. A centroid is a place to aim from, never an
    // end of the bridge: most of the distance between two cells is their own
    // ground, so measuring a bridge centroid-to-centroid measures mostly solid
    // earth — it rejects a short crossing between two large cells and passes a
    // long one between two small ones.
    private static bool TrimToGap(float ax, float az, float bx, float bz, int deckY,
        int[,] height, CellularTerrainData cd, int worldMinX, int worldMinZ,
        out float x0, out float z0, out float x1, out float z1)
    {
        x0 = z0 = x1 = z1 = 0f;
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        float dx = bx - ax;
        float dz = bz - az;
        float len = Mathf.Sqrt(dx * dx + dz * dz);
        if (len < 1f) { return false; }
        int samples = Mathf.CeilToInt(len) + 1;

        int GroundAt(int i)
        {
            float t = (float)i / (samples - 1);
            int lx = Mathf.RoundToInt(ax + dx * t) - worldMinX;
            int lz = Mathf.RoundToInt(az + dz * t) - worldMinZ;
            if (lx < 0 || lz < 0 || lx >= sizeX || lz >= sizeZ) { return int.MinValue; }
            return height[lx, lz];
        }

        // The gap is the outermost pair of columns standing bridgeGapDepth or
        // more under the deck. Anything solid BETWEEN them is left to the
        // ribbon test, which sees the deck's full width rather than one line.
        int first = -1;
        int last = -1;
        for (int i = 0; i < samples; i++)
        {
            int g = GroundAt(i);
            if (g == int.MinValue) { return false; }
            if (deckY - g < cd.bridgeGapDepth) { continue; }
            if (first < 0) { first = i; }
            last = i;
        }
        if (first <= 0 || last >= samples - 1) { return false; }
        if (first > BRIDGE_ABUTMENT_SEARCH_COLUMNS) { return false; }
        if (samples - 1 - last > BRIDGE_ABUTMENT_SEARCH_COLUMNS) { return false; }

        // An abutment stands at the deck's own level or one step under it. A
        // deck meeting its ground anywhere else is a plank driven into a cliff
        // face rather than a bridge landing on one.
        int step = Math.Max(1, cd.quantizeStep);
        int groundA = GroundAt(first - 1);
        int groundB = GroundAt(last + 1);
        if (groundA > deckY || groundA < deckY - step) { return false; }
        if (groundB > deckY || groundB < deckY - step) { return false; }

        float t0 = (float)(first - 1) / (samples - 1);
        float t1 = (float)(last + 1) / (samples - 1);
        x0 = ax + dx * t0;
        z0 = az + dz * t0;
        x1 = ax + dx * t1;
        z1 = az + dz * t1;
        return true;
    }

    // The deck's profile along the span: a mild arch written in the terrain's
    // own vocabulary — flat treads on the quantizeStep lattice, joined by risers.
    //
    // EVERY RISER CHOOSES FOR ITSELF whether to be a crisp 2m step or a short
    // 1-voxel-per-column grade, so one arch carries both: step up off the
    // ground, grade to the crown, step down the far side. Rolling it per DECK
    // instead gives bridges that are uniformly one or the other, which is a
    // duller shape and reads as two templates rather than as one kind of thing.
    //
    // Geometry has the veto, not the roll: a riser can only spread into a grade
    // if the treads either side have columns to spare, so a short bridge — whose
    // treads are a couple of columns long — comes out stepped whatever the
    // chance says. A graded column moves exactly ONE voxel, because that is
    // TerrainGenData.maxGradeStep: what the mesher smooths, and above which a
    // grade hardens straight back into the stairs it was meant to replace.
    private static int[] BuildArchProfile(int deckY, int columns, Random rng,
        CellularTerrainData cd, out bool[] sloped)
    {
        int n = Math.Max(2, columns);
        var deck = new int[n];
        sloped = new bool[n];
        int step = Math.Max(1, cd.quantizeStep);
        float crown = (n - 1) * cd.bridgeArchGrade;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / (n - 1);
            deck[i] = deckY + step * Mathf.RoundToInt(crown * Mathf.Sin(Mathf.Pi * t) / step);
        }
        if (crown < step) { return deck; }

        // Runs are measured on the stepped profile and the grades written to a
        // copy, so two neighbouring risers cannot spend the same columns twice.
        int[] treads = (int[])deck.Clone();
        for (int i = 1; i < n; i++)
        {
            int delta = treads[i] - treads[i - 1];
            if (delta == 0) { continue; }
            int height = Math.Abs(delta);
            int before = TreadRun(treads, i - 1, -1);
            int after = TreadRun(treads, i, 1);
            if (Math.Min(before, after) < Math.Max(cd.bridgeArchSlopeTread, 2 * height - 1))
            {
                continue;
            }
            if (rng.NextDouble() >= cd.bridgeArchSlopeChance) { continue; }

            // The grade is spent on the LOW side of the riser: a ramp belongs at
            // the foot of the thing it climbs, not hanging off the top of it.
            int low = Math.Min(treads[i - 1], treads[i]);
            for (int k = 1; k < height; k++)
            {
                int idx = delta > 0 ? i - k : i - 1 + k;
                deck[idx] = low + (height - k);
                sloped[idx] = true;
            }
        }
        return deck;
    }

    // Length of the flat run containing `start`, walking in one direction.
    private static int TreadRun(int[] treads, int start, int dir)
    {
        int level = treads[start];
        int run = 1;
        for (int i = start + dir; i >= 0 && i < treads.Length && treads[i] == level; i += dir)
        {
            run++;
        }
        return run;
    }

    // Walk the columns of one ribbon: a capsule of halfWidth about the segment.
    // `visit` gets the index into the deck profile as well as the column, since
    // every consumer of a ribbon needs the deck level at that point.
    private static void ForEachRibbonColumn(BridgeRibbon r, int sizeX, int sizeZ,
        int worldMinX, int worldMinZ, Action<int, int, int> visit)
    {
        float ex = r.X1 - r.X0;
        float ez = r.Z1 - r.Z0;
        float len2 = ex * ex + ez * ez;
        if (len2 < 0.0001f) { return; }

        int loX = Math.Max(0, Mathf.FloorToInt(Math.Min(r.X0, r.X1) - r.HalfWidth) - worldMinX);
        int hiX = Math.Min(sizeX - 1, Mathf.CeilToInt(Math.Max(r.X0, r.X1) + r.HalfWidth) - worldMinX);
        int loZ = Math.Max(0, Mathf.FloorToInt(Math.Min(r.Z0, r.Z1) - r.HalfWidth) - worldMinZ);
        int hiZ = Math.Min(sizeZ - 1, Mathf.CeilToInt(Math.Max(r.Z0, r.Z1) + r.HalfWidth) - worldMinZ);

        for (int lx = loX; lx <= hiX; lx++)
        {
            for (int lz = loZ; lz <= hiZ; lz++)
            {
                float px = lx + worldMinX + 0.5f - r.X0;
                float pz = lz + worldMinZ + 0.5f - r.Z0;
                float t = Math.Clamp((px * ex + pz * ez) / len2, 0f, 1f);
                float qx = px - t * ex;
                float qz = pz - t * ez;
                if (qx * qx + qz * qz > r.HalfWidth * r.HalfWidth) { continue; }
                visit(lx, lz, Mathf.RoundToInt(t * (r.Deck.Length - 1)));
            }
        }
    }
}
