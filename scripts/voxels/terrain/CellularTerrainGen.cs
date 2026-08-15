using System;
using System.Collections.Generic;
using Godot;

// The CELLULAR terrain approach: partition the world into irregular cells, give
// each one a single flat top at the MEDIAN of the ground under it, and let every
// cell border become a wall. Where the organic approach derives cliffs from
// steepness, this one derives them from the partition — so flat ground is the
// default and every wall is a border the player can read on the minimap.
//
// Shape, in order: a warped continental base with ridged relief, dropped to the
// sea floor by an island falloff that rings the world with ocean; a jittered-grid
// Voronoi partition that subdivides itself wherever one flat top would misstate
// the ground it covers; median flattening quantized to a fixed step and relaxed
// so no wall exceeds the authored ceiling; and a corridor network over the cell
// graph that subdivides the cells it crosses.
//
// NOTHING here slopes, and nothing is interpolated between two cell tops. The
// coast and the authored flatten zones are folded into the field BEFORE the
// medians precisely so they terrace with everything else instead of smearing a
// continuous ramp across the result.
//
// The ONE thing that carves is the river pass, last: rain routed over the
// finished terraces cuts channels, breaches the rims of sinks it will not
// flood, and hands back a per-column water surface. It keeps the invariant — a
// bed is cut a whole number of steps down and water stands on the same lattice.
//
// See CellularTerrainData for the knobs and the reasoning behind each stage.
//
// Split across three files, all one partial class: this one owns the pipeline,
// CellularTerrainFeatures.cs the placed landforms (islands, mesas, quarries,
// craters, stairs, land bridges) and CellularTerrainCaves.cs the carving.
public partial class CellularTerrainGen : ITerrainGenerator
{
    // Defaults for a zone with no CellularZoneTerrainData — a plain instance so
    // the fallback cannot drift from the authored defaults. Zones authored for
    // another approach land here, which is deliberate: they still contribute
    // their shared elevation / elevationRange rather than dropping out.
    private static readonly CellularZoneTerrainData ZoneDefaults = new();

    private readonly CellularTerrainData _data;
    private readonly WorldGenData _genData;
    private readonly int _worldSeed;
    private readonly int _cellSeed;

    public CellularTerrainGen(CellularTerrainData data, WorldGenData genData, int worldSeed)
    {
        _data = data;
        _genData = genData;
        _worldSeed = worldSeed;
        _cellSeed = WorldGen.DeriveSeed(worldSeed, SEED_SALT_CELL);
    }

    // The landforms this run built, kept so their names can be registered as
    // points of interest once the height field is final.
    private LandformResult _namedFeatures;

    // Every landform this approach placed, for WorldState.PointsOfInterest.
    // Registering them is what turns "a mesa somewhere" into a place the road
    // pathfinder can route to and a signpost can name, using registries that
    // already exist — the names are stable internal identifiers (mesa_0,
    // quarry_0, crater_0, stair_0), never shown to the player.
    public IReadOnlyList<KeyValuePair<string, Vector3>> GetNamedFeatures()
    {
        return _namedFeatures?.Pois ?? (IReadOnlyList<KeyValuePair<string, Vector3>>)Array.Empty<KeyValuePair<string, Vector3>>();
    }

    // Noise channel salts for this path only, kept clear of the legacy block
    // (ends at 0x18) and the organic block (ends at 0x2A).
    private const int SEED_SALT_WARP_X = 0x40;
    private const int SEED_SALT_WARP_Z = 0x41;
    private const int SEED_SALT_MACRO = 0x42;
    private const int SEED_SALT_RELIEF = 0x43;
    private const int SEED_SALT_COAST = 0x44;
    private const int SEED_SALT_CELL = 0x45;
    private const int SEED_SALT_RELIEF_MASK = 0x46;
    private const int SEED_SALT_ISLAND = 0x47;
    private const int SEED_SALT_LANDFORM = 0x48;
    private const int SEED_SALT_CAVE = 0x49;
    private const int SEED_SALT_CAVE_FLOOR = 0x4A;
    private const int SEED_SALT_STALAGMITE = 0x4B;
    private const int SEED_SALT_CLIFF_EROSION = 0x4C;
    private const int SEED_SALT_RIVER_WIDTH = 0x4D;

    // Passes the cell-graph wall relaxation may take. Each pass strictly lowers
    // at least one cell, so this is a runaway guard rather than a tuning knob.
    private const int MAX_RELAX_PASSES = 64;

    // Step, in voxels, by which a ramp grows outward looking for the length that
    // carries its drop at the authored grade. Two keeps the search cheap while
    // staying finer than the quantize step it is chasing.
    private const float RAMP_PROBE_STEP = 2f;


    public HeightMap BuildHeightMap(WorldState ws)
    {
        CellularTerrainData cd = _data;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;
        int sizeX = worldMaxX - worldMinX + 1;
        int sizeZ = worldMaxZ - worldMinZ + 1;

        var warpX = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_WARP_X), cd.warpFrequency, 2);
        var warpZ = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_WARP_Z), cd.warpFrequency, 2);
        var macro = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_MACRO), cd.macroFrequency, cd.macroOctaves);
        var relief = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_RELIEF), cd.reliefFrequency, cd.reliefOctaves);
        var continent = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_COAST),
            cd.continentFrequency, cd.continentOctaves);
        var reliefMask = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_RELIEF_MASK),
            cd.reliefMaskFrequency, 2);

        // Pass 1 — the continuous field, in voxels relative to sea level. This
        // is never what the player walks on; it is the signal the cells take
        // their medians from. The warped coordinates are KEPT rather than
        // recomputed because the cell lookup re-reads them once per subdivision
        // round, and two world-sized float arrays are cheaper than four more
        // noise samples per column per round.
        //
        // Split in two halves with the island injection between them. The mask
        // (`coast`) has to be complete before islands can be placed — a
        // candidate site is chosen by how far below the waterline it already
        // sits and how far it is from the nearest land — and the islands then
        // have to be IN the mask before it decides land from sea. So the first
        // half banks every per-column term, and the second resolves the mask
        // and the field once the discs are known.
        float[,] field = new float[sizeX, sizeZ];
        float[,] warpedX = new float[sizeX, sizeZ];
        float[,] warpedZ = new float[sizeX, sizeZ];
        // Per-column weight of any authored flattenSurface zone (the village).
        // Kept because the fold-in above only pulls the FIELD toward the
        // authored level - the cells still take their own medians, so a village
        // straddling two of them comes out on two elevations. Stamping needs to
        // know which cells are the village.
        float[,] flattenW = new float[sizeX, sizeZ];
        // Per-column rainfall weight, folded from the zones' authored humidity
        // (ZoneData.weather.humidity — the biome-local moisture channel; the
        // rainAmount beside it is imported weather and says nothing about the
        // climate here). Normalised to a mean of 1 over land further down, so
        // it redistributes flow between wet and dry country without changing
        // the world's total.
        float[,] rain = new float[sizeX, sizeZ];
        // Banked between the two halves of pass 1, so the island injection can
        // sit between the mask being built and the mask being read.
        float[,] coastBase = new float[sizeX, sizeZ];
        float[,] baseH = new float[sizeX, sizeZ];
        float[,] reliefH = new float[sizeX, sizeZ];
        float[,] flattenTarget = new float[sizeX, sizeZ];
        float unit = cd.zoneElevationUnit;
        // The guaranteed ocean margin is measured as a CHEBYSHEV distance to the
        // border, not a radius: it then follows the map rectangle at a constant
        // width instead of biting deep into the corners the way an ellipse does.
        float edgeMargin = Math.Max(1f, cd.edgeMarginChunks * ChunkState.SIZE);
        float halfExtentX = sizeX * 0.5f;
        float halfExtentZ = sizeZ * 0.5f;
        float centerX = (worldMinX + worldMaxX) * 0.5f;
        float centerZ = (worldMinZ + worldMaxZ) * 0.5f;

        // Allocated ONCE, not per column: the weights-out overload lets this
        // pass fold its own per-zone knobs from the same solve that produces the
        // shared scalars, instead of paying for a second solve per column.
        ZoneGenData[] zoneGens = _genData.ZoneGens;
        int zoneCount = zoneGens != null ? zoneGens.Length : 0;
        var zoneWeights = new float[zoneCount];

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int lx = wx - worldMinX;
                int lz = wz - worldMinZ;

                // Every channel — including the cell lookup below — reads the
                // SAME warped coordinate, so the continental swell, the ridges
                // riding on it and the cell borders cutting across both bend
                // together instead of sliding against each other.
                float sx = wx + cd.warpAmplitude * warpX.GetNoise2D(wx, wz);
                float sz = wz + cd.warpAmplitude * warpZ.GetNoise2D(wx, wz);
                warpedX[lx, lz] = sx;
                warpedZ[lx, lz] = sz;

                WorldGen.BlendedZoneGen blend = WorldGen.SampleBlendedZoneGen(wx, wz, zoneGens, zoneWeights);
                float humidity = 0f;
                float weightSum = 0f;
                for (int zi = 0; zi < zoneCount; zi++)
                {
                    if (zoneWeights[zi] <= 0f) { continue; }
                    ZoneGenData zg = zoneGens[zi];
                    humidity += (zg?.zone?.weather?.humidity ?? DEFAULT_HUMIDITY) * zoneWeights[zi];
                    weightSum += zoneWeights[zi];
                }
                humidity = weightSum > 0f ? Math.Clamp(humidity / weightSum, 0f, 1f) : DEFAULT_HUMIDITY;

                // Twice the humidity, so an average zone (0.5) sheds its one
                // unit and the authored extremes land at 0.1 (desert) and 1.9
                // (swamp). Normalised to a mean of 1 over land once the field
                // is known, which is what keeps the flow THRESHOLDS meaningful
                // as this weight is retuned.
                rain[lx, lz] = Math.Max(0.01f,
                    Mathf.Lerp(1f, 2f * humidity, Math.Clamp(cd.rainfallHumidityWeight, 0f, 1f)));

                // Ridged relief in [0,1]: 1 along the noise's zero crossings
                // (which branch like a drainage divide), 0 in the troughs.
                float ridged = 1f - Math.Abs(relief.GetNoise2D(sx, sz));
                ridged = Mathf.Pow(Math.Clamp(ridged, 0f, 1f), cd.reliefSharpness);

                // Relief is scaled by a POWER of the zone's elevationRange, not
                // by the range itself. The authored ranges only span 2 to 4, so
                // a linear map lifts the whole island and leaves the mountain
                // merely twice as tall as the swamp; the exponent widens that
                // ratio without touching the shared zone assets.
                float rangeScale = Mathf.Pow(Math.Max(0f, blend.ElevationRange), cd.reliefRangeExponent);

                // Highland mask: a low-frequency field that modulates how much
                // relief this part of the island gets, so some of it is plain
                // and some is broken country. It scales an AMPLITUDE — it never
                // adds a step of its own. That distinction is the whole design:
                // a wall here must come from the cells quantizing a genuinely
                // steep patch of field, so making walls means making the field
                // steeper, not injecting edges into it. A thresholded block
                // would manufacture a wall at its boundary that no gradient
                // earned, and it would sit at exactly the throw height whatever
                // the terrain around it was doing.
                float hills = Mathf.SmoothStep(cd.reliefMaskLow, cd.reliefMaskHigh,
                    reliefMask.GetNoise2D(sx, sz));
                float reliefAmount = rangeScale * unit * cd.reliefAmplitudeScale * ridged * hills;
                reliefH[lx, lz] = reliefAmount;

                // The natural height here, before the coastal taper and before
                // any authored clearing. Only the land/sea mask reads this one —
                // it wants to know how high the ground WOULD stand, so that a
                // flattened village near the shore does not read as low ground
                // and invite the sea in.
                float natural = blend.Elevation * unit + cd.macroAmplitude * macro.GetNoise2D(sx, sz);
                baseH[lx, lz] = natural;
                float h = natural + reliefAmount;
                flattenW[lx, lz] = blend.FlattenWeight;
                flattenTarget[lx, lz] = blend.FlattenLevel * unit;

                // The coast is a contour of its OWN noise field, and carries no
                // height information — that is what stops the continent coming
                // out as a dome. A radial falloff multiplies height by distance
                // from the centre, so the whole map slopes to its edges and the
                // shore sits at the bottom of the longest hill in the world.
                // Here `land` only decides where the water is; how high the
                // ground stands beside it is the zones' business alone.
                //
                // Applied to the FIELD, before the cells see it, so the coast is
                // terraced and quantized like everything else — a wide shoreBand
                // gives a run of beach terraces rather than a smooth slope.
                float coast = continent.GetNoise2D(sx, sz) - cd.continentSeaLevel;

                // A gentle pull toward the middle of the map, applied to the
                // MASK only. Pure noise is free to put its landmass anywhere,
                // which regularly left a whole authored zone under water — the
                // zones are laid out by quadrant and cannot move to meet it.
                // Because this biases where the WATER is and not how high the
                // ground stands, it centres the island without reintroducing the
                // dome a radial height falloff produces.
                // CUBED, not linear: a linear pull is already halfway to the sea
                // by mid-map, which drowns the outer half of every quadrant zone
                // and leaves only a small central island. The cube keeps the
                // interior almost untouched — the noise alone shapes it — and
                // concentrates the whole pull into the outer third, where its
                // job is to close the coastline before the map edge.
                float nx = (wx - centerX) / halfExtentX;
                float nz = (wz - centerZ) / halfExtentZ;
                float radial = Mathf.Sqrt(nx * nx + nz * nz);
                coast -= cd.continentCenterBias * radial * radial * radial;

                // High ground resists being drowned. Without this the mask is
                // blind to the zones, and the noise regularly puts open sea over
                // the whole mountain quadrant — the zones are fixed quadrants
                // and cannot move to meet the coast. It also does the shoreline
                // a favour: the coast now follows the relief, cutting bays into
                // the low ground and leaving headlands where ridges run out to
                // meet the water, which no amount of coastline noise imitates.
                coast += cd.continentHeightBias * h;

                // Guaranteed ocean margin: push the field under the waterline
                // over the last edgeMargin voxels, whatever the noise did. Full
                // strength (2) rather than a taper, so the border is open sea
                // and not a ring of shallows the coastline can wander into.
                float toEdge = Math.Min(
                    Math.Min(wx - worldMinX, worldMaxX - wx),
                    Math.Min(wz - worldMinZ, worldMaxZ - wz));
                coast -= 2f * (1f - Mathf.SmoothStep(0f, edgeMargin, toEdge));
                coastBase[lx, lz] = coast;
            }
        }

        // Pass 1.25 — offshore islands and sea stacks, added to the MASK. They
        // have to land here, between the mask being built and the mask being
        // read: everything downstream then treats an island as ordinary land
        // and it partitions, medians and terraces exactly like the mainland.
        // `stackTaper` is the one thing they change about the resolve below —
        // inside a sea stack the coastal relief taper is skipped, which is the
        // whole difference between an islet and a rock.
        var stackTaper = new float[sizeX, sizeZ];
        List<IslandDisc> islands = PlaceOffshoreIslands(coastBase, reliefH, stackTaper,
            worldMinX, worldMinZ, edgeMargin, cd);

        // Pass 1.5 — resolve the finished mask into the field.
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                float coast = coastBase[lx, lz];

                // Coastal plain: fade the RELIEF out as the coast is approached,
                // so land arrives at the water near sea level instead of running
                // a hillside into it. Deliberately does NOT touch the uplift —
                // an uplifted block that reaches the sea keeps its full throw and
                // meets the water as a cliff, which is where shore cliffs come
                // from. Tapering both would give a uniformly gentle beach, and
                // tapering neither is what made the shore the steepest ground in
                // the world.
                float plain = Mathf.SmoothStep(0f, Math.Max(0.01f, cd.coastalPlainBand), coast);
                float stack = stackTaper[lx, lz];
                plain = Math.Max(plain, stack);
                float h = baseH[lx, lz] + reliefH[lx, lz] * plain;
                // With the taper off a stack's height is whatever the relief
                // happened to be, and the world is sized to the tallest thing in
                // it — so the cap is a chunk-count budget, not a look. See
                // CellularTerrainData.seaStackMaxHeight.
                if (stack > 0f) { h = Math.Min(h, cd.seaStackMaxHeight); }
                float fw = flattenW[lx, lz];
                if (fw > 0f)
                {
                    h = h * (1f - fw) + flattenTarget[lx, lz];
                }

                float land = Mathf.SmoothStep(-cd.shoreBand, cd.shoreBand, coast);
                field[lx, lz] = Mathf.Lerp(-cd.oceanDepth, h, land);
            }
        }

        // Rain is normalised to a mean of 1 over LAND (the sea sheds nothing
        // that matters — it is already the outlet), so the two accumulation
        // passes below keep the same magnitudes whatever rainfallHumidityWeight
        // is set to, and their flow thresholds keep their meaning.
        NormaliseRainOverLand(rain, field);

        // Pass 1.75 — cut the drainage network into the field. Every column sheds
        // its rainfall to the steepest downhill neighbour and the surface is
        // then incised in proportion to the flow crossing it, so the valleys
        // branch and meet the way real ones do rather than following a noise
        // channel. It is also the one signal in this approach whose shape comes
        // from the terrain itself, which is what stops the cell field reading as
        // unrelated plateaus. The per-column depth comes back so the cells it
        // cut can be refined around it.
        float[,] carved = CarveDrainage(field, rain, cd);

        // Pass 2 — the cellular partition, refined until no cell misstates the
        // ground beneath it by more than subdivideRange, or it has shrunk to
        // minCellSizeMeters. Subdivision is driven by TARGET SIZE rather than a
        // level count so the two consumers can ask for the sizes they actually
        // need — ordinary terraces stop at minCellSizeMeters, ramp corridors go
        // all the way down to a ramp's width.
        int maxLevel = LevelForSize(cd.minCellSizeMeters);
        var cellLevel = new byte[sizeX, sizeZ];
        var coarseX = new short[sizeX, sizeZ];
        var coarseZ = new short[sizeX, sizeZ];
        var cellKey = new long[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                FindCell(warpedX[lx, lz], warpedZ[lx, lz], 0, out int gx, out int gz);
                coarseX[lx, lz] = (short)gx;
                coarseZ[lx, lz] = (short)gz;
                cellKey[lx, lz] = PackKey(0, gx, gz, gx, gz);
            }
        }

        CellPartition partition = BuildPartition(cellKey, cellLevel, field, flattenW, worldMinX, worldMinZ);
        for (int round = 0; round < maxLevel; round++)
        {
            var promote = new bool[partition.Count];
            bool any = false;
            for (int c = 0; c < partition.Count; c++)
            {
                if (partition.Level[c] >= maxLevel) { continue; }
                SampleZoneKnobs(partition.AnchorX[c], partition.AnchorZ[c], out float subdivideScale, out float _);

                // Per-cell appetite for splitting, hashed off the cell's own
                // identity. Without it cell size is a pure function of how busy
                // the ground is, so a region of even relief comes out as a field
                // of near-identical terraces — regular in a way that reads as
                // procedural. This scatters the threshold instead, so a quiet
                // area still holds a mix of big and small tops.
                float appetite = Mathf.Lerp(1f, 2f * Hash01(partition.Key[c], _cellSeed),
                    Math.Clamp(cd.cellSizeRandomness, 0f, 1f));
                if (partition.Max[c] - partition.Min[c] > cd.subdivideRange * subdivideScale * appetite)
                {
                    promote[c] = true;
                    any = true;
                }
            }
            if (!any) { break; }
            Subdivide(partition, promote, cellKey, cellLevel, coarseX, coarseZ, warpedX, warpedZ,
                maxLevel, jumpToTarget: false);
            partition = BuildPartition(cellKey, cellLevel, field, flattenW, worldMinX, worldMinZ);
        }

        // Pass 2.5 — refine the cells the drainage cut. A valley is the one
        // place a flat top is most obviously wrong: the channel and its banks
        // land in one cell, the median picks whichever won, and the valley
        // either fills in or swallows its banks. Cells carrying real incision
        // drop to their own target size so the channel comes out as a terraced
        // gorge instead.
        int erosionLevel = LevelForSize(cd.erosionCellSizeMeters);
        if (cd.drainageCarveDepth > 0f && erosionLevel > 0)
        {
            var eroded = new bool[partition.Count];
            for (int lx = 0; lx < sizeX; lx++)
            {
                for (int lz = 0; lz < sizeZ; lz++)
                {
                    if (carved[lx, lz] >= cd.erosionSubdivideDepth) { eroded[partition.Index[lx, lz]] = true; }
                }
            }
            Subdivide(partition, eroded, cellKey, cellLevel, coarseX, coarseZ, warpedX, warpedZ,
                erosionLevel, jumpToTarget: true);
            partition = BuildPartition(cellKey, cellLevel, field, flattenW, worldMinX, worldMinZ);
        }

        int cellsAfterVariance = partition.Count;

        // Pass 3 — flatten, then relax the cell graph so no wall exceeds the
        // authored ceiling. Done on the pre-corridor partition because the
        // corridor network below is routed over these heights.
        partition = MergeSlivers(partition, cellKey, cellLevel, field, flattenW, worldMinX, worldMinZ);
        FlattenCells(partition, cd);
        StampFlattenCells(partition, cd);
        List<CellEdge> edges = BuildCellGraph(partition, worldMinX, worldMinZ);
        RelaxCellWalls(partition, edges, cd);

        // Pass 4 — the ramp network: a spanning set of cell-graph edges, which
        // is where roads will run. The cells each one passes through subdivide
        // down to the ramp's own width, so the ground beside the cutting steps
        // down alongside it instead of standing as one wall the whole way; the
        // partition is then rebuilt and re-flattened.
        int rampLevel = LevelForSize(cd.rampCellSizeMeters);
        List<CorridorSegment> corridors = BuildCorridors(partition, edges, cd, _worldSeed);
        if (corridors.Count > 0 && rampLevel > 0)
        {
            var promote = new bool[partition.Count];
            MarkCorridorCells(partition, corridors, worldMinX, worldMinZ, promote);
            Subdivide(partition, promote, cellKey, cellLevel, coarseX, coarseZ, warpedX, warpedZ,
                rampLevel, jumpToTarget: true);
            partition = BuildPartition(cellKey, cellLevel, field, flattenW, worldMinX, worldMinZ);
            partition = MergeSlivers(partition, cellKey, cellLevel, field, flattenW, worldMinX, worldMinZ);
            FlattenCells(partition, cd);
            StampFlattenCells(partition, cd);
            edges = BuildCellGraph(partition, worldMinX, worldMinZ);
            RelaxCellWalls(partition, edges, cd);
        }

        // Pass 4.5 — the placed landforms: mesas, quarries, craters and
        // terraced stairs, all through ONE pass that picks cells by a
        // topological rule and pins what it changes.
        //
        // It has to run HERE, after the last RelaxCellWalls and before the
        // heights are materialized. Earlier and relax undoes it — the rule that
        // no cell may stand more than maxCellStep above a neighbour is exactly
        // what a mesa violates on purpose. Later and there is no partition left
        // to reason about: a landform is a statement about cells and their
        // neighbours, not about columns.
        LandformResult landforms = PlaceLandforms(partition, edges, cd, worldMinX, worldMinZ);

        // A ramp through a landform is what stops it being one. The corridors
        // were chosen before the ground moved and are not re-routed — the
        // spanning set is over the pre-landform heights, and re-deriving it here
        // would re-terrace every cell it crosses all over again. Instead CutRamps
        // refuses the few that now run into one.
        bool[,] noRampColumns = BuildNoRampColumns(partition, landforms);

        // One line per generate: where the cell count actually went. Cell size is
        // the single most-tuned property of this approach and three separate
        // passes can shrink it, so the counts are worth more than any one knob.
        int slivers = 0;
        int smallest = int.MaxValue;
        for (int c = 0; c < partition.Count; c++)
        {
            if (partition.Columns[c] < 16) { slivers++; }
            if (partition.Columns[c] < smallest) { smallest = partition.Columns[c]; }
        }
        GD.Print($"[CellularTerrain] cells under 16 columns: {slivers} / {partition.Count}, smallest {smallest}");
        GD.Print($"[CellularTerrain] cells: variance {cellsAfterVariance}"
            + $" -> erosion+ramps {partition.Count} (level-0 target {cd.cellSizeMeters}m,"
            + $" variance floor {cd.minCellSizeMeters}m, erosion {cd.erosionCellSizeMeters}m,"
            + $" ramp {cd.rampWidthMaxMeters}m), ramps {corridors.Count}");

        // Pass 5 — resolve every column: the flat cell top, then the ramps cut
        // through it. Off a ramp, every column is a multiple of quantizeStep and
        // nothing between two cell tops is interpolated, so the world is flat
        // ground and whole-step walls; the ramps are the only sloped ground.
        float[,] flat = new float[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                flat[lx, lz] = partition.Flat[partition.Index[lx, lz]];
            }
        }
        float[,] ramp = CutRamps(corridors, flat, noRampColumns, cd, worldMinX, worldMinZ);

        int[,] height = new int[sizeX, sizeZ];
        int[,] plateau = new int[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                float flatH = flat[lx, lz];
                float h = float.IsNaN(ramp[lx, lz]) ? flatH : ramp[lx, lz];
                int hi = WorldGen.WATER_LEVEL + Mathf.RoundToInt(h);
                height[lx, lz] = hi;
                // Plateau is the flat-ground reference: equal to Height on a
                // cell top, below it on a ramp, so IsFlatDryGrassAt keeps
                // scatter off the cuttings. Clamped so it never exceeds
                // Height — a ramp cutting DOWN into a cell reads as non-flat
                // either way, and consumers may assume Plateau <= Height.
                plateau[lx, lz] = Math.Min(WorldGen.WATER_LEVEL + Mathf.RoundToInt(flatH), hi);
            }
        }

        // Ground the carve may not touch: the authored village, and every cell a
        // landform claimed. Undermining a mesa or a crater rim is the same
        // mistake as breaching a cave roof — the feature is the thing that was
        // just built, and a tunnel through it is what unbuilds it. Built here,
        // ahead of the erosion, because the cave-mouth reservation below needs
        // it too.
        var noCarve = new bool[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                int cell = partition.Index[lx, lz];
                noCarve[lx, lz] = partition.Pinned[cell] || landforms.IsLandform[cell];
            }
        }

        // Pass 5.4 — hold back the walls the cave mouths will need. Caves and
        // cliff erosion compete for the same rare terrain (a full maxCellStep
        // wall), and erosion runs first, so without this it takes them all —
        // measured, candidate mouths fell from 80 to 5. See ReserveCaveEntrances.
        bool[,] noErode = ReserveCaveEntrances(height, noCarve, cd, out int reservedColumns);

        // Pass 5.5 — erode the outermost columns of the tall cliffs. Before the
        // water pass, so the rivers route over the finished ground rather than
        // over terraces that move under them afterwards; after the ramps,
        // because it needs to know which columns are ramp (the only sloped, and
        // only off-lattice, ground in the world) in order to leave them alone.
        // Its other half, the talus at the foot of each face, runs AFTER the
        // water — see below.
        ErodeCliffFaces(height, plateau, ramp, partition, landforms, noErode, cd,
            worldMinX, worldMinZ);
        GD.Print($"[CellularTerrain] cave-mouth reservation: {_reservedSites} sites held back from"
            + $" erosion, {reservedColumns} columns");

        // Pass 6 — rivers and lakes, routed over the FINISHED terraces. It has
        // to be these heights and not the pre-cell field: the water surface has
        // to land on the same lattice the ground did, and the cells move ground
        // by whole steps after the field is read.
        var pinned = new bool[sizeX, sizeZ];
        var onRamp = new bool[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                pinned[lx, lz] = partition.Pinned[partition.Index[lx, lz]];
                onRamp[lx, lz] = !float.IsNaN(ramp[lx, lz]);
            }
        }
        int[,] water = BuildWaterways(height, plateau, field, rain, pinned, onRamp, cd,
            worldMinX, worldMinZ, out Vector2[,] current, out List<WaterfallSite> waterfalls);

        // Pass 6.5 — talus: rubble at the foot of the eroded faces. AFTER the
        // water, and that ordering is the whole reason it is a separate pass
        // from the erosion above: it RAISES ground, and a 2-voxel block dropped
        // into a channel the flood already routed would dam it with nothing
        // downstream to notice. It skips wet columns instead.
        ScatterTalus(height, plateau, water, ramp, partition, landforms, cd, worldMinX, worldMinZ);

        // Pass 7 — land bridges, and pass 8 — caves. Both are CARVING passes and
        // both run last, after the water pass, deliberately: a deck stamped into
        // Height before it would read to the priority flood as a dam across the
        // gap it spans and pool a lake behind it, and a cave carved before it
        // would not know which of its columns end up under a river.
        //
        // The carve grid is sized from the finished heights. Nothing after this
        // point moves ground, so a bit set here is a bit IsCarvedAt can answer
        // for the rest of the run.
        AllocateCarveGrid(height, cd, worldMinX, worldMinZ);

        List<BridgeRibbon> ribbons = BuildLandBridges(partition, height, plateau, water, landforms,
            cd, worldMinX, worldMinZ);
        CarveUnderBridges(ribbons, height, cd, worldMinX, worldMinZ);
        // A bridge deck is a feature in its own right, so caves keep off it too.
        foreach (BridgeRibbon r in ribbons)
        {
            ForEachRibbonColumn(r, sizeX, sizeZ, worldMinX, worldMinZ,
                (lx, lz, _) => { noCarve[lx, lz] = true; });
        }

        BuildCaves(height, water, noCarve, cd, worldMinX, worldMinZ);
        PrepareCarveSlices(height);
        ResolveLandformPois(landforms, partition, height, worldMinX, worldMinZ);
        _namedFeatures = landforms;

        var surface = (int[,])height.Clone();
        var noSpawn = new bool[sizeX, sizeZ];
        return new HeightMap(worldMinX, worldMaxX, worldMinZ, worldMaxZ, plateau, height, surface, noSpawn,
            cd.interiorLevelStep, water, current, waterfalls);
    }

    // ----------------------------------------------------------------- cells

    // The world's columns grouped into cells. Struct-of-arrays because every
    // consumer below walks one channel at a time, and the whole thing is
    // rebuilt from scratch on each refinement rather than mutated.
    private sealed class CellPartition
    {
        public int[,] Index;        // column -> cell
        public int Count;
        public long[] Key;          // packed cell identity, stable across rebuilds
        public int[] Level;         // subdivision level of each cell
        public int[] Columns;
        public float[] CentroidX;   // world column coords
        public float[] CentroidZ;
        public int[] AnchorX;       // a real column in the cell, for zone sampling
        public int[] AnchorZ;
        public float[] Min;
        public float[] Max;
        public float[] Median;
        public float[] Flat;        // quantized top, filled by FlattenCells
        public float[] Flatten;     // mean authored flatten-zone weight
        public bool[] Pinned;       // stamped to an authored level; relax may not move it
    }

    // Group columns by cell key and measure each cell: extent of the field
    // inside it (what decides subdivision), centroid (what the corridor network
    // routes between) and median (what the cell flattens to).
    private static CellPartition BuildPartition(long[,] key, byte[,] level, float[,] field,
        float[,] flattenW, int worldMinX, int worldMinZ)
    {
        int sizeX = field.GetLength(0);
        int sizeZ = field.GetLength(1);
        var lookup = new Dictionary<long, int>();
        var index = new int[sizeX, sizeZ];
        int count = 0;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                long k = key[lx, lz];
                if (!lookup.TryGetValue(k, out int ci))
                {
                    ci = count++;
                    lookup[k] = ci;
                }
                index[lx, lz] = ci;
            }
        }

        var p = new CellPartition
        {
            Index = index,
            Count = count,
            Key = new long[count],
            Level = new int[count],
            Columns = new int[count],
            CentroidX = new float[count],
            CentroidZ = new float[count],
            AnchorX = new int[count],
            AnchorZ = new int[count],
            Min = new float[count],
            Max = new float[count],
            Median = new float[count],
            Flat = new float[count],
            Flatten = new float[count],
            Pinned = new bool[count],
        };
        for (int c = 0; c < count; c++)
        {
            p.Min[c] = float.MaxValue;
            p.Max[c] = float.MinValue;
        }
        foreach (KeyValuePair<long, int> kv in lookup) { p.Key[kv.Value] = kv.Key; }

        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                int c = index[lx, lz];
                float v = field[lx, lz];
                p.Columns[c]++;
                p.Flatten[c] += flattenW[lx, lz];
                p.CentroidX[c] += lx;
                p.CentroidZ[c] += lz;
                p.AnchorX[c] = lx;
                p.AnchorZ[c] = lz;
                p.Level[c] = level[lx, lz];
                if (v < p.Min[c]) { p.Min[c] = v; }
                if (v > p.Max[c]) { p.Max[c] = v; }
            }
        }
        for (int c = 0; c < count; c++)
        {
            float n = Math.Max(1, p.Columns[c]);
            p.Flatten[c] /= n;
            p.CentroidX[c] = p.CentroidX[c] / n + worldMinX;
            p.CentroidZ[c] = p.CentroidZ[c] / n + worldMinZ;
            p.AnchorX[c] += worldMinX;
            p.AnchorZ[c] += worldMinZ;
        }

        // Median per cell, via one shared buffer sliced by cell. The MEDIAN and
        // not the mean: a cell straddling a cliff has a bimodal field, and the
        // mean lands in the gap between the two populations — a terrace at a
        // height that describes nothing under it. The median commits to the
        // larger side, which is the ground most of the cell actually is.
        var start = new int[count + 1];
        for (int c = 0; c < count; c++)
        {
            start[c + 1] = start[c] + p.Columns[c];
        }
        var fill = (int[])start.Clone();
        var values = new float[sizeX * sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                int c = index[lx, lz];
                values[fill[c]++] = field[lx, lz];
            }
        }
        for (int c = 0; c < count; c++)
        {
            int len = p.Columns[c];
            if (len <= 0) { continue; }
            Array.Sort(values, start[c], len);
            p.Median[c] = values[start[c] + len / 2];
        }
        return p;
    }

    // Promote every column of every marked cell one level finer, and re-key it.
    // Whole cells, never individual columns: a partially promoted cell would
    // leave its remainder keyed at the coarse level and straddling the new
    // borders, which is a partition in name only.
    // `jumpToTarget` picks how far a marked cell falls: the variance pass steps
    // ONE level at a time so a cell stops as soon as it is honest about its
    // ground, while the ramp pass jumps straight to the target because a
    // half-refined corridor is no use to a cutting that needs the whole run at
    // its own width.
    private void Subdivide(CellPartition partition, bool[] promote, long[,] key, byte[,] level,
        short[,] coarseX, short[,] coarseZ, float[,] warpedX, float[,] warpedZ,
        int targetLevel, bool jumpToTarget)
    {
        int sizeX = key.GetLength(0);
        int sizeZ = key.GetLength(1);
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                if (!promote[partition.Index[lx, lz]]) { continue; }
                int lvl = jumpToTarget
                    ? Math.Max(level[lx, lz], targetLevel)
                    : Math.Min(targetLevel, level[lx, lz] + 1);
                if (lvl == level[lx, lz]) { continue; }
                level[lx, lz] = (byte)lvl;
                FindCell(warpedX[lx, lz], warpedZ[lx, lz], lvl, out int gx, out int gz);
                // The level-0 coords stay in the key, so a fine cell is CUT by
                // its coarse parent's border instead of spanning two parents
                // that subdivided for different reasons.
                key[lx, lz] = PackKey(lvl, coarseX[lx, lz], coarseZ[lx, lz], gx, gz);
            }
        }
    }

    // Median -> quantized flat top. Every cell top in the world lands on a
    // multiple of quantizeStep, which is what makes the wall between two
    // neighbours a whole number of steps rather than an arbitrary height.
    private static void FlattenCells(CellPartition p, CellularTerrainData cd)
    {
        float step = Math.Max(1, cd.quantizeStep);
        for (int c = 0; c < p.Count; c++)
        {
            p.Flat[c] = Mathf.Round(p.Median[c] / step) * step;
        }
    }

    // Fold away cells too small to be a terrace.
    //
    // They come from the nesting rule: a cell's key carries its LEVEL-0 parent,
    // so a fine cell straddling a coarse border is split in two and the far
    // piece can be a single column. That column then takes its own median and
    // quantizes independently — a one-voxel spike standing in open ground. It
    // shows up worst where the subdivision level changes sharply, which is why
    // the coast and the zone borders were full of them (measured: 44 of 354
    // cells under 16 columns, smallest 1).
    //
    // Each sliver is folded into the neighbour it shares the LONGEST border
    // with, never into another sliver, so it joins the terrace it is most part
    // of. Iterated, because folding one sliver can leave its neighbour eligible.
    private CellPartition MergeSlivers(CellPartition p, long[,] cellKey, byte[,] cellLevel,
        float[,] field, float[,] flattenW, int worldMinX, int worldMinZ)
    {
        int minColumns = Math.Max(1, _data.minCellColumns);
        for (int pass = 0; pass < SLIVER_MERGE_PASSES; pass++)
        {
            List<CellEdge> edges = BuildCellGraph(p, worldMinX, worldMinZ);
            var target = new int[p.Count];
            var bestBorder = new int[p.Count];
            for (int c = 0; c < p.Count; c++) { target[c] = -1; }

            foreach (CellEdge e in edges)
            {
                Consider(e.A, e.B, e.Border);
                Consider(e.B, e.A, e.Border);
            }
            void Consider(int self, int other, int border)
            {
                if (p.Columns[self] >= minColumns) { return; }
                // Never fold into something even smaller, or two slivers swap
                // into each other and neither goes away.
                if (p.Columns[other] < p.Columns[self]) { return; }
                if (border <= bestBorder[self]) { return; }
                bestBorder[self] = border;
                target[self] = other;
            }

            bool any = false;
            int sizeX = cellKey.GetLength(0);
            int sizeZ = cellKey.GetLength(1);
            for (int lx = 0; lx < sizeX; lx++)
            {
                for (int lz = 0; lz < sizeZ; lz++)
                {
                    int c = p.Index[lx, lz];
                    if (target[c] < 0) { continue; }
                    cellKey[lx, lz] = p.Key[target[c]];
                    cellLevel[lx, lz] = (byte)p.Level[target[c]];
                    any = true;
                }
            }
            if (!any) { break; }
            p = BuildPartition(cellKey, cellLevel, field, flattenW, worldMinX, worldMinZ);
        }
        return p;
    }

    // Folding a sliver can leave its neighbour eligible in turn; three passes
    // clears the chains this partition actually produces.
    private const int SLIVER_MERGE_PASSES = 3;

    // Stamp every cell of an authored flattenSurface zone (the village) to ONE
    // elevation and pin it there.
    //
    // Folding the zone into the field is not enough on its own: the cells still
    // take their own medians, so a village spanning several of them comes out
    // spread over two or three terraces with walls through the middle of it —
    // which is exactly what a hand-authored settlement cannot survive. The level
    // is the median of what those cells resolved to, so the village still sits
    // where the surrounding land put it, snapped to the quantize step like every
    // other cell top. Pinning keeps the wall relaxation from dragging it back
    // down afterwards; the relaxation lowers its NEIGHBOURS instead, which is
    // the right answer — the village is the authored thing and the hillside is
    // not.
    //
    // How much of a cell must belong to the zone before it stamps is authored
    // (flattenCellWeight). The zone kernel blends out over several chunks, so a
    // low bar flattens a wide apron of ordinary countryside around the
    // buildings; a high one keeps the clearing tight to the zone core.
    private static void StampFlattenCells(CellPartition p, CellularTerrainData cd)
    {
        var levels = new List<float>();
        for (int c = 0; c < p.Count; c++)
        {
            if (p.Flatten[c] >= cd.flattenCellWeight) { levels.Add(p.Flat[c]); }
        }
        if (levels.Count == 0) { return; }

        // Snapped to the INTERIOR lattice, not the terrain's quantize step.
        // WorldGen.FootprintPlateauY floors a subscene's anchor to
        // HeightMap.LevelStep before stamping it, so a village sitting at 2 with
        // a LevelStep of 4 stamps its buildings at 0 — floors two voxels under
        // the ground, well and firepit buried, and the plaza reading as a step
        // above everything around it. Any level that is a multiple of the
        // interior step is also a multiple of the quantize step (the lattice is
        // coarser), so this satisfies both.
        levels.Sort();
        float step = Math.Max(1, cd.interiorLevelStep);
        float level = Mathf.Round(levels[levels.Count / 2] / step) * step;
        for (int c = 0; c < p.Count; c++)
        {
            if (p.Flatten[c] < cd.flattenCellWeight) { continue; }
            p.Flat[c] = level;
            p.Pinned[c] = true;
        }
    }


    // One shared border between two cells: which cells, and where the border
    // sits. The border centroid is the pass — where a corridor crosses it.
    private struct CellEdge
    {
        public int A;
        public int B;
        public int Border;      // columns of shared border, i.e. how long the join is
        public float BorderX;
        public float BorderZ;
        public float Weight;
    }

    // Adjacency over the finished partition. Scanning the +X and +Z neighbour
    // of every column reaches each border exactly once from each side and needs
    // no geometry from the Voronoi construction, so it stays correct however the
    // cells were refined.
    private static List<CellEdge> BuildCellGraph(CellPartition p, int worldMinX, int worldMinZ)
    {
        int sizeX = p.Index.GetLength(0);
        int sizeZ = p.Index.GetLength(1);
        var lookup = new Dictionary<long, int>();
        var edges = new List<CellEdge>();
        var counts = new List<int>();

        void Touch(int a, int b, int lx, int lz)
        {
            if (a == b) { return; }
            int lo = Math.Min(a, b);
            int hi = Math.Max(a, b);
            long k = ((long)lo << 32) | (uint)hi;
            if (!lookup.TryGetValue(k, out int ei))
            {
                ei = edges.Count;
                lookup[k] = ei;
                edges.Add(new CellEdge { A = lo, B = hi });
                counts.Add(0);
            }
            CellEdge e = edges[ei];
            e.BorderX += lx + worldMinX;
            e.BorderZ += lz + worldMinZ;
            edges[ei] = e;
            counts[ei]++;
        }

        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                int c = p.Index[lx, lz];
                if (lx + 1 < sizeX) { Touch(c, p.Index[lx + 1, lz], lx, lz); }
                if (lz + 1 < sizeZ) { Touch(c, p.Index[lx, lz + 1], lx, lz); }
            }
        }

        for (int i = 0; i < edges.Count; i++)
        {
            CellEdge e = edges[i];
            float n = Math.Max(1, counts[i]);
            e.BorderX /= n;
            e.BorderZ /= n;
            e.Border = counts[i];
            edges[i] = e;
        }
        return edges;
    }

    // Lower any cell standing more than its wall ceiling above a neighbour. The
    // excess becomes another terrace further up rather than one taller wall,
    // which is the whole reason this runs on the cell graph and not per column:
    // lowering a cell keeps its top flat and keeps it on the quantization
    // lattice, where a per-column shave would leave a scree slope instead.
    private void RelaxCellWalls(CellPartition p, List<CellEdge> edges, CellularTerrainData cd)
    {
        float step = Math.Max(1, cd.quantizeStep);
        var cap = new float[p.Count];
        for (int c = 0; c < p.Count; c++)
        {
            SampleZoneKnobs(p.AnchorX[c], p.AnchorZ[c], out float _, out float cliffScale);
            cap[c] = Math.Max(step, Mathf.Round(cd.maxCellStep * cliffScale / step) * step);
        }

        for (int pass = 0; pass < MAX_RELAX_PASSES; pass++)
        {
            bool changed = false;
            foreach (CellEdge e in edges)
            {
                if (!p.Pinned[e.A] && p.Flat[e.A] - p.Flat[e.B] > cap[e.A])
                {
                    p.Flat[e.A] = p.Flat[e.B] + cap[e.A];
                    changed = true;
                }
                else if (!p.Pinned[e.B] && p.Flat[e.B] - p.Flat[e.A] > cap[e.B])
                {
                    p.Flat[e.B] = p.Flat[e.A] + cap[e.B];
                    changed = true;
                }
            }
            if (!changed) { break; }
        }
    }

    // ----------------------------------------------------------------- roads

    // One ramp: a straight cutting through a cell border, centred on the border
    // and running along the line between the two cells' centroids. Stored as
    // centre + direction + half-length so the same segment can be walked twice —
    // once to pick the cells to refine, and again (after refinement changed the
    // heights) to cut the slope, with the length re-derived from the drop that
    // actually ended up there.
    private struct CorridorSegment
    {
        public float Cx, Cz;
        public float Dx, Dz;
        public float Half;
        public float HalfWidth;
    }

    // Pick the cell-graph edges the road network will use, and hand back the
    // ones that have a wall to climb as corridors.
    //
    // A minimum spanning tree first, so EVERY cell is reachable on foot — a
    // world of flat tops separated by walls is otherwise trivially able to seal
    // one off. Weighted by the drop between the two cells plus their separation
    // in cell-widths, so the network prefers gentle joins and short hops; that
    // is what makes routes wind along the contours rather than run straight up.
    // Then extraEdgeFraction of the cheapest remaining edges, because a pure
    // tree gives every cell exactly one way in and out.
    private static List<CorridorSegment> BuildCorridors(CellPartition p, List<CellEdge> edges,
        CellularTerrainData cd, int worldSeed)
    {
        var corridors = new List<CorridorSegment>();
        if (p.Count <= 1 || edges.Count == 0) { return corridors; }

        // DRY cells only. The cell graph spans the seabed as readily as the
        // land, so a spanning set over all of it earns ramps between two
        // submerged cells and — worse — down the shelf from the coast into the
        // ocean, a cutting nobody can ever walk. Dropping them turns the tree
        // into a spanning FOREST, which Kruskal produces without any change:
        // each landmass gets its own connected network, which is also what the
        // offshore islands need (they are not reachable on foot at all, and a
        // ramp bridging one to the seabed would be a lie about that).
        //
        // Flat is in field units with 0 at the waterline, and a column whose top
        // voxel sits exactly at WATER_LEVEL is dry shoreline by the rule the
        // rest of worldgen uses — so `< 0` is the submerged test.
        var sorted = new List<CellEdge>();
        int submergedSkipped = 0;
        foreach (CellEdge edge in edges)
        {
            if (p.Flat[edge.A] < 0f || p.Flat[edge.B] < 0f) { submergedSkipped++; continue; }
            CellEdge e = edge;
            float dx = p.CentroidX[e.A] - p.CentroidX[e.B];
            float dz = p.CentroidZ[e.A] - p.CentroidZ[e.B];
            e.Weight = Math.Abs(p.Flat[e.A] - p.Flat[e.B])
                + Mathf.Sqrt(dx * dx + dz * dz) / Math.Max(1f, cd.cellSizeMeters);
            sorted.Add(e);
        }
        if (sorted.Count == 0) { return corridors; }
        // Tie-broken on the cell pair so the network is identical for a given
        // seed regardless of how the edge list happened to be ordered.
        sorted.Sort((a, b) =>
        {
            int c = a.Weight.CompareTo(b.Weight);
            if (c != 0) { return c; }
            c = a.A.CompareTo(b.A);
            return c != 0 ? c : a.B.CompareTo(b.B);
        });

        var parent = new int[p.Count];
        for (int i = 0; i < parent.Length; i++) { parent[i] = i; }
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        var chosen = new List<CellEdge>();
        var spare = new List<CellEdge>();
        foreach (CellEdge e in sorted)
        {
            int ra = Find(e.A);
            int rb = Find(e.B);
            if (ra != rb)
            {
                parent[ra] = rb;
                chosen.Add(e);
            }
            else
            {
                spare.Add(e);
            }
        }
        // Extra edges are drawn ONLY from spares that have a wall to climb, and
        // that is the whole point of them. The tree minimises total climbing, so
        // left to itself it threads the flat ground and routes around every
        // scarp — a correct spanning set that produces almost no ramps, and a
        // world where each terrace has exactly one way up, often far away. These
        // are what put a way up the walls themselves; cheapest-first among them
        // means the shortest walls get the ramps, which is where a cutting is
        // least intrusive.
        int budget = Mathf.RoundToInt(Math.Max(0f, cd.extraEdgeFraction) * p.Count);
        for (int i = 0; i < spare.Count && budget > 0; i++)
        {
            if (Math.Abs(p.Flat[spare[i].A] - p.Flat[spare[i].B]) < cd.rampMinDrop) { continue; }
            chosen.Add(spare[i]);
            budget--;
        }

        float grade = Math.Clamp(cd.rampGrade, 0.05f, 1f);
        float widthMin = Math.Max(1f, Math.Min(cd.rampWidthMinMeters, cd.rampWidthMaxMeters));
        float widthMax = Math.Max(widthMin, cd.rampWidthMaxMeters);
        int ei = 0;
        foreach (CellEdge e in chosen)
        {
            // A wall the player can already step or jump up gets no cutting —
            // a ramp there is clutter, and at one step it is shorter than it is
            // wide.
            float drop = Math.Abs(p.Flat[e.A] - p.Flat[e.B]);
            if (drop < cd.rampMinDrop) { ei++; continue; }

            // Never cut into a stamped cell. The whole point of stamping the
            // village is that it is ONE elevation; a ramp gouged across it puts
            // a slope back through the middle of the authored buildings.
            if (p.Pinned[e.A] || p.Pinned[e.B]) { ei++; continue; }

            float dx = p.CentroidX[e.B] - p.CentroidX[e.A];
            float dz = p.CentroidZ[e.B] - p.CentroidZ[e.A];
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 0.001f) { ei++; continue; }
            dx /= len;
            dz /= len;

            // Width is rolled per ramp from a hash of the cell pair, so it is
            // stable for a seed and independent of the order edges were visited.
            float t = (Hash(e.A, e.B, worldSeed) & 0xFFFF) / 65535f;
            float width = Mathf.Lerp(widthMin, widthMax, t);

            // Length follows from the drop and the steepest grade allowed, so
            // lowering rampGrade lengthens ramps rather than steepening them.
            // Re-derived at cut time against the FINAL heights; this one only
            // has to be long enough to pick the right cells to refine.
            float half = Math.Clamp(drop / (2f * grade), width, cd.rampMaxHalfLength);
            corridors.Add(new CorridorSegment
            {
                Cx = e.BorderX,
                Cz = e.BorderZ,
                Dx = dx,
                Dz = dz,
                Half = half,
                HalfWidth = width * 0.5f,
            });
            ei++;
        }
        GD.Print($"[CellularTerrain] ramps: {corridors.Count} cut from {sorted.Count} dry cell edges"
            + $" ({submergedSkipped} submerged edges skipped)");
        return corridors;
    }

    // Mark every cell a ramp passes through, so it subdivides to the ramp's own
    // width. Without it the cutting crosses one huge terrace and its shoulders
    // are a single wall the length of the ramp; with it the ground either side
    // steps down alongside.
    private static void MarkCorridorCells(CellPartition p, List<CorridorSegment> corridors,
        int worldMinX, int worldMinZ, bool[] promote)
    {
        foreach (CorridorSegment r in corridors)
        {
            // Only a little wider than the tread. This band is multiplied by
            // every ramp in the network, and a cell is refined if ANY of its
            // columns falls inside one — measured, a 3x band over 78 ramps
            // covered the island several times over and took the partition from
            // 137 cells to 3425, which is how a 72 m world ended up with 10 m
            // terraces everywhere.
            ForEachCorridorColumn(r, r.Half, r.HalfWidth * 1.5f, p.Index.GetLength(0), p.Index.GetLength(1),
                worldMinX, worldMinZ, (lx, lz, t) => { promote[p.Index[lx, lz]] = true; });
        }
    }

    // Cut each ramp into the flat field. Returns a parallel field holding the
    // ramp height per column, NaN where no ramp reaches — a sentinel rather than
    // a weight, because a ramp is not blended with the terrace it cuts through:
    // its edges are walls like any other, which is what keeps the world's
    // vocabulary to flat ground and walls plus these cuttings.
    //
    // Where two ramps overlap the LOWER one wins, so a junction resolves to a
    // single surface instead of one cutting hanging over the other.
    // A corridor whose FINISHED footprint touches a landform is skipped whole.
    // Skipping it whole and not per column is the point: a ramp cut everywhere
    // except across a mesa is a cutting with a wall through the middle of it,
    // which is worse than no ramp at all.
    private static float[,] CutRamps(List<CorridorSegment> corridors, float[,] flat,
        bool[,] noRampColumns, CellularTerrainData cd, int worldMinX, int worldMinZ)
    {
        int sizeX = flat.GetLength(0);
        int sizeZ = flat.GetLength(1);
        var ramp = new float[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++) { ramp[lx, lz] = float.NaN; }
        }
        float grade = Math.Clamp(cd.rampGrade, 0.05f, 1f);
        int cut = 0;
        int refusedByLandform = 0;

        foreach (CorridorSegment r in corridors)
        {
            // Endpoint heights come from the FINISHED flat field, by growing the
            // ramp outward until it is long enough to carry the drop it finds at
            // the authored grade. Deriving the length from the drop measured
            // BEFORE refinement does not work: subdividing the corridor
            // re-terraces the border, so a probe at the old length lands on an
            // intermediate terrace, reads almost no drop, and the ramp is
            // skipped as unnecessary — which is what left the network with a
            // handful of ramps instead of one per wall.
            float half = r.HalfWidth;
            float h0 = 0f;
            float h1 = 0f;
            for (float probe = r.HalfWidth; ; probe += RAMP_PROBE_STEP)
            {
                half = Math.Min(probe, cd.rampMaxHalfLength);
                h0 = SampleFlat(flat, r.Cx - r.Dx * half, r.Cz - r.Dz * half, worldMinX, worldMinZ);
                h1 = SampleFlat(flat, r.Cx + r.Dx * half, r.Cz + r.Dz * half, worldMinX, worldMinZ);
                if (Math.Abs(h1 - h0) <= 2f * half * grade || half >= cd.rampMaxHalfLength) { break; }
            }
            if (Math.Abs(h1 - h0) < cd.quantizeStep) { continue; }

            // The length is only known now, so this is the first point the
            // landform test can be exact rather than conservative.
            bool onLandform = false;
            ForEachCorridorColumn(r, half, r.HalfWidth, sizeX, sizeZ, worldMinX, worldMinZ,
                (lx, lz, t) => { if (noRampColumns[lx, lz]) { onLandform = true; } });
            if (onLandform) { refusedByLandform++; continue; }

            float[,] outRamp = ramp;
            ForEachCorridorColumn(r, half, r.HalfWidth, sizeX, sizeZ, worldMinX, worldMinZ, (lx, lz, t) =>
            {
                float h = Mathf.Lerp(h0, h1, t);
                float cur = outRamp[lx, lz];
                if (float.IsNaN(cur) || h < cur) { outRamp[lx, lz] = h; }
            });
            cut++;
        }
        GD.Print($"[CellularTerrain] ramps cut: {cut} of {corridors.Count} corridors"
            + $" ({refusedByLandform} refused for running into a landform)");
        return ramp;
    }

    // Walk the columns inside one corridor, handing back each column's position
    // along it (t in 0..1). Bounded by the segment's bbox so cost scales with
    // corridor area rather than with the world.
    private static void ForEachCorridorColumn(CorridorSegment r, float half, float halfWidth,
        int sizeX, int sizeZ, int worldMinX, int worldMinZ, Action<int, int, float> visit)
    {
        float x0 = r.Cx - r.Dx * half;
        float z0 = r.Cz - r.Dz * half;
        float x1 = r.Cx + r.Dx * half;
        float z1 = r.Cz + r.Dz * half;
        float ex = x1 - x0;
        float ez = z1 - z0;
        float len2 = ex * ex + ez * ez;
        if (len2 < 0.0001f) { return; }
        float radius = halfWidth;

        int loX = Math.Max(0, Mathf.FloorToInt(Math.Min(x0, x1) - radius) - worldMinX);
        int hiX = Math.Min(sizeX - 1, Mathf.CeilToInt(Math.Max(x0, x1) + radius) - worldMinX);
        int loZ = Math.Max(0, Mathf.FloorToInt(Math.Min(z0, z1) - radius) - worldMinZ);
        int hiZ = Math.Min(sizeZ - 1, Mathf.CeilToInt(Math.Max(z0, z1) + radius) - worldMinZ);

        for (int lx = loX; lx <= hiX; lx++)
        {
            for (int lz = loZ; lz <= hiZ; lz++)
            {
                float px = lx + worldMinX + 0.5f - x0;
                float pz = lz + worldMinZ + 0.5f - z0;
                float t = (px * ex + pz * ez) / len2;
                if (t < 0f || t > 1f) { continue; }
                float qx = px - t * ex;
                float qz = pz - t * ez;
                float perp = Mathf.Sqrt(qx * qx + qz * qz);
                if (perp > radius) { continue; }
                visit(lx, lz, t);
            }
        }
    }

    // Horizontal reach, in columns, of the slope the incision term measures.
    // Wider than one column so it reads the regional grade rather than the
    // roughness riding on it.
    private const int SLOPE_STENCIL = 3;

    // Cut the drainage network into the field and return how deep each column
    // was incised. Single-flow-direction (steepest of 8) over columns visited in
    // descending height order — that order is what makes one pass sufficient,
    // since every contributor to a column is processed before the column itself.
    // No depression filling: flow entering a local minimum stops there, which
    // costs some accumulation in pitted ground and saves a priority flood. This
    // pass only SHAPES valleys for the cells to terrace; the water in them is
    // BuildWaterways' business, and that one does fill its depressions.
    //
    // Incision is proportional to flow AND to local grade — the stream-power
    // form. The grade term is not a refinement, it is what keeps the pass
    // honest: flow accumulates enormously across a wide flat basin, so a
    // flow-only rule lowers the whole basin by the full depth and, near sea
    // level, simply drowns it. Water cuts where it runs fast; on the flat it
    // deposits instead. Logarithmic in flow because flow is heavy-tailed — a
    // linear map puts the entire visible effect in the few trunk channels.
    private static float[,] CarveDrainage(float[,] field, float[,] rain, CellularTerrainData cd)
    {
        int sizeX = field.GetLength(0);
        int sizeZ = field.GetLength(1);
        var carved = new float[sizeX, sizeZ];
        if (cd.drainageCarveDepth <= 0f) { return carved; }

        int count = sizeX * sizeZ;
        var order = new int[count];
        var keys = new float[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
            keys[i] = -field[i / sizeZ, i % sizeZ];
        }
        Array.Sort(keys, order);

        var flow = new float[count];
        for (int i = 0; i < count; i++) { flow[i] = rain[i / sizeZ, i % sizeZ]; }
        for (int i = 0; i < count; i++)
        {
            int idx = order[i];
            int lx = idx / sizeZ;
            int lz = idx % sizeZ;
            float h = field[lx, lz];

            int bestIdx = -1;
            float bestHeight = h;
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = lx + dx;
                if (nx < 0 || nx >= sizeX) { continue; }
                for (int dz = -1; dz <= 1; dz++)
                {
                    int nz = lz + dz;
                    if ((dx == 0 && dz == 0) || nz < 0 || nz >= sizeZ) { continue; }
                    float nh = field[nx, nz];
                    if (nh < bestHeight)
                    {
                        bestHeight = nh;
                        bestIdx = nx * sizeZ + nz;
                    }
                }
            }
            if (bestIdx >= 0) { flow[bestIdx] += flow[idx]; }
        }

        float logReference = Mathf.Log(Math.Max(2f, cd.drainageFlowReference));
        float slopeReference = Math.Max(0.001f, cd.drainageSlopeReference);
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                float h = field[lx, lz];
                float grade = Math.Max(
                    Math.Max(Math.Abs(FieldAt(field, lx - SLOPE_STENCIL, lz) - h),
                             Math.Abs(FieldAt(field, lx + SLOPE_STENCIL, lz) - h)),
                    Math.Max(Math.Abs(FieldAt(field, lx, lz - SLOPE_STENCIL) - h),
                             Math.Abs(FieldAt(field, lx, lz + SLOPE_STENCIL) - h))) / SLOPE_STENCIL;

                // A CHANNEL threshold, and it is the important one. Without it
                // every column carries flow >= 1 and therefore carves a little,
                // so the pass behaves as a general lowering rather than a river
                // network — and since all of an island's flow converges on its
                // coast, where the shelf also happens to be the steepest ground
                // in the world, the heaviest flow met the steepest grade and
                // shredded the whole shoreline into radial gullies. Requiring a
                // real catchment first confines incision to the trunk channels,
                // which is where the gorges are wanted.
                float catchment = flow[lx * sizeZ + lz];
                if (catchment < cd.drainageMinFlow) { continue; }

                float power = Math.Clamp(Mathf.Log(catchment) / logReference, 0f, 1f)
                            * Math.Clamp(grade / slopeReference, 0f, 1f);
                carved[lx, lz] = cd.drainageCarveDepth * power;
            }
        }
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++) { field[lx, lz] -= carved[lx, lz]; }
        }
        return carved;
    }

    // Clamped read — edge columns compare against themselves, so the slope at
    // the world border reads as flat rather than sampling out of bounds.
    private static float FieldAt(float[,] field, int lx, int lz)
    {
        lx = Math.Clamp(lx, 0, field.GetLength(0) - 1);
        lz = Math.Clamp(lz, 0, field.GetLength(1) - 1);
        return field[lx, lz];
    }

    private static float Hash01(long key, int seed)
    {
        return (Hash((int)key, (int)(key >> 32), seed) & 0xFFFF) / 65535f;
    }

    private static float SampleFlat(float[,] flat, float wx, float wz, int worldMinX, int worldMinZ)
    {
        int lx = Math.Clamp(Mathf.FloorToInt(wx) - worldMinX, 0, flat.GetLength(0) - 1);
        int lz = Math.Clamp(Mathf.FloorToInt(wz) - worldMinZ, 0, flat.GetLength(1) - 1);
        return flat[lx, lz];
    }

    // ------------------------------------------------------- rivers and lakes

    // Humidity assumed for a zone that authors no WeatherData — the middle of
    // the range, so a missing asset sheds ordinary rain rather than turning a
    // whole quadrant into desert or swamp.
    private const float DEFAULT_HUMIDITY = 0.5f;

    // Rain is a per-column WEIGHT, not an absolute: scale it so the mean over
    // land is 1 and the two accumulation passes keep working in units of
    // "contributing columns" however the humidity weight is authored. Sea
    // columns are excluded from the mean but keep their weight — they are the
    // outlet, so what they shed never reaches a channel anyway.
    private static void NormaliseRainOverLand(float[,] rain, float[,] field)
    {
        int sizeX = rain.GetLength(0);
        int sizeZ = rain.GetLength(1);
        double sum = 0;
        int n = 0;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                if (field[lx, lz] <= 0f) { continue; }
                sum += rain[lx, lz];
                n++;
            }
        }
        if (n == 0 || sum <= 0) { return; }
        float scale = (float)(n / sum);
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++) { rain[lx, lz] *= scale; }
        }
    }

    // Neighbour offsets for everything in this section: FOUR-connected, never
    // eight. Water routed diagonally leaks through the corner between two
    // voxels that share no face — it drains basins that have no outlet and
    // leaves rivers as broken diagonal chains of single voxels.
    private static readonly int[] NEIGHBOUR_DX = { 1, -1, 0, 0 };
    private static readonly int[] NEIGHBOUR_DZ = { 0, 0, 1, -1 };

    // Pass 6 — cut rivers and flood lakes into the finished terraces, and hand
    // back the per-column water surface (HeightMap.NoWater where there is none).
    //
    // ONE algorithm produces all of it. A priority flood from the sea gives, per
    // column, the level water would stand at if it had to escape to the ocean —
    // equal to the ground on open slopes, and raised to the spill height inside
    // any depression. Rain accumulated down the same flood's drainage tree gives
    // the catchment. Then:
    //
    //   filled == ground, enough flow   -> a CHANNEL: dig riverDepth into the
    //                                      terrace and stand water at the
    //                                      terrace's own level.
    //   filled >  ground, enough flow   -> a LAKE: leave the ground alone and
    //                                      fill to the spill level, which raises
    //                                      a new flat water terrace over it.
    //
    // The two are the same rule at a wall: the fill pools behind the crest, the
    // crest column itself is a channel notched through it, and the surface on
    // the far side drops a whole lattice step or more — a cascade between pools,
    // with no sloped water anywhere.
    //
    // A river MAY cross a ramp, and the ramp gets no say. The alternative —
    // sparing ramp columns to keep the only sloped ground in the world intact —
    // breaks the channel for its width at every crossing, and a river with holes
    // in it is worse than a route with a stream across it. What the crossing
    // leaves is a notch the ramp dips through, which is a ford; the road pass
    // grades and beds its own tread over it (see WorldGenData.roadFordMaxDepth),
    // so a road that actually uses that route comes out as a causeway.
    private int[,] BuildWaterways(int[,] height, int[,] plateau, float[,] field, float[,] rain,
        bool[,] pinned, bool[,] onRamp, CellularTerrainData cd, int worldMinX, int worldMinZ,
        out Vector2[,] current, out List<WaterfallSite> waterfalls)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        current = null;
        waterfalls = null;
        if (cd.riverMinFlow <= 0f) { return null; }

        int count = sizeX * sizeZ;
        int step = Math.Max(1, cd.quantizeStep);
        // Rounded UP to a whole step so a carved bed stays on the lattice the
        // rest of the world's land sits on.
        int depth = Math.Max(1, Mathf.CeilToInt(cd.riverDepth / (float)step)) * step;

        // Composite elevation: the integer terrace height plus a fraction that
        // ORDERS columns inside one terrace by the continuous field they came
        // from. Without it the flood and the flow tree have nothing to go on
        // across the world's large flat tops — every neighbour ties, the queue
        // resolves it by insertion order, and the channels come out as raster
        // artefacts fanning off the terrace edge. With it a river crossing a
        // terrace follows the slope the terracing rounded away.
        float fieldMin = float.MaxValue;
        float fieldMax = float.MinValue;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                float v = field[lx, lz];
                if (v < fieldMin) { fieldMin = v; }
                if (v > fieldMax) { fieldMax = v; }
            }
        }
        float fieldSpan = Math.Max(0.001f, fieldMax - fieldMin);
        var elevation = new float[count];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                elevation[lx * sizeZ + lz] = height[lx, lz]
                    + 0.999f * (field[lx, lz] - fieldMin) / fieldSpan;
            }
        }

        var filled = new float[count];
        var receiver = new int[count];
        var order = new int[count];
        var flow = new float[count];
        var surfaceY = new int[count];
        var spill = new int[count];
        var stats = new WaterStats();
        var lakes = new List<LakeBasin>();

        // ---- fill, judge, BREACH, repeat. A sink either becomes a lake or gets
        // its outlet notched down until it drains; both are real landforms and
        // the lake test decides which. Abandoning a rejected sink is not an
        // option: water entering one has nowhere else to go, so the river simply
        // stopped at its rim and the network came out in pieces.
        //
        // Breaching CHANGES the terrain, so the fill has to be redone against it
        // — hence a loop rather than one pass. It converges quickly (a breach
        // strictly lowers ground and can only merge sinks), and the cap is a
        // runaway guard, not a tuning knob.
        bool settled = false;
        for (int pass = 0; pass < MAX_BREACH_PASSES && !settled; pass++)
        {
            FloodAndAccumulate(height, elevation, rain, filled, receiver, order, flow, surfaceY,
                spill, step);
            lakes.Clear();
            settled = !ResolveBasins(height, plateau, elevation, field, receiver, surfaceY, flow, spill,
                pinned, cd, step, lakes, stats, fieldMin, fieldSpan, allowBreach: true);
            if (!settled) { stats.BreachPasses++; }
        }
        if (!settled)
        {
            // Ran out of passes. Everything below reads the fill and the flow,
            // and the last breach invalidated both, so re-fill once and take the
            // lakes from THAT — with breaching off, so this cannot loop.
            stats.Unsettled = true;
            FloodAndAccumulate(height, elevation, rain, filled, receiver, order, flow, surfaceY,
                spill, step);
            lakes.Clear();
            ResolveBasins(height, plateau, elevation, field, receiver, surfaceY, flow, spill,
                pinned, cd, step, lakes, stats, fieldMin, fieldSpan, allowBreach: false);
        }

        var water = new int[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++) { water[lx, lz] = HeightMap.NoWater; }
        }

        // Which columns a basin flooded, kept for the current pass below: a lake
        // is the one water body whose surface does NOT move at its catchment's
        // speed, and after the stamp a lake column and a channel column are
        // indistinguishable from `water` alone.
        var isLake = new bool[sizeX, sizeZ];
        foreach (LakeBasin lake in lakes)
        {
            foreach (int idx in lake.Columns)
            {
                int lx = idx / sizeZ;
                int lz = idx % sizeZ;
                if (onRamp[lx, lz]) { stats.RampCrossings++; }
                water[lx, lz] = lake.Level;
                isLake[lx, lz] = true;
                stats.LakeColumns++;
                stats.WaterColumns++;
            }
            stats.Lakes++;
            if (lake.Depth > stats.DeepestLake) { stats.DeepestLake = lake.Depth; }
        }

        // The width wander. A THIRD field of its own rather than a reuse of the
        // erosion noise, for the reason spelled out there: a shared field
        // correlates two decisions that have nothing to do with each other.
        var widthNoise = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_RIVER_WIDTH),
            cd.riverWidthNoiseFrequency, 2);

        CutRiverChannels(height, plateau, water, surfaceY, flow, pinned, onRamp, cd, depth, stats,
            widthNoise, worldMinX, worldMinZ);
        // The fall pass writes nothing into the world; it reports where the
        // cascades are so an effect can be placed at each. Its scratch starts as
        // a copy of the finished water field.
        var sheet = (int[,])water.Clone();
        StandWaterfalls(height, water, cd, stats, sheet);
        waterfalls = BuildWaterfallSites(sheet, water, height, worldMinX, worldMinZ, stats);

        current = BuildSurfaceCurrents(water, isLake, receiver, flow, cd, sizeX, sizeZ, stats);
        stats.Report(cd, count);
        return stats.WaterColumns > 0 ? water : null;
    }

    // Breach passes allowed before the pass gives up and leaves whatever sinks
    // remain (it says so in the log when it does). Each one strictly lowers
    // ground and can only merge sinks, so this is a runaway guard rather than a
    // tuning knob — the default world settles in seven.
    private const int MAX_BREACH_PASSES = 24;

    // One accepted lake: the columns it covers and the lattice level it stands
    // at. Collected rather than stamped immediately because a later breach pass
    // can re-shape the basin it came from.
    private sealed class LakeBasin
    {
        public int Level;
        public int Depth;
        public List<int> Columns;
    }

    // Priority flood (Barnes et al.) from the sea, then rain accumulated back
    // down the tree it produces.
    //
    // Pop the lowest unvisited column and raise each neighbour to at least the
    // level just popped. The order the queue hands columns back is exactly
    // "outward from the sea, uphill", so the neighbour that first reaches a
    // column is its downstream receiver and the pop order REVERSED is a valid
    // accumulation order — every contributor is processed before what it drains
    // into, with no second sort.
    //
    // Ties break on the column's OWN elevation first, and only then on its
    // index. That secondary key is what keeps rivers off the raster. Across a
    // large flat terrace the filled level saturates at the running maximum, so
    // every frontier column ties on it; ordering those by index made the queue
    // spread in scan order and the trunk river came out as axis-aligned
    // straight lines and right-angle corners across the central plain.
    // Ordering them by the underlying continuous field instead makes a column's
    // first-reaching neighbour its lowest-field neighbour, so the channel
    // follows the valley the terracing rounded away. The index stays as the
    // last key: without a total order two identical columns pop in an order the
    // queue does not define and the world stops reproducing.
    private static void FloodAndAccumulate(int[,] height, float[] elevation, float[,] rain,
        float[] filled, int[] receiver, int[] order, float[] flow, int[] surfaceY, int[] spill, int step)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int count = sizeX * sizeZ;
        var visited = new bool[count];
        int popped = 0;
        var open = new PriorityQueue<int, (float, float, int)>();

        // Outlets: genuinely submerged columns, plus the world border. STRICTLY
        // below the waterline, matching the rest of worldgen's "a column whose
        // top voxel sits exactly at WATER_LEVEL is dry shoreline" rule. Seeding
        // at <= instead made all 9000-odd columns of the y=0 coastal flat their
        // own outlet, so every river terminated the moment it reached low ground
        // and the network came out as disconnected fragments never meeting the
        // sea.
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                bool border = lx == 0 || lz == 0 || lx == sizeX - 1 || lz == sizeZ - 1;
                if (!border && height[lx, lz] >= WorldGen.WATER_LEVEL) { continue; }
                int idx = lx * sizeZ + lz;
                visited[idx] = true;
                receiver[idx] = -1;
                filled[idx] = elevation[idx];
                open.Enqueue(idx, (filled[idx], elevation[idx], idx));
            }
        }

        while (open.TryDequeue(out int cur, out _))
        {
            order[popped++] = cur;
            int lx = cur / sizeZ;
            int lz = cur % sizeZ;
            for (int d = 0; d < 4; d++)
            {
                int nx = lx + NEIGHBOUR_DX[d];
                int nz = lz + NEIGHBOUR_DZ[d];
                if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                int nIdx = nx * sizeZ + nz;
                if (visited[nIdx]) { continue; }
                visited[nIdx] = true;
                filled[nIdx] = Math.Max(elevation[nIdx], filled[cur]);
                receiver[nIdx] = cur;
                open.Enqueue(nIdx, (filled[nIdx], elevation[nIdx], nIdx));
            }
        }

        for (int i = 0; i < count; i++) { flow[i] = rain[i / sizeZ, i % sizeZ]; }
        for (int i = popped - 1; i >= 0; i--)
        {
            int idx = order[i];
            int r = receiver[idx];
            if (r >= 0) { flow[r] += flow[idx]; }
        }

        // Each column's CONSTRICTION: the column upstream of the sea that last
        // raised the fill, i.e. the rim a pool here would be standing behind.
        // Read straight off the outward walk — a column's constriction is its
        // own position if the fill had to climb to reach it, and otherwise
        // whatever its downstream neighbour was already held back by. That
        // makes every pool's spill point exact and free, where searching a
        // finished pool for its lowest rim cannot tell the true saddle from an
        // interior bump of the same height (which is what made an earlier
        // version cut bumps in the middle of a basin and never drain it).
        for (int i = 0; i < popped; i++)
        {
            int idx = order[i];
            int r = receiver[idx];
            spill[idx] = r < 0 ? -1
                : filled[idx] > filled[r] + 0.0001f ? idx
                : spill[r];
        }

        // The water surface each column would carry, on the lattice. Rounded
        // DOWN, never up: a surface above its own spill point would pour out of
        // the basin it is meant to sit in.
        for (int i = 0; i < count; i++)
        {
            surfaceY[i] = LatticeFloor(Mathf.FloorToInt(filled[i]), step);
        }
    }

    // Counters for the one line this pass logs. Every rejection it can make is
    // counted rather than silent: a world with no rivers should say WHY (no
    // catchment clears the threshold / every basin was too small / the village
    // vetoed them), because the alternative is tuning three knobs blind.
    private sealed class WaterStats
    {
        public int WaterColumns;
        public int RiverColumns;
        public int Lakes;
        public int LakeColumns;
        public int LakesTooSmall;
        public int LakesTooShallow;
        public int LakesTooDry;
        public int LakesTooBig;
        public int LakesOnPinned;
        public int PinnedCrossings;
        public int RampCrossings;
        public int BankRejects;
        public int DropRejects;
        public int DeepestLake;
        public int BasinsSeen;
        public int BreachPasses;
        public int Breaches;
        public int BreachColumns;
        public int BreachesBlocked;
        public bool Unsettled;
        public int BasinColumns;
        public int FallColumns;
        public int TallestFall;
        public int CurrentColumns;
        public float FastestCurrent;
        public float NoiseLo = float.MaxValue;
        public float NoiseHi = float.MinValue;
        public float NarrowestHalf = float.MaxValue;
        public float WidestHalf;

        // The sink census describes the finished world, so it is rebuilt on each
        // breach pass rather than accumulated — summed, it would report sinks
        // that no longer exist. The level VETOES are the opposite: they are the
        // record of why no lake formed, which is the whole diagnostic, so they
        // accumulate over the passes like the breach totals do.
        public void ResetSinkCounters()
        {
            BasinsSeen = 0;
            BasinColumns = 0;
        }

        public void Report(CellularTerrainData cd, int worldColumns)
        {
            GD.Print($"[CellularTerrain] water: {WaterColumns} columns"
                + $" ({100.0 * WaterColumns / Math.Max(1, worldColumns):F1}% of world)"
                + $" = {RiverColumns} river + {LakeColumns} lake in {Lakes} lakes"
                + $" (deepest {DeepestLake}v); level vetoes:"
                + $" {LakesTooDry} dry (<{cd.lakeMinFlow:F0} flow),"
                + $" {LakesTooSmall} small (<{cd.lakeMinColumns} cols),"
                + $" {LakesTooShallow} shallow (<{cd.lakeMinDepth}v),"
                + $" {LakesTooBig} oversized, {LakesOnPinned} on pinned ground;"
                + $" {PinnedCrossings} columns crossing pinned ground,"
                + $" {BankRejects} refused as bank (>{cd.riverBankCut}v above water),"
                + $" {DropRejects} refused below a drop (>{cd.riverDepth}v under water),"
                + $" {RampCrossings} ramp crossings;"
                + $" {FallColumns} waterfall columns (tallest {TallestFall}v);"
                + $" {CurrentColumns} columns carrying a current (fastest {FastestCurrent:F2},"
                + $" width noise {cd.riverWidthNoise:F2} @ {cd.riverWidthNoiseFrequency:F4}"
                + $" spanning {NoiseLo:F2}..{NoiseHi:F2}, half-width {NarrowestHalf:F2}..{WidestHalf:F2});"
                + $" {BasinsSeen} sinks left, totalling {BasinColumns} columns,"
                + $" {Breaches} breaches cutting {BreachColumns} columns over {BreachPasses} passes"
                + $" ({BreachesBlocked} stopped by pinned ground)"
                + (Unsettled ? " — DID NOT SETTLE, sinks remain" : ""));
        }
    }

    // Decide what each pool the fill produced becomes: a lake, or a breach.
    //
    // Pools come straight off the constriction channel — every column held back
    // by the same rim column IS one pool, at one level, and that rim column is
    // where a breach has to cut. No search, and no risk of mistaking a bump in
    // the middle of a basin for its rim.
    //
    // A pool that passes the lake tests at its spill height is collected as a
    // lake. One that does not is BREACHED at the highest level that WOULD pass:
    // its rim is notched down, the water above that level drains away, and
    // whatever stays below it is a lake on the next pass. Notch to the floor and
    // the pool is simply gone — which is the honest answer for a flat-floored
    // terrace, since it has no deepest part to keep water in. That is what turns
    // this world's one huge shallow bowl (the central plain, 6752 columns,
    // uniformly two voxels deep, with the village in it) into a river gorge out
    // to the sea instead of an inland sea.
    //
    // Pinned ground (the stamped village) vetoes a LEVEL for the whole pool
    // rather than being punched out of it: a lake with a dry island of buildings
    // in the middle is worse than no lake, and the village is the authored
    // thing. It only ever forces the level down, so it can never flood.
    //
    // Returns true if it changed the terrain, meaning the fill is stale and the
    // caller must run it again.
    private static bool ResolveBasins(int[,] height, int[,] plateau, float[] elevation, float[,] field,
        int[] receiver, int[] surfaceY, float[] flow, int[] spill, bool[,] pinned,
        CellularTerrainData cd, int step, List<LakeBasin> lakes, WaterStats stats,
        float fieldMin, float fieldSpan, bool allowBreach)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int maxColumns = Mathf.RoundToInt(Math.Clamp(cd.lakeMaxWorldFraction, 0.001f, 1f) * sizeX * sizeZ);
        bool changed = false;
        // Reset per call: these describe the world as it stands NOW, and summing
        // them over the breach passes reports sinks that no longer exist.
        stats.ResetSinkCounters();

        var pools = new Dictionary<int, List<int>>();
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                int idx = lx * sizeZ + lz;
                int rim = spill[idx];
                if (rim < 0) { continue; }
                if (surfaceY[idx] <= height[lx, lz] || height[lx, lz] < WorldGen.WATER_LEVEL) { continue; }
                if (!pools.TryGetValue(rim, out List<int> columns))
                {
                    columns = new List<int>();
                    pools[rim] = columns;
                }
                columns.Add(idx);
            }
        }

        // Ordered by rim column so a run is reproducible: the dictionary's own
        // order is not defined, and a breach mutates ground the next pool's
        // tests read.
        var rims = new List<int>(pools.Keys);
        rims.Sort();
        foreach (int rim in rims)
        {
            List<int> region = pools[rim];
            int spillY = surfaceY[region[0]];
            float best = 0f;
            int floor = int.MaxValue;
            foreach (int idx in region)
            {
                if (flow[idx] > best) { best = flow[idx]; }
                int h = height[idx / sizeZ, idx % sizeZ];
                if (h < floor) { floor = h; }
            }

            stats.BasinsSeen++;
            stats.BasinColumns += region.Count;

            // Highest lattice level this pool may stand at, walking DOWN from
            // the spill, so a pool that is merely too big still keeps a lake in
            // its deepest part rather than losing all its water.
            int level = floor;
            bool dry = best < cd.lakeMinFlow;
            for (int candidate = spillY; candidate > floor && !dry; candidate -= step)
            {
                int columns = 0;
                int deepest = 0;
                bool hitsPinned = false;
                foreach (int idx in region)
                {
                    int lx = idx / sizeZ;
                    int lz = idx % sizeZ;
                    if (height[lx, lz] >= candidate) { continue; }
                    columns++;
                    if (pinned[lx, lz]) { hitsPinned = true; break; }
                    int d = candidate - height[lx, lz];
                    if (d > deepest) { deepest = d; }
                }
                if (hitsPinned) { stats.LakesOnPinned++; continue; }
                if (columns > maxColumns) { stats.LakesTooBig++; continue; }
                if (columns < cd.lakeMinColumns) { stats.LakesTooSmall++; continue; }
                if (deepest < cd.lakeMinDepth) { stats.LakesTooShallow++; continue; }
                level = candidate;
                break;
            }
            if (dry) { stats.LakesTooDry++; }

            if (level >= spillY)
            {
                int deepest = 0;
                foreach (int idx in region)
                {
                    int d = level - height[idx / sizeZ, idx % sizeZ];
                    if (d > deepest) { deepest = d; }
                }
                lakes.Add(new LakeBasin { Level = level, Depth = deepest, Columns = region });
                continue;
            }

            if (allowBreach && BreachOutlet(height, plateau, elevation, field, receiver, rim, level,
                    pinned, cd, fieldMin, fieldSpan, stats))
            {
                stats.Breaches++;
                changed = true;
            }
        }
        return changed;
    }

    // Cut the outlet of a sink down to `level`, following the fill's own
    // receiver line off the rim — the line water would take — until the ground
    // is already at or below the target. A short band either side so the result
    // is a gorge a river can occupy rather than a one-column slot.
    //
    // Pinned ground stops the cut dead: a breach through the village is worse
    // than an undrained sink, and this is the one place the two can collide.
    private static bool BreachOutlet(int[,] height, int[,] plateau, float[] elevation, float[,] field,
        int[] receiver, int exit, int level, bool[,] pinned, CellularTerrainData cd,
        float fieldMin, float fieldSpan, WaterStats stats)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int half = Math.Max(0, Mathf.FloorToInt(cd.riverHalfWidthMin));
        int idx = exit;
        bool cut = false;
        // Bounded by the world's diagonal — the receiver line is loop-free by
        // construction, so this is a guard and not an expected exit.
        for (int guard = 0; guard < sizeX + sizeZ; guard++)
        {
            int lx = idx / sizeZ;
            int lz = idx % sizeZ;
            if (height[lx, lz] <= level) { break; }
            for (int dx = -half; dx <= half; dx++)
            {
                int nx = lx + dx;
                if (nx < 0 || nx >= sizeX) { continue; }
                for (int dz = -half; dz <= half; dz++)
                {
                    int nz = lz + dz;
                    if (nz < 0 || nz >= sizeZ) { continue; }
                    if (dx * dx + dz * dz > half * half) { continue; }
                    if (pinned[nx, nz]) { continue; }
                    if (height[nx, nz] <= level) { continue; }
                    height[nx, nz] = level;
                    plateau[nx, nz] = Math.Min(plateau[nx, nz], level);
                    elevation[nx * sizeZ + nz] = level
                        + 0.999f * (field[nx, nz] - fieldMin) / fieldSpan;
                    stats.BreachColumns++;
                    cut = true;
                }
            }
            if (pinned[lx, lz])
            {
                stats.BreachesBlocked++;
                GD.Print("[CellularTerrain] breach stopped at pinned ground; that sink stays undrained"
                    + " and the river above it dead-ends.");
                return cut;
            }
            // Walk on down the fill's own line, which runs from the saddle to
            // the sea. Read from the receiver array rather than re-derived from
            // the ground: the cut is rewriting that ground as it goes, and a
            // steepest-descent step over half-cut terrain can turn back into
            // the notch it just made.
            int next = receiver[idx];
            if (next < 0) { break; }
            idx = next;
        }
        return cut;
    }

    // Cut a channel wherever the flow runs over ground already at its own water
    // level — i.e. everywhere outside the flooded basins. On such a column the
    // filled level EQUALS the terrace, so the surface is the terrace's own top
    // and the bed is one riverDepth under it: a notch, with the same flat top
    // the ground beside it has.
    //
    // Width is stamped as a disc per channel column, and a column reached by
    // several discs takes the level of the NEAREST of them. Nearest, not lowest:
    // at a cascade the pool above the drop and the pool below it stamp each
    // other, and taking the lower level there dragged the upper pool's surface
    // down to the lower terrace — where the bank-cut rule below then rejected
    // it as a wall, so the river vanished for a few columns at EVERY wall it
    // crossed and never reached the sea. Nearest keeps each pool at its own
    // level and lets the two meet as a step.
    private static void CutRiverChannels(int[,] height, int[,] plateau, int[,] water, int[] surfaceY,
        float[] flow, bool[,] pinned, bool[,] onRamp, CellularTerrainData cd, int depth, WaterStats stats,
        FastNoiseLite widthNoise, int worldMinX, int worldMinZ)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        var stamped = new int[sizeX, sizeZ];
        var stampDist = new int[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                stamped[lx, lz] = HeightMap.NoWater;
                stampDist[lx, lz] = int.MaxValue;
            }
        }

        float logSpan = Mathf.Log(Math.Max(2f, cd.riverWidthFullFlow / Math.Max(1f, cd.riverMinFlow)));
        float halfMin = Math.Max(0.5f, cd.riverHalfWidthMin);
        float halfMax = Math.Max(halfMin, cd.riverHalfWidthMax);
        bool any = false;

        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                int idx = lx * sizeZ + lz;
                if (flow[idx] < cd.riverMinFlow) { continue; }
                int level = surfaceY[idx];
                // Already a lake column, or sea: the channel resumes below the
                // outlet rather than trying to dig through standing water.
                if (level > height[lx, lz] || height[lx, lz] < WorldGen.WATER_LEVEL) { continue; }

                float t = Math.Clamp(Mathf.Log(flow[idx] / Math.Max(1f, cd.riverMinFlow)) / logSpan, 0f, 1f);
                // Flow alone only ever grows downstream, so it gives a ribbon
                // that widens monotonically from source to mouth. The noise is
                // what breaks that into pools and narrows — a MULTIPLIER rather
                // than an added width, so a tributary wanders by a fraction of
                // its own size instead of by the trunk's.
                float rawNoise = Math.Clamp(
                    WIDTH_NOISE_GAIN * (2f * Noise01(widthNoise, lx + worldMinX, lz + worldMinZ) - 1f),
                    -1f, 1f);
                if (rawNoise < stats.NoiseLo) { stats.NoiseLo = rawNoise; }
                if (rawNoise > stats.NoiseHi) { stats.NoiseHi = rawNoise; }
                float wobble = 1f + Math.Clamp(cd.riverWidthNoise, 0f, 1f) * rawNoise;
                // Never below the half-column that guarantees the channel's OWN
                // column is stamped. Under it the disc covers nothing, the river
                // comes out with holes in it, and nothing downstream re-checks
                // that the network still reaches the sea.
                float half = Math.Max(0.5f, Mathf.Lerp(halfMin, halfMax, t) * wobble);
                if (half < stats.NarrowestHalf) { stats.NarrowestHalf = half; }
                if (half > stats.WidestHalf) { stats.WidestHalf = half; }
                int r = Mathf.CeilToInt(half);
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = lx + dx;
                    if (nx < 0 || nx >= sizeX) { continue; }
                    for (int dz = -r; dz <= r; dz++)
                    {
                        int nz = lz + dz;
                        if (nz < 0 || nz >= sizeZ) { continue; }
                        int distSq = dx * dx + dz * dz;
                        if (distSq > half * half) { continue; }
                        // Equidistant stamps go to the HIGHER level, so a pool
                        // keeps its full depth rather than being trimmed by the
                        // one below it.
                        if (distSq > stampDist[nx, nz]) { continue; }
                        if (distSq == stampDist[nx, nz] && level <= stamped[nx, nz]) { continue; }
                        stampDist[nx, nz] = distSq;
                        stamped[nx, nz] = level;
                        any = true;
                    }
                }
            }
        }
        if (!any) { return; }

        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                int level = stamped[lx, lz];
                if (level == HeightMap.NoWater) { continue; }
                if (water[lx, lz] != HeightMap.NoWater) { continue; }
                if (height[lx, lz] < WorldGen.WATER_LEVEL) { continue; }
                // A CHANNEL may cross pinned ground; a LAKE may never flood it.
                // The asymmetry is the point: standing water over the village
                // drowns it, while a two-voxel channel through it is a stream
                // through a settlement, which is where settlements are. Refusing
                // both left a 175-column dry gap in the trunk river exactly
                // where the pinned zone straddles the plain it drains.
                if (pinned[lx, lz]) { stats.PinnedCrossings++; }
                // The widening stops at a real wall. Along the channel's own
                // path the ground never stands above the water (the depression
                // fill guarantees it), so this only bounds how far the banks are
                // cut back — without it a channel running at the foot of a
                // 12-voxel scarp would trench straight through it.
                if (height[lx, lz] - level > cd.riverBankCut) { stats.BankRejects++; continue; }
                // The mirror of the bank rule, and the one that keeps a cascade
                // from standing as solid water. The disc spreads a pool's level
                // ACROSS the lip of a drop — the tie-break above deliberately
                // keeps the HIGHER of two competing stamps — so a column at the
                // foot of the fall is handed the upper pool's surface while its
                // own bed stays where it is, and the chunk fill then fills every
                // voxel between the two. Only ground within the channel's own
                // notch belongs to this pool. Without it the fall is also
                // INVISIBLE to the sheet test in BuildWaterfallSites, since that
                // reports a cascade where the scratch surface ends up above the
                // real water field and this had already raised the real one.
                if (level - height[lx, lz] > depth) { stats.DropRejects++; continue; }
                if (onRamp[lx, lz]) { stats.RampCrossings++; }

                int bed = Math.Min(height[lx, lz], level - depth);
                height[lx, lz] = bed;
                plateau[lx, lz] = Math.Min(plateau[lx, lz], bed);
                water[lx, lz] = level;
                stats.RiverColumns++;
                stats.WaterColumns++;
            }
        }
    }

    // Group the columns the fall pass flagged into SITES — one per cascade,
    // not one per column — and report them. A five-wide sheet is one waterfall
    // and wants one effect across it, not five narrow ones side by side.
    //
    // A column carries a fall wherever the pass's scratch surface ended up above
    // the real water field; the gap between the two IS the sheet.
    private static List<WaterfallSite> BuildWaterfallSites(int[,] sheet, int[,] water,
        int[,] height, int worldMinX, int worldMinZ, WaterStats stats)
    {
        int sizeX = sheet.GetLength(0);
        int sizeZ = sheet.GetLength(1);
        var sites = new List<WaterfallSite>();
        var seen = new bool[sizeX, sizeZ];
        var stack = new Stack<int>();

        bool Falls(int lx, int lz)
        {
            int top = sheet[lx, lz];
            if (top == HeightMap.NoWater) { return false; }
            int had = water[lx, lz];
            return had == HeightMap.NoWater || top > had;
        }

        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                if (seen[lx, lz] || !Falls(lx, lz)) { continue; }
                seen[lx, lz] = true;
                stack.Clear();
                stack.Push(lx * sizeZ + lz);
                int columns = 0;
                int top = int.MinValue;
                int bottom = int.MaxValue;
                long sumX = 0;
                long sumZ = 0;
                var members = new List<int>();
                while (stack.Count > 0)
                {
                    int idx = stack.Pop();
                    int cx = idx / sizeZ;
                    int cz = idx % sizeZ;
                    columns++;
                    members.Add(idx);
                    sumX += cx + worldMinX;
                    sumZ += cz + worldMinZ;
                    if (sheet[cx, cz] > top) { top = sheet[cx, cz]; }
                    // Where the sheet LANDS: the pool it falls into, or the bed
                    // where it lands dry.
                    int floor = water[cx, cz] == HeightMap.NoWater ? height[cx, cz] : water[cx, cz];
                    if (floor < bottom) { bottom = floor; }
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = cx + NEIGHBOUR_DX[d];
                        int nz = cz + NEIGHBOUR_DZ[d];
                        if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                        if (seen[nx, nz] || !Falls(nx, nz)) { continue; }
                        seen[nx, nz] = true;
                        stack.Push(nx * sizeZ + nz);
                    }
                }
                // The centroid is where the sheet is ON AVERAGE, which for a
                // curved or L-shaped group is not one of its own columns — the
                // 12 m fall's centroid landed in the channel BESIDE the drop, so
                // anything placed there would sit in the river rather than at the
                // lip. Snap to the member column nearest it, the same fix
                // ResolveLandformPois makes for the same reason.
                float cxAvg = sumX / (float)columns;
                float czAvg = sumZ / (float)columns;
                int bestIdx = members[0];
                float bestD = float.MaxValue;
                foreach (int idx in members)
                {
                    float dx = idx / sizeZ + worldMinX - cxAvg;
                    float dz = idx % sizeZ + worldMinZ - czAvg;
                    float d = dx * dx + dz * dz;
                    if (d < bestD) { bestD = d; bestIdx = idx; }
                }
                sites.Add(new WaterfallSite(
                    new Vector3(bestIdx / sizeZ + worldMinX + 0.5f, top, bestIdx % sizeZ + worldMinZ + 0.5f),
                    bottom, columns));
                stats.FallColumns += columns;
            }
        }

        var parts = new List<string>();
        foreach (WaterfallSite w in sites)
        {
            parts.Add($"({w.Top.X:F0}, {w.Top.Y:F0}, {w.Top.Z:F0}) {w.Height}v/{w.Columns}col");
        }
        GD.Print($"[CellularTerrain] waterfall sites ({sites.Count}): {string.Join("; ", parts)}");
        return sites;
    }

    // Smoothing passes over the raw current field. Needed because the drainage
    // tree is FOUR-connected: a raw direction is one of four axis vectors, so a
    // reach running diagonally comes out as a zigzag of alternating ±X and ±Z
    // columns, and the shader advects the ripple pattern along it — visibly
    // snapping between two axes down a river that looks straight. Averaging over
    // the wet neighbours resolves the zigzag back into the diagonal it was
    // approximating.
    private const int CURRENT_SMOOTHING_PASSES = 3;

    // Gain applied to the river-width noise before riverWidthNoise scales it.
    // Perlin does not reach ±1 — it is a gradient field, and over the few
    // hundred columns this world spans at the authored frequency the extremes
    // simply never come up. MEASURED: the raw channel spanned -0.38..0.51 over
    // the whole map, so an authored amount of 0.5 delivered a ±0.25 wobble and
    // the rivers came out barely distinguishable from the pure flow curve. The
    // gain is a bit over the reciprocal of that span, CLAMPED, so the authored
    // number is actually reachable; the clamp flat-tops the extremes, which is
    // the right shape for water anyway — a pool has a width, it is not an
    // instant of maximum on a sine.
    private const float WIDTH_NOISE_GAIN = 2.2f;

    // Which way the water in each wet column is moving, and how fast, in the
    // normalized [-1, 1] units ChunkState.SetCurrent stores.
    //
    // Direction comes from the priority flood's own receiver line — the column
    // each one drains into — which is the only thing in the world that knows
    // which way a river runs. It cannot be recovered downstream: the water
    // surface is deliberately FLAT along a reach (that is the whole lattice
    // invariant), so there is no gradient left in the finished heightfield to
    // read a direction off. Speed follows the same log-flow curve the width
    // does, so one reach is never wide and still.
    //
    // Direction and speed are smoothed SEPARATELY and recombined. Blurring the
    // vector alone loses speed wherever the zigzag cancels — averaging (1,0)
    // with (0,1) is 0.7 long, so every diagonal reach comes out 30% slower than
    // the axis-aligned ones either side of it, which reads as a river that
    // stalls on the bends.
    private static Vector2[,] BuildSurfaceCurrents(int[,] water, bool[,] isLake, int[] receiver,
        float[] flow, CellularTerrainData cd, int sizeX, int sizeZ, WaterStats stats)
    {
        var dir = new Vector2[sizeX, sizeZ];
        var speed = new float[sizeX, sizeZ];
        float logSpan = Mathf.Log(Math.Max(2f, cd.riverWidthFullFlow / Math.Max(1f, cd.riverMinFlow)));
        float speedMin = Math.Clamp(cd.riverCurrentMin, 0f, 1f);
        float speedMax = Math.Max(speedMin, Math.Clamp(cd.riverCurrentMax, 0f, 1f));
        float lakeScale = Math.Clamp(cd.lakeCurrentScale, 0f, 1f);

        bool any = false;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                if (water[lx, lz] == HeightMap.NoWater) { continue; }
                int idx = lx * sizeZ + lz;
                int r = receiver[idx];
                // An outlet column (the sea, the world border) has no receiver.
                // Left still rather than guessed at — it is where the river ends.
                if (r < 0) { continue; }

                float t = Math.Clamp(
                    Mathf.Log(Math.Max(1f, flow[idx]) / Math.Max(1f, cd.riverMinFlow)) / logSpan, 0f, 1f);
                float s = Mathf.Lerp(speedMin, speedMax, t);
                if (isLake[lx, lz]) { s *= lakeScale; }

                dir[lx, lz] = new Vector2(r / sizeZ - lx, r % sizeZ - lz);
                speed[lx, lz] = s;
                any = true;
                stats.CurrentColumns++;
            }
        }
        if (!any) { return null; }

        // Averaged over WET neighbours only. Including the dry ones would drag
        // every bank column toward zero and, over three passes, hollow out the
        // middle of any channel narrower than the kernel — which is most of them.
        var dirNext = new Vector2[sizeX, sizeZ];
        var speedNext = new float[sizeX, sizeZ];
        for (int pass = 0; pass < CURRENT_SMOOTHING_PASSES; pass++)
        {
            for (int lx = 0; lx < sizeX; lx++)
            {
                for (int lz = 0; lz < sizeZ; lz++)
                {
                    if (water[lx, lz] == HeightMap.NoWater) { continue; }
                    Vector2 dSum = dir[lx, lz];
                    float sSum = speed[lx, lz];
                    int n = 1;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = lx + NEIGHBOUR_DX[d];
                        int nz = lz + NEIGHBOUR_DZ[d];
                        if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                        if (water[nx, nz] == HeightMap.NoWater) { continue; }
                        dSum += dir[nx, nz];
                        sSum += speed[nx, nz];
                        n++;
                    }
                    dirNext[lx, lz] = dSum / n;
                    speedNext[lx, lz] = sSum / n;
                }
            }
            (dir, dirNext) = (dirNext, dir);
            (speed, speedNext) = (speedNext, speed);
        }

        var current = new Vector2[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                float len = dir[lx, lz].Length();
                // A genuine cancellation — the middle of a lake, a confluence of
                // two opposed inflows — is still water, and normalizing it would
                // invent a direction out of rounding error.
                if (len < 0.001f) { continue; }
                current[lx, lz] = dir[lx, lz] / len * speed[lx, lz];
                if (speed[lx, lz] > stats.FastestCurrent) { stats.FastestCurrent = speed[lx, lz]; }
            }
        }
        return current;
    }

    // Carry the water DOWN each drop as a vertical sheet. Every column already
    // holding water is raised to the highest water surface among its four
    // neighbours, so the single column at the foot of a step is filled from its
    // own bed up to the pool above it — a waterfall. Without it a cascade is two
    // pools with a bare rock face between them and the river reads as broken.
    //
    // It has to reach DRY columns, not just ones that already hold water: where
    // a channel runs off a lip there is no channel below it to raise, and a
    // wet-only rule left the river as a lip of water overhanging bare rock.
    // DETECTION ONLY — it writes no water into the world.
    //
    // `sheet` comes in as a copy of `water` and is the pass's own scratch: the
    // walk needs somewhere to propagate a level down a staircase, and the real
    // field must not be that place. Where `sheet` ends up above `water`, a
    // cascade pours through the column, and the difference is exactly the
    // vertical extent of it.
    //
    // It used to STAND the water — fill the drop with voxels so a fall did not
    // read as two pools with bare rock between them. A waterfall effect draws
    // that now, and the voxels were actively harmful: a column of water is
    // indistinguishable from a deep pool, so buoyancy floated the player back up
    // the cascade. Leaving the drop as air is what makes falling through it work
    // with no special case anywhere.
    private static void StandWaterfalls(int[,] height, int[,] water, CellularTerrainData cd,
        WaterStats stats, int[,] sheet)
    {
        int sizeX = water.GetLength(0);
        int sizeZ = water.GetLength(1);
        int reach = Math.Max(1, cd.riverFallReach);

        // Distance in columns from the lip the fall started at, so the walk
        // below is bounded. int.MaxValue = not part of any fall.
        var stepsFromLip = new int[sizeX, sizeZ];
        var work = new Queue<int>();
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                stepsFromLip[lx, lz] = int.MaxValue;
                if (water[lx, lz] != HeightMap.NoWater)
                {
                    stepsFromLip[lx, lz] = 0;
                    work.Enqueue(lx * sizeZ + lz);
                }
            }
        }

        // Walk the fall DOWNHILL, one step of the staircase per round, instead
        // of one column and done. A wall in this world is rarely a single drop —
        // the relaxation caps each cell step and the coast terraces repeatedly,
        // so a cliff is a flight of steps. Carrying the water only one column
        // reached the first tread and stopped, which is exactly the break: a lip
        // of water, a bare step, then the reach below.
        //
        // The rule that keeps this from flooding the world is that it may only
        // move onto STRICTLY LOWER ground. Water never spreads along a terrace,
        // only down a face, so it cannot cross the flat ground it lands on. The
        // step budget bounds the other case — a long gentle descent, where an
        // unbounded walk would trail a ribbon of standing water for as far as
        // the ground keeps falling.
        // A WORKLIST, not a fixed number of rounds. A column's level can be
        // improved after it has already handed its own level on — the step down
        // a staircase reaches the tread below before the taller neighbour above
        // it has raised it — and a round-based walk never revisits it, so the
        // fall arrived at that tread carrying the wrong (lower) surface and the
        // curtain broke two voxels below the lip. Re-queueing on a level
        // improvement is what closes it. Terminates because a column is only
        // ever re-queued when its level strictly rises or its step count
        // strictly falls, and both are bounded.
        while (work.Count > 0)
        {
            int idx = work.Dequeue();
            int lx = idx / sizeZ;
            int lz = idx % sizeZ;
            int level = sheet[lx, lz];
            if (stepsFromLip[lx, lz] >= reach) { continue; }
            for (int d = 0; d < 4; d++)
            {
                int nx = lx + NEIGHBOUR_DX[d];
                int nz = lz + NEIGHBOUR_DZ[d];
                if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                if (height[nx, nz] >= height[lx, lz]) { continue; }
                int have = sheet[nx, nz];
                if (have != HeightMap.NoWater && have >= level) { continue; }
                // Above the SEA as well as above the ground: a shoreline column
                // below a river mouth already at sea level is not a fall, and
                // stamping one would fringe every mouth with a ring of
                // duplicate waterline.
                if (level <= Math.Max(WorldGen.WATER_LEVEL, height[nx, nz])) { continue; }

                int drop = level - Math.Max(have, height[nx, nz]);
                sheet[nx, nz] = level;
                if (drop > stats.TallestFall) { stats.TallestFall = drop; }

                int steps = Math.Min(stepsFromLip[nx, nz], stepsFromLip[lx, lz] + 1);
                stepsFromLip[nx, nz] = steps;
                if (steps < reach) { work.Enqueue(nx * sizeZ + nz); }
            }
        }
    }

    // Round DOWN to the world's terrace lattice, measured from sea level so the
    // waterline itself is on it. Floor, not truncation: heights below sea level
    // are ordinary here.
    private static int LatticeFloor(int y, int step)
    {
        int rel = y - WorldGen.WATER_LEVEL;
        int q = rel >= 0 ? rel / step : -((-rel + step - 1) / step);
        return WorldGen.WATER_LEVEL + q * step;
    }

    // Subdivision levels needed to bring a level-0 cell down to `target` voxels.
    // Capped so the packed cell key stays inside its 12-bit coordinate fields.
    private int LevelForSize(float target)
    {
        float baseSize = Math.Max(1f, _data.cellSizeMeters);
        if (target >= baseSize) { return 0; }
        return Math.Clamp(Mathf.CeilToInt(Mathf.Log(baseSize / Math.Max(1f, target)) / Mathf.Log(2f)), 0, 6);
    }

    // ------------------------------------------------------------- utilities

    // Nearest jittered site over the 3x3 grid neighbourhood — a Voronoi cell.
    // Three-by-three is sufficient for any jitter up to a full slot: a site
    // cannot leave its own slot, so nothing outside that ring can be nearest.
    private void FindCell(float px, float pz, int level, out int gx, out int gz)
    {
        float spacing = Math.Max(1f, _data.cellSizeMeters) / (1 << level);
        int cx = Mathf.FloorToInt(px / spacing);
        int cz = Mathf.FloorToInt(pz / spacing);
        gx = cx;
        gz = cz;
        float best = float.MaxValue;
        for (int ix = -1; ix <= 1; ix++)
        {
            for (int iz = -1; iz <= 1; iz++)
            {
                SiteOf(cx + ix, cz + iz, level, spacing, out float sx, out float sz);
                float dx = px - sx;
                float dz = pz - sz;
                float d = dx * dx + dz * dz;
                if (d < best)
                {
                    best = d;
                    gx = cx + ix;
                    gz = cz + iz;
                }
            }
        }
    }

    private void SiteOf(int gx, int gz, int level, float spacing, out float sx, out float sz)
    {
        int h = Hash(gx, gz, _cellSeed + level);
        float jx = ((h & 0xFFFF) / 65535f - 0.5f) * _data.cellJitter;
        float jz = (((h >> 16) & 0x7FFF) / 32767f - 0.5f) * _data.cellJitter;
        sx = (gx + 0.5f + jx) * spacing;
        sz = (gz + 0.5f + jz) * spacing;
    }

    // Cell identity: the level, the LEVEL-0 cell (so a fine cell never spans two
    // coarse parents) and the cell at that level. Coordinates are offset into 12
    // bits, which covers a world of 4096 columns per side at any level.
    private static long PackKey(int level, int g0x, int g0z, int gx, int gz)
    {
        long a = (uint)(g0x + 2048) & 0xFFF;
        long b = (uint)(g0z + 2048) & 0xFFF;
        long c = (uint)(gx + 2048) & 0xFFF;
        long d = (uint)(gz + 2048) & 0xFFF;
        return ((long)level << 48) | (a << 36) | (b << 24) | (c << 12) | d;
    }

    private static int Hash(int a, int b, int c)
    {
        unchecked
        {
            uint h = (uint)a * 0x9E3779B1u;
            h ^= (uint)b * 0x85EBCA77u;
            h ^= (uint)c * 0xC2B2AE3Du;
            h = ((h >> 16) ^ h) * 0x85EBCA6Bu;
            h = ((h >> 13) ^ h) * 0xC2B2AE35u;
            h = (h >> 16) ^ h;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    // This approach's own per-zone knobs, folded from the same weight solve the
    // shared scalars use. A zone carrying another approach's resource
    // contributes defaults rather than dropping out of the sum, which would
    // silently skew its neighbours' share.
    private void SampleZoneKnobs(int wx, int wz, out float subdivideScale, out float cliffScale)
    {
        ZoneGenData[] zones = _genData.ZoneGens;
        int n = zones != null ? zones.Length : 0;
        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        WorldGen.SampleBlendedZoneGen(wx, wz, zones, weights);

        subdivideScale = 0f;
        cliffScale = 0f;
        float total = 0f;
        for (int i = 0; i < n; i++)
        {
            if (weights[i] <= 0f) { continue; }
            CellularZoneTerrainData zt = zones[i]?.terrain as CellularZoneTerrainData ?? ZoneDefaults;
            subdivideScale += zt.cellSubdivideScale * weights[i];
            cliffScale += zt.cliffScale * weights[i];
            total += weights[i];
        }
        if (total <= 0f)
        {
            subdivideScale = ZoneDefaults.cellSubdivideScale;
            cliffScale = ZoneDefaults.cliffScale;
            return;
        }
        subdivideScale = Math.Max(0.05f, subdivideScale / total);
        cliffScale = Math.Max(0.05f, cliffScale / total);
    }
}
