using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

public static class WorldGen
{
    // Per-zone kit slot order. Each ZoneGenData.Kits[] is treated as a
    // fixed-length tuple in this slot order: surface, cave, underwater. The
    // ChunkState.KitId byte stamped per voxel is a GLOBAL index into the
    // flattened palette (Zone0.Kits ++ Zone1.Kits ++ …) — see
    // GlobalKit() / Main.StartGame's flatten step. With KITS_PER_ZONE = 3
    // and N zones, the palette occupies 3·N slots; ChunkMesh.MAX_KITS = 16
    // bounds N at 5. Same as the legacy single-zone mapping when N=1.
    // Bitmask flags for the worldgen_skip CVar — see CVars.worldgenSkip.
    // Each category is checked independently inside GenerateProps; setting
    // SKIP_ALL turns the prop pass off entirely.
    public const int SKIP_DETAILS = 1;       // painted detail-sprite scatter
    public const int SKIP_PROPS = 2;         // trees + tall grass
    public const int SKIP_MOBS = 4;          // goblins, kun_kun (surface + cave)
    public const int SKIP_INTERACTIVES = 8;  // loot + chests (surface + cave)
    public const int SKIP_ALL = SKIP_DETAILS | SKIP_PROPS | SKIP_MOBS | SKIP_INTERACTIVES;

    private static readonly PackedScene PoisonChestScene =
        GD.Load<PackedScene>("res://scenes/interactives/chest_poison.tscn");

    public const int KITS_PER_ZONE = 3;
    private const byte KIT_SLOT_TEMPERATE = 0;   // surface (temperate / desert / marsh / …)
    private const byte KIT_SLOT_CAVE = 1;        // cave-interior shells
    private const byte KIT_SLOT_UNDERWATER = 2;  // submerged seabed shell

    // Pack (zoneIdx, slot) → global kitId.
    private static byte GlobalKit(int zoneIndex, byte slot)
    {
        return (byte)(zoneIndex * KITS_PER_ZONE + slot);
    }

    // Slot of a global kitId — the cross-zone "kind of surface" (surface vs
    // cave vs underwater). Used by passes that ask "is this a temperate-style
    // surface voxel?" without caring which zone it belongs to.
    private static int KitSlot(int kitId)
    {
        return kitId % KITS_PER_ZONE;
    }

    // ZoneIndex of the chunk owning (wx, wy, wz). Falls back to 0 if the
    // chunk isn't loaded — fine for pre-streaming worldgen which generates
    // every chunk before this gets called. Streaming will need richer
    // semantics here (a zone atlas keyed by world XZ).
    private static int ZoneIndexAtWorld(WorldState ws, int wx, int wy, int wz)
    {
        Vector3I cc = new Vector3I(
            (int)System.Math.Floor((double)wx / ChunkState.SIZE),
            (int)System.Math.Floor((double)wy / ChunkState.SIZE),
            (int)System.Math.Floor((double)wz / ChunkState.SIZE));
        ChunkState chunk = ws.GetChunk(cc);
        return chunk != null ? chunk.ZoneIndex : 0;
    }

    // Staircase spiral pattern: (dx, dz) offsets from center, actions per y-level
    private const int STAIR_KEEP = 0;
    private const int STAIR_FULL = 1;
    private const int STAIR_AIR = 2;

    private static readonly (int dx, int dz, int[] yActions)[] StaircasePattern =
    {
        (-1,  1, new[] { STAIR_FULL, STAIR_AIR,  STAIR_AIR,  STAIR_KEEP }),
        (-1,  0, new[] { STAIR_FULL, STAIR_AIR,  STAIR_AIR,  STAIR_AIR  }),
        (-1, -1, new[] { STAIR_FULL, STAIR_FULL, STAIR_AIR,  STAIR_AIR  }),
        ( 0, -1, new[] { STAIR_FULL, STAIR_FULL, STAIR_AIR,  STAIR_AIR  }),
        ( 1, -1, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_AIR  }),
        ( 1,  0, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_AIR  }),
        ( 1,  1, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_FULL }),
        ( 0,  0, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_FULL }),
        ( 0,  1, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_FULL }),
    };

    // World y at and below this level is filled with water wherever terrain
    // doesn't reach up to it. Terrain perlin noise is allowed to dip below
    // this value, producing natural lakes and oceans. Set to 0 so the
    // shoreline plateau (where land meets sea) sits at y = 0 — height
    // numbers in the editor / debug dumps now read directly as "voxels
    // above sea level". Land starts at y = 1; water fills y ≤ 0.
    public const int WATER_LEVEL = 0;

    // Ground-hugging fog. Per-zone humidity is treated as a "fog volume"
    // poured into the zone's terrain like water — bucket-fills the lowest
    // air space first, finds a level Y, then stamps density on each voxel
    // proportional to how far below Y it sits. Flat zones spread fog
    // thin; varied zones concentrate fog in the deepest valleys. Across
    // zone borders the bucket levels blend per-column via the same
    // kernel that drives prop / kit divergence. Only open-to-sky voxels
    // get seeded — caves / tunnels stay fog-free.
    public const int FOG_MAX_DENSITY = 255;
    // Per-column "bucket capacity" at humidity = 1, in voxel-depth units.
    // Tuned so a humid zone (≈0.9) yields a few voxels of average fog
    // depth — reads as low mist, not a wall.
    private const float FOG_VOLUME_PER_HUMIDITY = 6f;
    // Density gradient inside the bucket: density(wy) = (ceiling - wy) *
    // FOG_DENSITY_PER_VOXEL, clamped to [0, FOG_MAX_DENSITY]. With value 80,
    // the bottom of a 4-voxel-deep pocket caps at full density and the top
    // voxel reads as a thin haze.
    private const float FOG_DENSITY_PER_VOXEL = 80f;

    // Horizontal cells per 1 vertical voxel on the ramp skirt cast by a taller
    // plateau into its lower neighbour. With PlateauStep=4, slope=1 produces
    // a 4-cell ramp that rises one full plateau step — steep (45°) but
    // narrow, matching the "ramps should be a handful of cells wide" spec.
    private const int RAMP_SLOPE = 1;

    // Per-channel salts for DeriveSeed. Stable values — never reuse one for a
    // new noise channel, or two channels will share a noise field for the
    // same worldSeed and a future change to one would shift the other.
    private const int SEED_SALT_TERRAIN = 0x01;
    private const int SEED_SALT_TUNNEL  = 0x02;
    private const int SEED_SALT_CAVE    = 0x03;
    private const int SEED_SALT_GRASS   = 0x04;
    private const int SEED_SALT_PATH    = 0x05;
    private const int SEED_SALT_RIVER   = 0x06;
    private const int SEED_SALT_FOREST  = 0x07;
    private const int SEED_SALT_ZONE  = 0x08;
    // Used by GenerateRoads — kept in the same registry so it can't collide
    // with a future channel.
    public const int SEED_SALT_ROAD     = 0x09;
    private const int SEED_SALT_ELEVATION = 0x0A;
    private const int SEED_SALT_PROPS   = 0x0B;

    // Stable, process-independent mix of three ints. System.HashCode.Combine
    // seeds itself with a process-random salt, so it would re-randomize
    // world-gen on every launch — use this anywhere worldgen needs a
    // deterministic seed.
    private static int StableMix(int a, int b, int c)
    {
        unchecked
        {
            uint h = (uint)a * 0x9E3779B1u;
            h ^= (uint)b * 0x85EBCA77u;
            h ^= (uint)c * 0xC2B2AE3Du;
            h = ((h >> 16) ^ h) * 0x85EBCA6Bu;
            h = ((h >> 13) ^ h) * 0xC2B2AE35u;
            h = (h >> 16) ^ h;
            return (int)h;
        }
    }

    // Mix worldSeed with a per-channel salt to produce a stable sub-seed.
    // Must be deterministic across runs — see StableMix.
    public static int DeriveSeed(int worldSeed, int salt)
    {
        return StableMix(worldSeed, salt, 0);
    }

    public static WorldState Generate(WorldGenData genData, int worldSeed, Vector3I worldSize)
    {
        var min = new Vector3I(-worldSize.X / 2, -1, -worldSize.Z / 2);
        var max = new Vector3I(min.X + worldSize.X - 1, min.Y + worldSize.Y - 1, min.Z + worldSize.Z - 1);
        var ws = new WorldState(min, max, genData.SimData);

        // Build the per-world ZoneState array from the authored zone
        // templates. The ZoneData embedded in each ZoneGenData is what
        // ZoneState carries forward — the per-zone worldgen scalars on
        // ZoneGenData stay in `genData` and get blended per-position
        // during the passes below. Runtime fields (windDirection, elevation)
        // are seeded here — windDirection randomized in the XZ plane so
        // each generated world has its own prevailing wind per zone.
        // Elevation defaults to 0 for now; once the editor lands it can
        // be authored per-zone per-world.
        var zoneRng = new RandomNumberGenerator();
        zoneRng.Seed = (ulong)DeriveSeed(worldSeed, SEED_SALT_ZONE);
        ws.Zones = new ZoneState[genData.Zones.Length];
        for (int i = 0; i < genData.Zones.Length; i++)
        {
            float angle = zoneRng.RandfRange(0f, Mathf.Tau);
            ws.Zones[i] = new ZoneState
            {
                Data = genData.Zones[i]?.Zone,
                WindDirection = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)),
                Elevation = 0f,
            };
        }

        var terrainNoise = new FastNoiseLite();
        terrainNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        terrainNoise.Seed = DeriveSeed(worldSeed, SEED_SALT_TERRAIN);
        terrainNoise.Frequency = 0.02f;
        terrainNoise.FractalOctaves = 4;

        var tunnelNoise = new FastNoiseLite();
        tunnelNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        tunnelNoise.Seed = DeriveSeed(worldSeed, SEED_SALT_TUNNEL);
        tunnelNoise.Frequency = 0.025f;
        tunnelNoise.FractalOctaves = 2;

        var caveNoise = new FastNoiseLite();
        caveNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        caveNoise.Seed = DeriveSeed(worldSeed, SEED_SALT_CAVE);
        // Single shared cave noise for the whole world — frequency comes
        // from the first zone. CaveThreshold IS blended per-column below,
        // so zones can still vary in cave density even though the underlying
        // noise pattern is the same.
        caveNoise.Frequency = FirstZoneGen(genData)?.CaveNoiseFrequency ?? 0.04f;
        caveNoise.FractalOctaves = 2;

        var grassNoise = new FastNoiseLite();
        grassNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        grassNoise.Seed = DeriveSeed(worldSeed, SEED_SALT_GRASS);
        grassNoise.Frequency = 0.1f;
        grassNoise.FractalOctaves = 2;

        // Dedicated low-frequency gate noise. Its zero-crossings mark
        // plateau-boundary segments that get ramped; everything else stays a
        // cliff. Lower frequency than pathNoise so the world has just a
        // handful of long, sparse ramp zones rather than dozens of short
        // meanders. Seeded off the path channel so the pattern is stable
        // with the rest of the world-gen output for a given worldSeed.
        var rampGateNoise = new FastNoiseLite();
        rampGateNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        rampGateNoise.Seed = DeriveSeed(worldSeed, SEED_SALT_PATH);
        rampGateNoise.Frequency = 0.015f;
        rampGateNoise.FractalOctaves = 1;

        var forestNoise = new FastNoiseLite();
        forestNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        forestNoise.Seed = DeriveSeed(worldSeed, SEED_SALT_FOREST);
        // First-zone frequency, same rule as caveNoise. ForestThreshold
        // and ForestDensity are blended per-column.
        forestNoise.Frequency = FirstZoneGen(genData)?.ForestNoiseFrequency ?? 0.05f;
        forestNoise.FractalOctaves = 2;

        // Low-frequency macro elevation. Drives broad continental shape that
        // the per-zone terrain noise modulates around — generates rolling
        // foothills/basins independent of which zone a chunk belongs to.
        var elevationNoise = new FastNoiseLite();
        elevationNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        elevationNoise.Seed = DeriveSeed(worldSeed, SEED_SALT_ELEVATION);
        elevationNoise.Frequency = 0.005f;
        elevationNoise.FractalOctaves = 1;

        // Build the integer height field once up front. Chunk and prop
        // generation read from this map instead of re-evaluating noise per
        // voxel — the shape is also authored here (plateau / ramp / river)
        // so geometry is noise-free by construction.
        var heightMap = BuildHeightMap(ws, genData, terrainNoise, rampGateNoise, elevationNoise);

        for (int x = ws.Min.X; x <= ws.Max.X; x++)
        {
            for (int y = ws.Min.Y; y <= ws.Max.Y; y++)
            {
                for (int z = ws.Min.Z; z <= ws.Max.Z; z++)
                {
                    var coord = new Vector3I(x, y, z);
                    var chunk = new ChunkState(coord);
                    chunk.ZoneIndex = PickZoneIndex(coord, ws.Zones.Length);
                    GenerateChunk(chunk, genData, tunnelNoise, heightMap);
                    ws._chunks[coord] = chunk;
                }
            }
        }

        // Tag the submerged shell as KIT_UNDERWATER. Runs after every chunk
        // (and its water voxels) exist so we can check actual water adjacency
        // instead of "wy <= WATER_LEVEL" — a y-only rule paints buried rock
        // under above-water cliffs as underwater, and the mesher's 27-voxel
        // kit vote then bleeds sand onto cliff faces nowhere near water.
        TagSubmergedKits(ws, genData);

        // Carve swiss-cheese caves through terrain. Runs after terrain+tunnel
        // generation so cave carving sees the full solid column and can
        // connect tunnels vertically where they overlap.
        GenerateCaves(ws, genData, caveNoise);

        // One-off test stamp for underground-water visuals. Carves a wide
        // shallow cavern inland in the mountain zone (toward the desert
        // border) with the ceiling capped at the first plateau above water.
        // Will not survive pure worldgen — remove or gate this call once
        // the underwater shader work lands.
        GenerateTestUnderwaterLake(ws, genData);

        // Place fog after all terrain is final. The pass skips enclosed
        // voxels (tunnels, caves, anything with solid geometry directly
        // overhead) so fog only shows up in genuinely open-to-sky air above
        // water level.
        GenerateFog(ws, genData, heightMap);

        // Mark every buried solid voxel adjacent to carved air/water as Y.
        // Runs after all terrain and cave carving, so it sees the final
        // geometry. Catches cave ceilings, floors, walls, and noise-carved
        // "island" voxels (stalactites) in one pass — regardless of the
        // column's outdoor ramp status. The `buried` gate (solid voxel above
        // somewhere in the column) keeps the outdoor surface voxel untouched
        // so plateau vs. ramp behavior at the surface is preserved.
        MarkCaveSurfaceShapes(ws, genData);

        // Per-voxel neighborhood slope pass: stamp OverlayId=dirt on 1-voxel
        // bumps, walkable ramps, and small plateau steps. The shader's
        // per-fragment slope on a box-smoothed normal cannot see features the
        // smoothing averages away; this authored signal puts them back.
        // Currently disabled — the ±EDGE_SCAN_WINDOW / diff-threshold heuristic
        // doesn't map cleanly to the terrain shapes we actually generate, so
        // overlays end up in the wrong places. Revisit once we have a clearer
        // read on which features need the dirt treatment (probably driven by
        // authored tags from the editor rather than derived from geometry).
        // StampEdgeOverlays(ws);

        // Scatter procedural overlays (dirt patches, field/clover patches) on
        // temperate-kit surface voxels. Noise-driven placement is a rough
        // starting point so the authored overlay art shows up in generated
        // worlds; replace with authored tags once the custom editor lands.
        // Cobblestone roads — sparse 5-voxel-wide ribbons that follow the
        // authored heightfield (step up/down ramps, avoid cliffs and water)
        // and occasionally branch. Runs BEFORE StampProceduralOverlays so
        // the procedural dirt/field scatter can be suppressed on and around
        // road columns — otherwise the mesher's 27-voxel overlay vote at
        // road edges would mix in field/dirt and the road would read as a
        // muddy path instead of paved cobblestone.
        var roadColumns = GenerateRoads(ws, heightMap, genData, worldSeed);

        StampProceduralOverlays(ws, roadColumns);

        // Detail-sprite scatter and prop / mob / loot spawning are gated by
        // the worldgen_skip CVar (bitmask — see SKIP_* flags below). Each
        // category is checked independently so e.g. setting just "details"
        // strips grass blades without affecting trees or mobs.
        int skipFlags = CVars.worldgenSkip.Value;
        if ((skipFlags & SKIP_DETAILS) == 0)
        {
            StampDetailScatter(ws, genData, roadColumns);
        }

        // Always run GenerateProps when *any* of its categories are still
        // active — internal gates pick which subsections actually spawn.
        // Block-light sources are no longer pre-propagated here — torch
        // entities register themselves with WorldState.LightSources when
        // they spawn, which runs the BFS footprint at that point.
        if ((skipFlags & (SKIP_PROPS | SKIP_MOBS | SKIP_INTERACTIVES)) != (SKIP_PROPS | SKIP_MOBS | SKIP_INTERACTIVES))
        {
            for (int x = ws.Min.X; x <= ws.Max.X; x++)
            {
                for (int z = ws.Min.Z; z <= ws.Max.Z; z++)
                {
                    var coord = new Vector3I(x, 0, z);
                    GenerateProps(ws, coord, genData, grassNoise, forestNoise, heightMap, skipFlags, worldSeed);
                }
            }
        }

        // Compute sunlight after all geometry exists.
        LightEngine.ComputeSunlight(ws);

        // Bake the per-chunk wind subgrid from sunlight openness so caves
        // and roofed interiors ship with damped wind. Must run after
        // ComputeSunlight; disk-loaded chunks skip this and use the
        // serialized bytes.
        WindGen.ComputeWindGrid(ws);

        // Default-bake the per-chunk env-tag subgrid from the wind signal
        // so audio's reverb routing has a sensible starting tag without
        // an editor authoring step. Disk-loaded chunks skip this and use
        // the (eventually editor-authored) serialized bytes.
        EnvTagGen.ComputeEnvTagGrid(ws);

        _lastHeightMap = heightMap;
        _lastPlateauStep = (int)Math.Max(1, Math.Round(genData.PlateauStep));
        return ws;
    }

    // Stamp a chunk into one of the world's zones. Legacy 4-quadrant
    // split (NE/NW/SE/SW around the world origin), mapping into a 4-entry
    // Zones[] in [NE, NW, SE, SW] order — matches the array order in
    // default_world_gen.tres. East (X >= 0) is the coastal half (mountain
    // north / swamp south); west (X < 0) is the inland half (desert north /
    // forest south). The eastern strip of the coastal zones transitions
    // to ocean via the BuildHeightMap east-edge falloff. With fewer zones,
    // indices are clamped to the available range so worlds with 1 or 2
    // zone templates still generate. The long-term plan is arbitrary
    // zone polygons authored by the editor; only this function needs to
    // change.
    private static byte PickZoneIndex(Vector3I chunkCoord, int zoneCount)
    {
        if (zoneCount <= 0) { return 0; }
        int quadrant;
        if (chunkCoord.X >= 0 && chunkCoord.Z >= 0) { quadrant = 0; }       // NE
        else if (chunkCoord.X < 0 && chunkCoord.Z >= 0) { quadrant = 1; }   // NW
        else if (chunkCoord.X >= 0 && chunkCoord.Z < 0) { quadrant = 2; }   // SE
        else { quadrant = 3; }                                              // SW
        if (quadrant >= zoneCount) { quadrant = zoneCount - 1; }
        return (byte)quadrant;
    }

    private static ZoneGenData FirstZoneGen(WorldGenData genData)
    {
        if (genData.Zones == null) { return null; }
        for (int i = 0; i < genData.Zones.Length; i++)
        {
            if (genData.Zones[i] != null) { return genData.Zones[i]; }
        }
        return null;
    }

    // Per-column smoothstep blend kernel that mirrors ZoneBlend.Sample.
    // Reaches ZONE_GEN_BLEND_RADIUS chunks out from (wx, wz) and weights
    // each chunk's zone by its smoothstep falloff. PickZoneIndex still
    // returns one zone per chunk for ChunkState.ZoneIndex (gameplay
    // needs a single value), but the worldgen scalars blend smoothly across
    // chunk borders so a desert→forest transition isn't a hard line.
    //
    // `weights` Span must be sized to zoneCount. Output sums to 1 (or
    // all zeros if no neighbour has a valid zone — caller's choice what
    // to do about that).
    private const float ZONE_GEN_BLEND_RADIUS = 2.0f;

    private static void GetZoneGenWeights(int wx, int wz, int zoneCount, Span<float> weights)
    {
        for (int i = 0; i < zoneCount; i++) { weights[i] = 0f; }
        if (zoneCount <= 0) { return; }

        int chunkX = (int)Math.Floor((double)wx / ChunkState.SIZE);
        int chunkZ = (int)Math.Floor((double)wz / ChunkState.SIZE);
        int half = Mathf.CeilToInt(ZONE_GEN_BLEND_RADIUS);

        for (int dx = -half; dx <= half; dx++)
        {
            for (int dz = -half; dz <= half; dz++)
            {
                int cx = chunkX + dx;
                int cz = chunkZ + dz;
                int zoneIdx = PickZoneIndex(new Vector3I(cx, 0, cz), zoneCount);
                float chunkCenterX = (cx + 0.5f) * ChunkState.SIZE;
                float chunkCenterZ = (cz + 0.5f) * ChunkState.SIZE;
                float dxw = wx - chunkCenterX;
                float dzw = wz - chunkCenterZ;
                float distChunks = Mathf.Sqrt(dxw * dxw + dzw * dzw) / ChunkState.SIZE;
                float w = Mathf.SmoothStep(ZONE_GEN_BLEND_RADIUS, 0f, distChunks);
                if (w > 0f) { weights[zoneIdx] += w; }
            }
        }

        float total = 0f;
        for (int i = 0; i < zoneCount; i++) { total += weights[i]; }
        if (total > 1e-6f)
        {
            float inv = 1f / total;
            for (int i = 0; i < zoneCount; i++) { weights[i] *= inv; }
        }
    }

    // Blended per-column scalars sampled from the per-zone ZoneGenData
    // at (wx, wz). Tunnel/cave thresholds are XZ-only so a single struct
    // serves callers that walk the column at any Y.
    private struct BlendedZoneGen
    {
        // Per-zone authored center elevation, kernel-blended at sample
        // time. Eventually the heightmap that feeds BuildHeightMap will be
        // an authored coarse 2D field; this per-zone scalar is the
        // stand-in until that lands.
        public float Elevation;
        public float ElevationRange;
        public float TunnelThreshold;
        public float CaveThreshold;
        public float GrassThreshold;
        public float ForestThreshold;
        public float ForestDensity;
        public float DetailNoiseThreshold;
        public float DetailStrengthMin;
    }

    private static BlendedZoneGen SampleBlendedZoneGen(int wx, int wz, ZoneGenData[] zones)
    {
        var result = new BlendedZoneGen();
        int n = zones != null ? zones.Length : 0;
        if (n == 0) { return result; }

        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        GetZoneGenWeights(wx, wz, n, weights);

        for (int i = 0; i < n; i++)
        {
            float w = weights[i];
            if (w <= 0f) { continue; }
            ZoneGenData rg = zones[i];
            if (rg == null) { continue; }
            result.Elevation += rg.Elevation * w;
            result.ElevationRange += rg.ElevationRange * w;
            result.TunnelThreshold += rg.TunnelThreshold * w;
            result.CaveThreshold += rg.CaveThreshold * w;
            result.GrassThreshold += rg.GrassThreshold * w;
            result.ForestThreshold += rg.ForestThreshold * w;
            result.ForestDensity += rg.ForestDensity * w;
            result.DetailNoiseThreshold += rg.DetailNoiseThreshold * w;
            result.DetailStrengthMin += rg.DetailStrengthMin * w;
        }
        return result;
    }

    // Sample a zone index at column (wx, wz) weighted by the same chunk
    // smoothstep kernel that drives scalar blending. Use this for prop /
    // mob scene picks: in the kernel-overlap band between two zones, each
    // prop independently rolls which palette to draw from, so e.g. a
    // forest→desert seam reads as a few desert trees among forest pines and
    // vice versa rather than a hard line at the chunk boundary. Returns -1
    // if no zone has positive weight (caller skips the spawn).
    private static int PickWeightedZone(int wx, int wz, ZoneGenData[] zones, Random rng)
    {
        int n = zones != null ? zones.Length : 0;
        if (n == 0) { return -1; }

        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        GetZoneGenWeights(wx, wz, n, weights);

        float total = 0f;
        for (int i = 0; i < n; i++) { total += weights[i]; }
        if (total <= 1e-6f) { return -1; }

        float r = (float)rng.NextDouble() * total;
        float acc = 0f;
        for (int i = 0; i < n; i++)
        {
            acc += weights[i];
            if (r <= acc) { return i; }
        }
        return n - 1;
    }

    // Convenience: pick a scene from the kernel-weighted zone's palette.
    // Returns null when the chosen zone has no scenes authored.
    private static PackedScene PickWeightedScene(int wx, int wz, ZoneGenData[] zones,
        Func<ZoneGenData, PackedScene[]> selector, Random rng)
    {
        int idx = PickWeightedZone(wx, wz, zones, rng);
        if (idx < 0) { return null; }
        PackedScene[] scenes = selector(zones[idx]);
        if (scenes == null || scenes.Length == 0) { return null; }
        return scenes[rng.Next(scenes.Length)];
    }

    // Convenience: kernel-weighted zone pick that returns the data resource
    // (or null). Caller reads chance/scene/data fields off it for the spawn
    // roll. In the kernel-overlap band each cell rolls independently, so e.g.
    // the desert→forest seam ends up with a few forest goblins among desert
    // monsters and vice versa rather than a hard boundary.
    private static ZoneGenData PickWeightedZoneData(int wx, int wz, ZoneGenData[] zones, Random rng)
    {
        int idx = PickWeightedZone(wx, wz, zones, rng);
        if (idx < 0) { return null; }
        return zones[idx];
    }

    // Deterministic per-(wx, wz, salt) hash → [0, 1). Used to pick a
    // kernel-weighted zone per voxel without allocating a Random — same
    // worldSeed/coords always produce the same zone pick, so kit borders
    // (and any other deterministic per-voxel choice) replay identically
    // across runs and across save/load.
    private static float HashFloat01(int wx, int wz, int salt)
    {
        unchecked
        {
            uint h = (uint)wx * 0x9E3779B1u;
            h ^= (uint)wz * 0x85EBCA77u;
            h ^= (uint)salt * 0xC2B2AE3Du;
            h = ((h >> 16) ^ h) * 0x85EBCA6Bu;
            h = ((h >> 13) ^ h) * 0xC2B2AE35u;
            h = (h >> 16) ^ h;
            return (h & 0x00FFFFFF) / (float)0x01000000;
        }
    }

    // Same weighted pick as PickWeightedZone but driven by a precomputed
    // [0, 1) sample (HashFloat01). For deterministic per-voxel kit assignment
    // we want jagged zone borders that follow the kernel weights — a hash
    // of the voxel's column gives a stable noisy boundary instead of the
    // chunk-aligned orthogonal seam you'd get from `chunk.ZoneIndex`.
    private static int PickWeightedZoneFromHash(int wx, int wz, ZoneGenData[] zones, float r01)
    {
        int n = zones != null ? zones.Length : 0;
        if (n == 0) { return -1; }

        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        GetZoneGenWeights(wx, wz, n, weights);

        float total = 0f;
        for (int i = 0; i < n; i++) { total += weights[i]; }
        if (total <= 1e-6f) { return -1; }

        float r = r01 * total;
        float acc = 0f;
        for (int i = 0; i < n; i++)
        {
            acc += weights[i];
            if (r <= acc) { return i; }
        }
        return n - 1;
    }

    // Per-voxel salt for the kit-border hash. Distinct from any other hash
    // salt so kit borders don't correlate with future per-voxel decisions.
    private const int KIT_HASH_SALT = 0x4B495454; // "KITT"

    // Pick a zone for a kit stamp at (wx, wz). Falls back to the chunk's
    // ZoneIndex when the kernel produces no positive weight (off-world,
    // edge cases) so we always end up with a stamped kit.
    private static int PickKitZone(int wx, int wz, ZoneGenData[] zones, int fallbackZoneIndex)
    {
        int idx = PickWeightedZoneFromHash(wx, wz, zones, HashFloat01(wx, wz, KIT_HASH_SALT));
        return idx >= 0 ? idx : fallbackZoneIndex;
    }

    // Set at the end of Generate so debug dumps / console commands can
    // inspect the height field without the caller having to plumb it through.
    private static HeightMap? _lastHeightMap;
    private static int _lastPlateauStep = 1;

    // Writes three PPM images (plateau, height, ramp mask) and a stats text
    // file to `dir`. Called from the `worldgen_debug` console command and
    // from the headless auto-dump path in Main.
    public static void DumpDebug(string dir)
    {
        if (!_lastHeightMap.HasValue)
        {
            GD.PrintErr("worldgen_debug: no world has been generated yet.");
            return;
        }
        HeightMap hm = _lastHeightMap.Value;
        System.IO.Directory.CreateDirectory(dir);

        int sizeX = hm.Plateau.GetLength(0);
        int sizeZ = hm.Plateau.GetLength(1);
        int total = sizeX * sizeZ;

        var plateauHist = new Dictionary<int, int>();
        var heightHist = new Dictionary<int, int>();
        var liftHist = new Dictionary<int, int>();
        int rampCells = 0;
        int plateauCells = 0;
        int minP = int.MaxValue, maxP = int.MinValue;
        int minH = int.MaxValue, maxH = int.MinValue;
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                int p = hm.Plateau[x, z];
                int h = hm.Height[x, z];
                plateauHist.TryGetValue(p, out int pc); plateauHist[p] = pc + 1;
                heightHist.TryGetValue(h, out int hc); heightHist[h] = hc + 1;
                int lift = h - p;
                liftHist.TryGetValue(lift, out int lc); liftHist[lift] = lc + 1;
                if (lift > 0) { rampCells++; } else { plateauCells++; }
                if (p < minP) { minP = p; }
                if (p > maxP) { maxP = p; }
                if (h < minH) { minH = h; }
                if (h > maxH) { maxH = h; }
            }
        }

        // Plateau-transition count: how often adjacent columns have different
        // plateaus, split by |delta|. This is the key signal for "does the
        // plateau field actually look plateau-y, or is it a stippled mess?".
        var transHist = new Dictionary<int, int>();
        int transCount = 0;
        int adjPairs = 0;
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                int p = hm.Plateau[x, z];
                if (x + 1 < sizeX)
                {
                    int d = Math.Abs(hm.Plateau[x + 1, z] - p);
                    transHist.TryGetValue(d, out int tc); transHist[d] = tc + 1;
                    if (d > 0) { transCount++; }
                    adjPairs++;
                }
                if (z + 1 < sizeZ)
                {
                    int d = Math.Abs(hm.Plateau[x, z + 1] - p);
                    transHist.TryGetValue(d, out int tc); transHist[d] = tc + 1;
                    if (d > 0) { transCount++; }
                    adjPairs++;
                }
            }
        }

        using (var sw = new System.IO.StreamWriter($"{dir}/stats.txt"))
        {
            sw.WriteLine($"World: {sizeX} x {sizeZ} = {total} columns");
            sw.WriteLine($"RAMP_SLOPE={RAMP_SLOPE}, PlateauStep={_lastPlateauStep}, rampRadius={_lastPlateauStep * RAMP_SLOPE}");
            sw.WriteLine();
            sw.WriteLine($"Ramp cells: {rampCells} ({100.0 * rampCells / total:F1}%)");
            sw.WriteLine($"Plain plateau cells: {plateauCells} ({100.0 * plateauCells / total:F1}%)");
            sw.WriteLine($"Plateau range: [{minP}, {maxP}]");
            sw.WriteLine($"Height range:  [{minH}, {maxH}]");
            sw.WriteLine();
            sw.WriteLine($"Adjacent-plateau transitions: {transCount} / {adjPairs} pairs ({100.0 * transCount / adjPairs:F1}%)");
            sw.WriteLine("Transition-delta histogram (|Δplateau| between adjacent columns : count):");
            foreach (var kv in transHist.OrderBy(k => k.Key))
            {
                sw.WriteLine($"  {kv.Key,4}: {kv.Value}");
            }
            sw.WriteLine();
            sw.WriteLine("Plateau histogram:");
            foreach (var kv in plateauHist.OrderBy(k => k.Key))
            {
                sw.WriteLine($"  {kv.Key,4}: {kv.Value}");
            }
            sw.WriteLine();
            sw.WriteLine("Height histogram:");
            foreach (var kv in heightHist.OrderBy(k => k.Key))
            {
                sw.WriteLine($"  {kv.Key,4}: {kv.Value}");
            }
            sw.WriteLine();
            sw.WriteLine("Ramp lift histogram (h - plateau : count):");
            foreach (var kv in liftHist.OrderBy(k => k.Key))
            {
                sw.WriteLine($"  {kv.Key,4}: {kv.Value}");
            }
        }

        WritePlateauPpm($"{dir}/plateau.ppm", hm.Plateau, minP, maxP, _lastPlateauStep);
        WritePlateauPpm($"{dir}/height.ppm", hm.Height, minH, maxH, _lastPlateauStep);
        WriteRampPpm($"{dir}/ramp.ppm", hm);

        GD.Print($"worldgen_debug: wrote {dir}/stats.txt, plateau.ppm, height.ppm, ramp.ppm");
    }

    // Paints each plateau step as a distinct hue (so 4-voxel bands read as
    // flat color patches) and darkens within a band by the voxel offset from
    // the band's base (so ramp lift inside a band shows up as a gradient).
    private static void WritePlateauPpm(string path, int[,] field, int min, int max, int step)
    {
        int w = field.GetLength(0);
        int h = field.GetLength(1);
        using var fs = System.IO.File.Create(path);
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        fs.Write(header, 0, header.Length);
        byte[] row = new byte[w * 3];
        for (int z = h - 1; z >= 0; z--)
        {
            for (int x = 0; x < w; x++)
            {
                int v = field[x, z];
                int bandIndex = (int)Math.Floor((double)v / Math.Max(1, step));
                int within = v - bandIndex * step;
                // Cycle a 6-color palette by band; dim within the band.
                (byte r, byte g, byte b) palette = PaletteFor(bandIndex);
                float shade = 1f - 0.6f * within / Math.Max(1, step);
                row[x * 3 + 0] = (byte)Math.Clamp(palette.r * shade, 0, 255);
                row[x * 3 + 1] = (byte)Math.Clamp(palette.g * shade, 0, 255);
                row[x * 3 + 2] = (byte)Math.Clamp(palette.b * shade, 0, 255);
            }
            fs.Write(row, 0, row.Length);
        }
    }

    private static (byte r, byte g, byte b) PaletteFor(int bandIndex)
    {
        // Signed mod for stable coloring of negative bands.
        int m = ((bandIndex % 8) + 8) % 8;
        return m switch
        {
            0 => (80, 160, 80),    // green (plateau 0 = grass)
            1 => (180, 160, 80),   // tan
            2 => (200, 120, 80),   // orange
            3 => (180, 80, 80),    // red
            4 => (160, 80, 160),   // purple
            5 => (80, 80, 200),    // blue
            6 => (80, 160, 200),   // cyan
            _ => (200, 200, 80),   // yellow
        };
    }

    private static void WriteRampPpm(string path, HeightMap hm)
    {
        int w = hm.Plateau.GetLength(0);
        int h = hm.Plateau.GetLength(1);
        using var fs = System.IO.File.Create(path);
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        fs.Write(header, 0, header.Length);
        byte[] row = new byte[w * 3];
        for (int z = h - 1; z >= 0; z--)
        {
            for (int x = 0; x < w; x++)
            {
                int lift = hm.Height[x, z] - hm.Plateau[x, z];
                if (lift == 0)
                {
                    row[x * 3 + 0] = 32;
                    row[x * 3 + 1] = 32;
                    row[x * 3 + 2] = 32;
                }
                else
                {
                    // Bright red scales with lift magnitude.
                    int shade = Math.Clamp(80 + lift * 40, 0, 255);
                    row[x * 3 + 0] = (byte)shade;
                    row[x * 3 + 1] = 0;
                    row[x * 3 + 2] = 0;
                }
            }
            fs.Write(row, 0, row.Length);
        }
    }

    // Precomputed per-column height data for the whole world. Both arrays are
    // indexed as [wx - WorldMinX, wz - WorldMinZ].
    //
    //   Plateau: the "natural" terrain height — `round(noise / step) * step`,
    //            or 0 inside the spawn-flat zone. Always a plateau multiple.
    //
    //   Height:  the final integer surface height — plateau, optionally
    //            lifted by the ramp skirt cast from a nearby higher plateau.
    //            This is what chunk generation fills solid voxels up to.
    //
    // A column is a ramp iff Height > Plateau; otherwise it's a plain
    // plateau. This is the authored input to the mesher's shape-snapping rule.
    private readonly struct HeightMap
    {
        public readonly int WorldMinX;
        public readonly int WorldMinZ;
        public readonly int WorldMaxX;
        public readonly int WorldMaxZ;
        public readonly int[,] Plateau;
        public readonly int[,] Height;

        public HeightMap(int worldMinX, int worldMaxX, int worldMinZ, int worldMaxZ, int[,] plateau, int[,] height)
        {
            WorldMinX = worldMinX;
            WorldMaxX = worldMaxX;
            WorldMinZ = worldMinZ;
            WorldMaxZ = worldMaxZ;
            Plateau = plateau;
            Height = height;
        }

        public int GetHeight(int wx, int wz)
        {
            return Height[wx - WorldMinX, wz - WorldMinZ];
        }

        public int GetPlateau(int wx, int wz)
        {
            return Plateau[wx - WorldMinX, wz - WorldMinZ];
        }

        public bool IsRamp(int wx, int wz)
        {
            return GetHeight(wx, wz) > GetPlateau(wx, wz);
        }
    }

    // Build the integer height field for the whole world. Two passes:
    //   1. Per-column plateau: `round(noise / step) * step`, or 0 inside the
    //      spawn-flat zone.
    //   2. Ramp skirt: every non-flat column scans neighbors within
    //      `rampRadius` and, for each higher-plateau neighbor, contributes a
    //      candidate lift. The lift is capped at one plateau step above the
    //      column's own plateau so ramps always span exactly one step — a
    //      2-step cliff stays a cliff with a single-step ramp at its base,
    //      never a funny half-height hill. The column's final height is the
    //      maximum of its own plateau and every candidate.
    //
    // Lift formula: `targetPlateau - ceil(dist / slope)`. The ceiling matters
    // — with integer floor, dist=1 produces zero lift loss, so cells directly
    // adjacent to a higher plateau would stamp a bulge at plateau height.
    private static HeightMap BuildHeightMap(WorldState ws, WorldGenData genData,
        FastNoiseLite terrainNoise, FastNoiseLite rampGateNoise, FastNoiseLite elevationNoise)
    {
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;
        int sizeX = worldMaxX - worldMinX + 1;
        int sizeZ = worldMaxZ - worldMinZ + 1;

        int[,] plateau = new int[sizeX, sizeZ];
        bool[,] rampAnchor = new bool[sizeX, sizeZ];
        int step = Math.Max(1, (int)Math.Round(genData.PlateauStep));
        // Strict anchor band — `|pathNoise|` below this marks the core of a
        // ramp zone. Kept tight so anchors form thin, sparse meanders rather
        // than a blanket. Anchors get dilated below to give ramp skirts room
        // to form.
        const float RAMP_ANCHOR_BAND = 0.015f;

        // Half-amplitude (in plateau steps) added by the macro elevation
        // noise. ±1 step keeps the macro shape subtle relative to per-zone
        // ElevationRange so zones still drive the dominant terrain look.
        const float MACRO_ELEVATION_RANGE_PLATEAUS = 1f;

        // Far east of the world drops to ocean. Over this many chunks the
        // plateau-quantized inland height lerps down to OCEAN_DEPTH_PLATEAUS
        // below zero at the east edge. Kept narrow so the slope doesn't bite
        // into the bulk of coastal zones; widen for a sandier, gentler
        // coast.
        const int SHORELINE_CHUNKS = 2;
        const float OCEAN_DEPTH_PLATEAUS = 3f;
        float shorelineFalloffWidth = SHORELINE_CHUNKS * ChunkState.SIZE;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int lx = wx - worldMinX;
                int lz = wz - worldMinZ;

                // Step 1: kernel-blend authored center elevation + range
                // across zones. Eventually `Elevation` will be sampled from
                // an authored coarse heightmap; the blended ElevationRange
                // term still rides on top.
                BlendedZoneGen blend = SampleBlendedZoneGen(wx, wz, genData.Zones);

                // Step 2: weighted noise in plateau-step units.
                float terrainN = terrainNoise.GetNoise2D(wx, wz);
                float macroN = elevationNoise.GetNoise2D(wx, wz);
                float plateaus = blend.Elevation
                               + blend.ElevationRange * terrainN
                               + macroN * MACRO_ELEVATION_RANGE_PLATEAUS;

                // Step 3: plateau-step quantization (round to integer
                // plateau count). Done BEFORE the ocean falloff so cliffs
                // inland snap cleanly while the coast still gets a smooth
                // descent. Elevation = 0 is treated as sea level: the world-y
                // offset by WATER_LEVEL is applied at the end so authored
                // ZoneGenData.Elevation reads naturally — +1 means one
                // plateau step above sea level, -1 means one below.
                int plateauSteps = (int)Mathf.Round(plateaus);

                // Step 4: east-edge ocean falloff in plateau-step units.
                // Inland (coastT = 1) → unchanged plateauSteps. Coastal
                // (coastT → 0) → -OCEAN_DEPTH_PLATEAUS (deep ocean below
                // sea level).
                int distFromEastEdge = worldMaxX - wx;
                float coastT = Mathf.Clamp(distFromEastEdge / shorelineFalloffWidth, 0f, 1f);
                coastT = Mathf.SmoothStep(0f, 1f, coastT);
                int effectivePlateaus = (int)Mathf.Round(
                    Mathf.Lerp(-OCEAN_DEPTH_PLATEAUS, plateauSteps, coastT));

                // Step 5: convert plateau steps → world voxels with
                // Elevation = 0 anchored at sea level. Sea is at WATER_LEVEL
                // (= -1 plateau step in voxel units), so a plateau-step value
                // of 0 lands at WATER_LEVEL and each unit of Elevation /
                // ElevationRange shifts the surface by exactly one plateau
                // step (4 voxels) above or below the water plane.
                plateau[lx, lz] = WATER_LEVEL + effectivePlateaus * step;
                rampAnchor[lx, lz] = Math.Abs(rampGateNoise.GetNoise2D(wx, wz)) < RAMP_ANCHOR_BAND;
            }
        }

        // Dilate anchor mask by `rampRadius` cells — one full scan-radius's
        // worth on each side of the raw anchor line, so the lift scan has a
        // fully eligible neighbourhood and always produces a complete skirt.
        bool[,] rampEligible = new bool[sizeX, sizeZ];
        int rampRadiusConst = step * RAMP_SLOPE;
        int dilateRadius = rampRadiusConst;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                for (int dx = -dilateRadius; dx <= dilateRadius && !rampEligible[lx, lz]; dx++)
                {
                    int nx = lx + dx;
                    if (nx < 0 || nx >= sizeX)
                    {
                        continue;
                    }
                    for (int dz = -dilateRadius; dz <= dilateRadius; dz++)
                    {
                        int nz = lz + dz;
                        if (nz < 0 || nz >= sizeZ)
                        {
                            continue;
                        }
                        if (rampAnchor[nx, nz])
                        {
                            rampEligible[lx, lz] = true;
                            break;
                        }
                    }
                }
            }
        }

        int[,] height = new int[sizeX, sizeZ];
        // One step of rise takes `step * RAMP_SLOPE` horizontal cells; that's
        // also the scan radius since anything farther would only contribute
        // a zero (or clamped-away) lift.
        int rampRadius = step * RAMP_SLOPE;
        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int lx = wx - worldMinX;
                int lz = wz - worldMinZ;

                int myPlateau = plateau[lx, lz];
                int best = myPlateau;

                // Spawn-flat columns must stay at y=0. Non-ramp-eligible
                // columns skip the scan too: only cells inside the dilated
                // ramp band can be lifted.
                if (rampEligible[lx, lz])
                {
                    int oneStepUp = myPlateau + step;
                    for (int dx = -rampRadius; dx <= rampRadius; dx++)
                    {
                        int nx = wx + dx;
                        if (nx < worldMinX || nx > worldMaxX)
                        {
                            continue;
                        }
                        for (int dz = -rampRadius; dz <= rampRadius; dz++)
                        {
                            int nz = wz + dz;
                            if (nz < worldMinZ || nz > worldMaxZ)
                            {
                                continue;
                            }
                            if (dx == 0 && dz == 0)
                            {
                                continue;
                            }
                            int neighborPlateau = plateau[nx - worldMinX, nz - worldMinZ];
                            if (neighborPlateau <= myPlateau)
                            {
                                continue;
                            }
                            // Clamp to one step up so a taller plateau farther
                            // out can't out-vote a closer single-step plateau.
                            int target = Math.Min(neighborPlateau, oneStepUp);
                            int dist = Math.Max(Math.Abs(dx), Math.Abs(dz));
                            int verticalDrop = (dist + RAMP_SLOPE - 1) / RAMP_SLOPE;
                            int candidate = target - verticalDrop;
                            if (candidate > best)
                            {
                                best = candidate;
                            }
                        }
                    }
                }

                height[lx, lz] = best;
            }
        }

        return new HeightMap(worldMinX, worldMaxX, worldMinZ, worldMaxZ, plateau, height);
    }

    // True iff (wx, wz) is a flat, dry land column suitable for prop / mob /
    // grass spawning. "Flat" = no ramp lift (Height == Plateau). "Dry" = the
    // surface sits above water level — river-carved columns dip below
    // WATER_LEVEL and fall out. The legacy "plateau == 0" rule is gone:
    // per-zone BaseElevation now lifts each zone's surface to its own
    // platform height, so anchoring on world y=0 would only let swamp (which
    // happens to have BaseElevation near 0) ever spawn props.
    private static bool IsFlatDryGrassAt(int wx, int wz, HeightMap heightMap)
    {
        int h = heightMap.GetHeight(wx, wz);
        // h is the topmost solid voxel; the walkable surface sits at h+1, so
        // "above water" is h+1 > WATER_LEVEL, i.e. h >= WATER_LEVEL. Strict
        // greater-than was wrong: it excluded shoreline plateaus (h=WATER_LEVEL)
        // whose top voxel sits exactly at the water plane but whose air-above
        // is still dry — exactly the band where forest's noise dips would
        // otherwise plant trees.
        return h == heightMap.GetPlateau(wx, wz) && h >= WATER_LEVEL;
    }

    // Plateau-step tunnels: the top TunnelLayerHeight voxels of every plateau
    // step (the band immediately under each plateau ceiling) are tunnel
    // candidates, gated by 3D tunnel noise. This produces tiered tunnel
    // systems whose ceilings line up with plateau elevations and whose
    // openings show up in cliff faces between adjacent plateau levels.
    private static bool IsTunnelAt(int wx, int wy, int wz, FastNoiseLite tunnelNoise, WorldGenData genData)
    {
        if (wy <= WATER_LEVEL)
        {
            return false;
        }
        int step = Math.Max(1, (int)Math.Round(genData.PlateauStep));
        int rem = ((wy % step) + step) % step;
        if (rem < step - genData.TunnelLayerHeight)
        {
            return false;
        }
        // Sample at the band's base (rem=0 row) so all voxels in the band
        // share the same noise value — guarantees the band carves all-or-nothing
        // and never leaves sub-3-tall openings. Math.Floor (not C# integer
        // division) so negative wy snaps down, not toward zero.
        int bandBase = (int)Math.Floor((double)wy / step) * step;
        float threshold = SampleBlendedZoneGen(wx, wz, genData.Zones).TunnelThreshold;
        return Mathf.Abs(tunnelNoise.GetNoise3D(wx, bandBase, wz)) < threshold;
    }

    private static void GenerateChunk(ChunkState data, WorldGenData genData,
        FastNoiseLite tunnelNoise, HeightMap heightMap)
    {
        int chunkWorldX = data.ChunkCoord.X * ChunkState.SIZE;
        int chunkWorldY = data.ChunkCoord.Y * ChunkState.SIZE;
        int chunkWorldZ = data.ChunkCoord.Z * ChunkState.SIZE;
        int tunnelStep = Math.Max(1, (int)Math.Round(genData.PlateauStep));

        // True iff this column's surface reaches the plateau ceiling above wy,
        // so the rem=0 voxel at bandTop is solid and can serve as a tunnel
        // roof. Without this, columns whose solidHeight lands mid-band would
        // carve a tunnel with no ceiling above it, producing 1- and 2-tall
        // openings the player can't fit through.
        bool ColumnSupportsTunnel(int wy, int columnSolidHeight)
        {
            int bandTop = (int)Math.Floor((double)wy / tunnelStep) * tunnelStep + tunnelStep;
            return columnSolidHeight >= bandTop;
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int z = 0; z < ChunkState.SIZE; z++)
            {
                int wx = chunkWorldX + x;
                int wz = chunkWorldZ + z;

                int solidHeight = heightMap.GetHeight(wx, wz);

                // Per-column shape: the topmost solid voxel (the surface) gets
                // None for ramp columns, Y for plateau columns. All buried
                // voxels stamp Y regardless — a ramp's softness must not leak
                // downward into caves or other surfaces that happen to share
                // the column. The mesher's "any soft voxel on Y wins" rule
                // then propagates the ramp surface's softness horizontally
                // into the adjacent plateau column's surface cell, so the
                // ramp base blends smoothly into the plateau.
                bool isRamp = heightMap.IsRamp(wx, wz);
                byte surfaceShape = (byte)(isRamp ? VoxelTypeInfo.SharpAxes.None : VoxelTypeInfo.SharpAxes.Y);

                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    int wy = chunkWorldY + y;

                    // Caves carve only when the column reaches the plateau
                    // ceiling above the band — that ceiling row (rem=0) is
                    // never carved by IsCaveAt, so it serves as the cave roof
                    // and we get a guaranteed full-height cave.
                    bool solid = wy <= solidHeight
                        && !(ColumnSupportsTunnel(wy, solidHeight) && IsTunnelAt(wx, wy, wz, tunnelNoise, genData));

                    if (!solid)
                    {
                        if (wy <= WATER_LEVEL)
                        {
                            data.Voxels[x, y, z] = VoxelType.Water;
                        }
                        // Fog isn't placed here — it's a separate pass after
                        // GenerateCaves so it can see the final geometry and
                        // skip enclosed spaces. See GenerateFog.
                        continue;
                    }

                    // Every natural solid voxel is Terrain — the shader picks
                    // the tile from the per-voxel KitId (see below) + surface
                    // normal.y. The 27-voxel majority vote in the mesher now
                    // resolves to Terrain everywhere, so cliff faces, cave
                    // walls, and seabeds all flow through the AUTO branch and
                    // read from their kit's palette. Explicit materials
                    // (Wood/Stone walls from structures) overwrite this later
                    // via SetVoxelWorld and take the non-AUTO shader path.
                    data.Voxels[x, y, z] = VoxelType.Terrain;

                    // Cave interior surfaces always snap flat, regardless of
                    // whether the outdoor ridge above this column is a plateau
                    // or a path-band ramp. Without this override a cave carved
                    // under a ramp column inherits the column's None tag, the
                    // ceiling vertex interpolates downward, and the ceiling
                    // pokes below the clip plane into the player's view.
                    // A voxel is a cave surface if the cell directly above it
                    // OR directly below it is a carved tunnel cell.
                    bool aboveIsCarved = wy + 1 <= solidHeight
                        && ColumnSupportsTunnel(wy + 1, solidHeight)
                        && IsTunnelAt(wx, wy + 1, wz, tunnelNoise, genData);
                    bool belowIsCarved = wy - 1 >= 0
                        && ColumnSupportsTunnel(wy - 1, solidHeight)
                        && IsTunnelAt(wx, wy - 1, wz, tunnelNoise, genData);
                    byte voxelShape = wy == solidHeight ? surfaceShape : (byte)VoxelTypeInfo.SharpAxes.Y;
                    data.Shape[x, y, z] = (aboveIsCarved || belowIsCarved)
                        ? (byte)VoxelTypeInfo.SharpAxes.Y
                        : voxelShape;

                    // Kit assignment: default every solid voxel to a zone's
                    // surface kit (slot 0). The zone is picked per-column
                    // by hash-weighted kernel, so the boundary between
                    // adjacent zones' palettes is jagged — desert sand
                    // peppered into forest grass within the kernel overlap
                    // band — instead of a straight chunk-aligned seam.
                    // TagSubmergedKits runs after all chunks/water exist and
                    // re-tags the submerged shell to the underwater slot based
                    // on actual water adjacency, so buried rock under
                    // above-water cliffs stays surface-kit (no sand bleed on
                    // cliff faces). Cave interiors are later re-tagged to the
                    // cave slot by MarkCaveSurfaceShapes.
                    int kitZone = PickKitZone(wx, wz, genData.Zones, data.ZoneIndex);
                    data.KitId[x, y, z] = GlobalKit(kitZone, KIT_SLOT_TEMPERATE);
                }
            }
        }
    }

    // Bucket-fill ground fog. For each zone we compute a "fog level" Y_i
    // by pouring a humidity-scaled volume into the zone's heightmap (sorted
    // floor heights, water-clamped) and finding where it settles. Per voxel
    // the level blends across zones via the prop/kit kernel; density falls
    // off linearly with distance below the level. Only open-to-sky voxels
    // get seeded — caves / tunnels stay fog-free.
    private static void GenerateFog(WorldState ws, WorldGenData genData, HeightMap heightMap)
    {
        int zoneCount = genData.Zones != null ? genData.Zones.Length : 0;
        if (zoneCount == 0)
        {
            return;
        }

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        // Step 1 — gather each zone's column floor heights, water-clamped.
        // Floors below WATER_LEVEL get pinned there: water surface IS the
        // bucket bottom over submerged columns (we don't fog underwater).
        var zoneFloors = new List<int>[zoneCount];
        for (int i = 0; i < zoneCount; i++)
        {
            zoneFloors[i] = new List<int>();
        }
        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int chunkX = (int)Math.Floor((double)wx / ChunkState.SIZE);
                int chunkZ = (int)Math.Floor((double)wz / ChunkState.SIZE);
                int zoneIdx = PickZoneIndex(new Vector3I(chunkX, 0, chunkZ), zoneCount);
                int h = heightMap.GetHeight(wx, wz);
                zoneFloors[zoneIdx].Add(Math.Max(h, WATER_LEVEL));
            }
        }

        // Step 2 — solve bucket-fill per zone. Humidity * volume-per-column
        // gives the total fog volume to pour; SolveBucketFill returns the
        // resulting level Y. Floors are sorted in place (cheap; we only walk
        // the list once after this).
        float[] fogLevelY = new float[zoneCount];
        for (int i = 0; i < zoneCount; i++)
        {
            List<int> floors = zoneFloors[i];
            if (floors.Count == 0)
            {
                fogLevelY[i] = float.NegativeInfinity;
                continue;
            }
            floors.Sort();
            float humidity = 0f;
            ZoneGenData rg = genData.Zones[i];
            if (rg?.Zone?.weather != null)
            {
                humidity = rg.Zone.weather.humidity;
            }
            float desiredVolume = humidity * FOG_VOLUME_PER_HUMIDITY * floors.Count;
            fogLevelY[i] = SolveBucketFill(floors, desiredVolume);
        }

        // Step 3 — stamp fog density per voxel under the kernel-blended level.
        Span<float> weights = zoneCount <= 32
            ? stackalloc float[zoneCount]
            : new float[zoneCount];

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                // Highest non-air voxel: air above it is open-to-sky; air at
                // or below is enclosed (cave / tunnel / under-overhang) and
                // stays fog-free.
                int highestNonAir = worldMinY - 1;
                for (int wy = worldMaxY; wy >= worldMinY; wy--)
                {
                    if (ws.GetVoxelWorld(wx, wy, wz) != VoxelType.Air)
                    {
                        highestNonAir = wy;
                        break;
                    }
                }
                int fogStartY = Math.Max(highestNonAir + 1, WATER_LEVEL + 1);
                if (fogStartY > worldMaxY) { continue; }

                // Per-column blended fog level. Skip when no neighbouring
                // zone offers a level — empty kernel.
                GetZoneGenWeights(wx, wz, zoneCount, weights);
                float blendedLevel = 0f;
                float wSum = 0f;
                for (int i = 0; i < zoneCount; i++)
                {
                    if (weights[i] <= 0f) { continue; }
                    if (float.IsNegativeInfinity(fogLevelY[i])) { continue; }
                    blendedLevel += weights[i] * fogLevelY[i];
                    wSum += weights[i];
                }
                if (wSum < 1e-6f) { continue; }
                blendedLevel /= wSum;

                int ceilingY = (int)Math.Floor(blendedLevel);
                if (ceilingY < fogStartY) { continue; }

                for (int wy = fogStartY; wy <= ceilingY && wy <= worldMaxY; wy++)
                {
                    if (ws.GetVoxelWorld(wx, wy, wz) != VoxelType.Air)
                    {
                        continue;
                    }
                    float depth = blendedLevel - wy;
                    int density = (int)Mathf.Clamp(depth * FOG_DENSITY_PER_VOXEL, 0f, FOG_MAX_DENSITY);
                    if (density > 0)
                    {
                        ws.SetFogWorld(wx, wy, wz, density);
                    }
                }
            }
        }
    }

    // Bucket-fill: given column floor heights sorted ascending and a desired
    // total volume V (in voxel-units of integrated air below the level), find
    // Y such that sum_c max(0, Y - floors[c]) = V. Volume vs Y is non-decreasing
    // piecewise-linear; we walk segments [floors[k], floors[k+1]] (slope k+1
    // since k+1 columns sit below Y in that segment) and solve linearly in
    // the segment that contains the target volume.
    private static float SolveBucketFill(List<int> sortedFloors, float desiredVolume)
    {
        int n = sortedFloors.Count;
        if (n == 0)
        {
            return 0f;
        }
        if (desiredVolume <= 0f)
        {
            return sortedFloors[0];
        }
        float v = 0f;
        for (int k = 0; k < n - 1; k++)
        {
            int slope = k + 1;
            int dh = sortedFloors[k + 1] - sortedFloors[k];
            if (dh <= 0) { continue; }
            float segVol = (float)slope * dh;
            if (v + segVol >= desiredVolume)
            {
                return sortedFloors[k] + (desiredVolume - v) / slope;
            }
            v += segVol;
        }
        // All floors submerged in the bucket — slope is n above floors[n-1].
        return sortedFloors[n - 1] + (desiredVolume - v) / n;
    }

    // Swiss-cheese caves: 3D noise carves blob-shaped holes through solid
    // terrain. Floors follow the noise surface (smooth); ceilings snap up to
    // the next plateau-step boundary so the rem=0 row above each cave stays
    // solid and acts as a flat roof. Caves never breach the surface and are
    // discarded if shorter than CaveMinHeight, guaranteeing walkable paths.
    private static void GenerateCaves(WorldState ws, WorldGenData genData, FastNoiseLite caveNoise)
    {
        int step = Math.Max(1, (int)Math.Round(genData.PlateauStep));
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        bool IsNaturallyCarved(int wx, int wy, int wz, float caveThreshold)
        {
            return Math.Abs(caveNoise.GetNoise3D(wx, wy, wz)) > caveThreshold;
        }

        // Highest solid (non-Air, non-Water) voxel in this column. Anything
        // above is sky; we never want to carve into sky (no craters).
        int FindSurface(int wx, int wz)
        {
            for (int wy = worldMaxY; wy >= worldMinY; wy--)
            {
                var v = ws.GetVoxelWorld(wx, wy, wz);
                if (v != VoxelType.Air && v != VoxelType.Water)
                {
                    return wy;
                }
            }
            return worldMinY - 1;
        }

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int surfaceY = FindSurface(wx, wz);
                if (surfaceY <= worldMinY)
                {
                    continue;
                }

                // Threshold blends per-column so cave density transitions
                // smoothly across zone borders. Sampled once per column
                // since the kernel is XZ-only.
                float caveThreshold = SampleBlendedZoneGen(wx, wz, genData.Zones).CaveThreshold;

                // Walk the column bottom-up finding runs of natural carve.
                // worldMinY is preserved as bedrock, so start one above.
                int wy = worldMinY + 1;
                while (wy <= surfaceY)
                {
                    if (!IsNaturallyCarved(wx, wy, wz, caveThreshold))
                    {
                        wy++;
                        continue;
                    }
                    int runLo = wy;
                    while (wy <= surfaceY && IsNaturallyCarved(wx, wy, wz, caveThreshold))
                    {
                        wy++;
                    }
                    int runHi = wy - 1;

                    // Snap top up to the next plateau-step boundary. If the
                    // snap reaches above surface, that's fine — the cave just
                    // breaches as an open-topped pit.
                    int ceilingY = (int)Math.Floor((double)runHi / step) * step + step;
                    if (ceilingY - runLo < genData.CaveMinHeight)
                    {
                        continue;
                    }

                    for (int cy = runLo; cy < ceilingY; cy++)
                    {
                        var fill = cy <= WATER_LEVEL ? VoxelType.Water : VoxelType.Air;
                        ws.SetVoxelWorld(wx, cy, wz, fill);
                    }

                    // Force Y on the solid voxels bracketing the carved run
                    // (cave ceiling at ceilingY, cave floor at runLo-1), so the
                    // cave surface snaps flat regardless of whether this
                    // column's outdoor height came from the ramp branch of the
                    // height function. Cave interior geometry is its own ruleset.
                    ws.SetShapeWorld(wx, ceilingY, wz, VoxelTypeInfo.SharpAxes.Y);
                    if (runLo - 1 >= worldMinY)
                    {
                        ws.SetShapeWorld(wx, runLo - 1, wz, VoxelTypeInfo.SharpAxes.Y);
                    }
                }
            }
        }
    }

    // One-off test stamp: a wide shallow underwater lake in the mountain
    // (NE) zone, pushed inland toward the desert border. The ceiling is
    // pinned to the first plateau above water (WATER_LEVEL + PlateauStep)
    // so the cavern stays low even where the natural mountain surface
    // climbs higher. Columns whose natural surface already sits below the
    // ceiling merge into the existing terrain instead of getting a
    // synthetic ceiling stamped over them. MarkCaveSurfaceShapes runs
    // after this and will tag the carved walls / floor / ceiling as
    // cave-kit + SharpAxes.Y. Hardcoded constants — this does not survive
    // editor-authored worlds; remove once underground-water visuals are
    // signed off.
    private static void GenerateTestUnderwaterLake(WorldState ws, WorldGenData genData)
    {
        const int CenterX = 20;
        const int CenterZ = 32;
        const int HalfSize = 25;            // 50x50 footprint
        const int FloorY = WATER_LEVEL - 3; // 4 voxels of standing water

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;

        int step = Math.Max(1, (int)Math.Round(genData.PlateauStep));
        int ceilingY = WATER_LEVEL + step;

        for (int dx = -HalfSize; dx < HalfSize; dx++)
        {
            for (int dz = -HalfSize; dz < HalfSize; dz++)
            {
                int wx = CenterX + dx;
                int wz = CenterZ + dz;

                // Topmost natural solid voxel in the column. Carving stops
                // here so we never punch a hole in the sky for shoreline
                // columns whose surface already dips below the lake ceiling.
                int naturalSurfaceY = worldMinY - 1;
                for (int wy = worldMaxY; wy >= worldMinY; wy--)
                {
                    var v = ws.GetVoxelWorld(wx, wy, wz);
                    if (v != VoxelType.Air && v != VoxelType.Water)
                    {
                        naturalSurfaceY = wy;
                        break;
                    }
                }

                int topCarve = Math.Min(ceilingY - 1, naturalSurfaceY);
                for (int wy = FloorY + 1; wy <= topCarve; wy++)
                {
                    var fill = wy <= WATER_LEVEL ? VoxelType.Water : VoxelType.Air;
                    ws.SetVoxelWorld(wx, wy, wz, fill);
                }

                // Stamp a solid floor only where the natural seabed sat
                // above it. Where the column was already deeper than FloorY
                // (open ocean), leave the existing geometry so the lake
                // merges seamlessly with the sea.
                if (naturalSurfaceY > FloorY)
                {
                    ws.SetVoxelWorld(wx, FloorY, wz, VoxelType.Terrain,
                        VoxelTypeInfo.SharpAxes.Y);
                }
            }
        }
    }

    // Sweep every column, find the topmost solid voxel, and mark any *buried*
    // solid voxel (i.e. below the top) that borders a cave-interior air/water
    // cell as SharpAxes.Y + KIT_CAVE. This is the authored form of "cave
    // interior surfaces snap flat" — ceilings, floors, lateral walls of
    // tunnels, and noise-carved island voxels all qualify.
    //
    // "Cave-interior" air is air/water that has solid above it somewhere in
    // its column — i.e. enclosed by a ceiling. A cliff face voxel ALSO sits
    // "buried" in its own (tall) column and is adjacent to air, but that air
    // is open to sky (no solid above in its own shorter column), so the
    // cave check filters it out. Without that filter every cliff face gets
    // KIT_CAVE, whose FlatTile is sand, and stone cliffs grow sand bases.
    private static void MarkCaveSurfaceShapes(WorldState ws, WorldGenData genData)
    {
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        int sizeX = worldMaxX - worldMinX + 1;
        int sizeZ = worldMaxZ - worldMinZ + 1;
        int[,] columnTopY = new int[sizeX, sizeZ];
        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int t = worldMinY - 1;
                for (int wy = worldMaxY; wy >= worldMinY; wy--)
                {
                    var v = ws.GetVoxelWorld(wx, wy, wz);
                    if (VoxelTypeInfo.IsSolid(v) && v != VoxelType.Barrier)
                    {
                        t = wy;
                        break;
                    }
                }
                columnTopY[wx - worldMinX, wz - worldMinZ] = t;
            }
        }

        bool IsCaveAirOrWater(int wx, int wy, int wz)
        {
            int ax = wx - worldMinX;
            int az = wz - worldMinZ;
            if (ax < 0 || ax >= sizeX || az < 0 || az >= sizeZ) { return false; }
            var v = ws.GetVoxelWorld(wx, wy, wz);
            if (v != VoxelType.Air && v != VoxelType.Water) { return false; }
            // "Has solid above" = under a ceiling = cave interior, not cliff-side open sky.
            return wy < columnTopY[ax, az];
        }

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int topY = columnTopY[wx - worldMinX, wz - worldMinZ];
                if (topY <= worldMinY)
                {
                    continue;
                }

                for (int wy = worldMinY; wy < topY; wy++)
                {
                    var v = ws.GetVoxelWorld(wx, wy, wz);
                    if (!VoxelTypeInfo.IsSolid(v) || v == VoxelType.Barrier)
                    {
                        continue;
                    }
                    if (IsCaveAirOrWater(wx - 1, wy, wz)
                        || IsCaveAirOrWater(wx + 1, wy, wz)
                        || IsCaveAirOrWater(wx, wy - 1, wz)
                        || IsCaveAirOrWater(wx, wy + 1, wz)
                        || IsCaveAirOrWater(wx, wy, wz - 1)
                        || IsCaveAirOrWater(wx, wy, wz + 1))
                    {
                        ws.SetShapeWorld(wx, wy, wz, VoxelTypeInfo.SharpAxes.Y);
                        // Stamp this chunk's zone cave kit so the shader
                        // can paint it distinctly from the surface above.
                        // Overrides the underwater slot for submerged caves —
                        // the cave palette wins there.
                        int zoneIdx = PickKitZone(wx, wz, genData.Zones, ZoneIndexAtWorld(ws, wx, wy, wz));
                        ws.SetKitIdWorld(wx, wy, wz, GlobalKit(zoneIdx, KIT_SLOT_CAVE));
                    }
                }
            }
        }
    }

    // Chebyshev radius for the water-adjacency search in TagSubmergedKits.
    // Must be >= 2: the mesher's kit vote is a 3x3x3 neighbourhood around a
    // DC cell corner, so a seabed cell's vote sees one layer of seabed (would
    // be KIT_UNDERWATER) plus one layer of buried rock just below (would be
    // surface slot). With a 1-voxel shell the two layers tie at 9–9 and the
    // lower-index kit (temperate) wins — seabed reads as grass. A 2-voxel
    // shell tags both layers underwater so the vote goes 18–0.
    private const int SUBMERGED_KIT_RADIUS = 2;

    // Re-tag solid voxels at or below WATER_LEVEL to KIT_UNDERWATER iff they
    // sit within SUBMERGED_KIT_RADIUS of a water voxel. Runs after every
    // chunk exists so the water pass has already filled every non-solid
    // wy<=WATER_LEVEL cell with VoxelType.Water. Semantic "near water" beats
    // the old "wy<=WATER_LEVEL" rule because the latter paints deeply buried
    // rock under cliffs as underwater — then the mesher's 27-voxel kit vote
    // for nearby DC cells drags that sand onto the visible cliff face.
    private static void TagSubmergedKits(WorldState ws, WorldGenData genData)
    {
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                for (int wy = worldMinY; wy <= WATER_LEVEL; wy++)
                {
                    var v = ws.GetVoxelWorld(wx, wy, wz);
                    if (!VoxelTypeInfo.IsSolid(v) || v == VoxelType.Barrier)
                    {
                        continue;
                    }

                    bool nearWater = false;
                    for (int dy = -SUBMERGED_KIT_RADIUS; dy <= SUBMERGED_KIT_RADIUS && !nearWater; dy++)
                    {
                        for (int dx = -SUBMERGED_KIT_RADIUS; dx <= SUBMERGED_KIT_RADIUS && !nearWater; dx++)
                        {
                            for (int dz = -SUBMERGED_KIT_RADIUS; dz <= SUBMERGED_KIT_RADIUS && !nearWater; dz++)
                            {
                                if (ws.GetVoxelWorld(wx + dx, wy + dy, wz + dz) == VoxelType.Water)
                                {
                                    nearWater = true;
                                }
                            }
                        }
                    }

                    if (nearWater)
                    {
                        int zoneIdx = PickKitZone(wx, wz, genData.Zones, ZoneIndexAtWorld(ws, wx, wy, wz));
                        ws.SetKitIdWorld(wx, wy, wz, GlobalKit(zoneIdx, KIT_SLOT_UNDERWATER));
                    }
                }
            }
        }
    }

    // Overlay id values. 0 = no overlay. A non-zero OverlayId is a direct
    // tile_array base-layer index sampled by voxel_clip.gdshader with its
    // own alpha channel driving blend strength. Any tile in the global
    // catalog can be used as an overlay — add new OVERLAY_* constants rather
    // than reusing numbers so .hike files written with old values keep
    // mapping to the right tile when new tiles are added ahead of them.
    private const byte OVERLAY_NONE = 0;
    private const byte OVERLAY_DIRT = VoxelTypeInfo.TILE_DIRT_OVERLAY;
    private const byte OVERLAY_FIELD = VoxelTypeInfo.TILE_FIELD_OVERLAY;

    // How many voxels above/below `wy` to scan the neighbor column for its
    // local surface. Anything beyond this is treated as a cliff and skipped
    // (the kit's wall tile already paints cliff faces; overlays are for
    // walkable slopes the smooth normal can't see).
    private const int EDGE_SCAN_WINDOW = 4;
    // Neighbor-diff threshold at/above which we stamp OVERLAY_EDGE. 1 = any
    // non-flat cardinal neighbor (1-voxel bumps, ramps). Raise to 2+ for
    // smaller overlay coverage.
    private const int EDGE_MIN_DIFF = 1;
    // Neighbor-diff at/above which we stop stamping edge — the feature is a
    // real cliff and the wall band owns it.
    private const int EDGE_MAX_DIFF = 3;

    // Placement tuning for the procedural overlay scatter. Noise patches at
    // these frequencies produce ~10–30-voxel blobs; thresholds choose
    // roughly 10–20% coverage per type, with field winning over dirt where
    // they overlap. Feel free to tweak or replace with authored placement.
    private const int OVERLAY_DIRT_SEED = 4242;
    private const int OVERLAY_FIELD_SEED = 7373;
    private const float OVERLAY_DIRT_FREQ = 0.2f;
    private const float OVERLAY_FIELD_FREQ = 0.015f;
    private const float OVERLAY_DIRT_THRESHOLD = 0.9f;
    private const float OVERLAY_FIELD_THRESHOLD = 0.10f;

    // Test placement for detail-sprite scatter. Paints DetailGroup=1 (i.e.
    // ZoneGenData.DetailGroups[0]) on temperate surface voxels wherever
    // detailNoise exceeds the threshold; strength is the noise value remapped
    // to [DetailStrengthMin, 255]. Replace with authored brushes once the
    // editor lands; the runtime is happy with no DetailGroups configured (the
    // scatter pass short-circuits) so this is safe to leave on.
    private const int DETAIL_NOISE_SEED = 9191;

    // Noise-scatter dirt and field overlays on temperate-kit surface voxels.
    // Only top-surface voxels (solid with air above) are candidates so buried
    // geometry and cliff faces stay untouched. Kit gate restricts placement
    // to temperate — sand (underwater/cave) and cave palette stay clean.
    //
    // roadColumns carries the (wx, wz) mask of cobblestone road columns; those
    // are skipped entirely AND given a one-voxel buffer of suppression so the
    // mesher's 27-voxel overlay vote at road edges isn't polluted by field or
    // dirt stamped one voxel outside the road.
    private const int ROAD_OVERLAY_BUFFER = 1;
    private static void StampProceduralOverlays(WorldState ws, HashSet<(int, int)> roadColumns)
    {
        bool NearRoad(int wx, int wz)
        {
            for (int dx = -ROAD_OVERLAY_BUFFER; dx <= ROAD_OVERLAY_BUFFER; dx++)
            {
                for (int dz = -ROAD_OVERLAY_BUFFER; dz <= ROAD_OVERLAY_BUFFER; dz++)
                {
                    if (roadColumns.Contains((wx + dx, wz + dz)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        var dirtNoise = new FastNoiseLite();
        dirtNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        dirtNoise.Seed = OVERLAY_DIRT_SEED;
        dirtNoise.Frequency = OVERLAY_DIRT_FREQ;
        dirtNoise.FractalOctaves = 2;

        var fieldNoise = new FastNoiseLite();
        fieldNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        fieldNoise.Seed = OVERLAY_FIELD_SEED;
        fieldNoise.Frequency = OVERLAY_FIELD_FREQ;
        fieldNoise.FractalOctaves = 2;

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                if (NearRoad(wx, wz))
                {
                    continue;
                }
                for (int wy = worldMinY; wy < worldMaxY; wy++)
                {
                    if (!IsSurfaceVoxel(ws, wx, wy, wz))
                    {
                        continue;
                    }
                    if (KitSlot(ws.GetKitIdWorld(wx, wy, wz)) != KIT_SLOT_TEMPERATE)
                    {
                        continue;
                    }

                    // Field wins — denser grass masks muddy ground beneath.
                    if (fieldNoise.GetNoise2D(wx, wz) > OVERLAY_FIELD_THRESHOLD)
                    {
                        ws.SetOverlayIdWorld(wx, wy, wz, OVERLAY_FIELD);
                        continue;
                    }
                    if (dirtNoise.GetNoise2D(wx, wz) > OVERLAY_DIRT_THRESHOLD)
                    {
                        ws.SetOverlayIdWorld(wx, wy, wz, OVERLAY_DIRT);
                    }
                }
            }
        }
    }

    // Cobblestone road placement.
    //
    // Roads are drawn by a set of random walkers that start at scattered
    // points on dry land and step one cell at a time along an 8-connected
    // grid. Each step:
    //   - prefers to continue in the current heading, falling back to small
    //     left/right offsets if the heading is blocked;
    //   - only accepts neighbours with |height delta| <= 1, so ramps are
    //     followed naturally and cliffs / water are avoided by construction;
    //   - occasionally spawns a perpendicular branch walker.
    // The walker centres are dilated to a 5-voxel-wide disk per step so the
    // road reads as a continuous ribbon through turns and diagonals. The
    // resulting column mask is used twice: stamped as OverlayId on the
    // surface voxel in each column, and handed to StampDetailScatter so
    // grass sprites do not spawn on the pavement.
    private const int ROAD_HALF_WIDTH = 2;           // 5-wide disk (radius 2.5).
    private const int ROAD_DISK_R2 = 6;              // dx*dx+dz*dz threshold.
    private const float ROAD_TURN_CHANCE = 0.15f;    // Per step, nudge heading ±1.
    private const float ROAD_BRANCH_CHANCE = 0.004f; // Per step, spawn perpendicular walker.
    private const int ROAD_MAX_WALKERS = 16;         // Safety cap on branching.
    private const int ROAD_START_SPACING = 32;       // World voxels per start walker.
    private const int ROAD_MIN_STARTS = 2;
    private const int ROAD_START_ATTEMPTS = 32;      // Max tries to land a start on dry land.

    private static HashSet<(int, int)> GenerateRoads(WorldState ws, HeightMap heightMap, WorldGenData genData, int worldSeed)
    {
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldExtent = Math.Max(worldMaxX - worldMinX, worldMaxZ - worldMinZ);
        int maxSteps = Math.Max(32, worldExtent * 2);
        int startCount = Math.Max(ROAD_MIN_STARTS, worldExtent / ROAD_START_SPACING);

        var roadColumns = new HashSet<(int, int)>();
        // Roads share the worldSeed but use their own salt so they don't
        // correlate with the ramp/path noise for the same seed.
        var rng = new Random(DeriveSeed(worldSeed, SEED_SALT_ROAD));

        // 8-connected directions, in a ring so (dir+1)%8 is a 45° turn.
        var dirs = new (int dx, int dz)[]
        {
            ( 1,  0), ( 1,  1), ( 0,  1), (-1,  1),
            (-1,  0), (-1, -1), ( 0, -1), ( 1, -1),
        };

        bool CanWalkOnto(int wx, int wz, int fromHeight)
        {
            if (wx < worldMinX || wx > worldMaxX || wz < worldMinZ || wz > worldMaxZ)
            {
                return false;
            }
            int h = heightMap.GetHeight(wx, wz);
            if (h <= WATER_LEVEL)
            {
                return false;
            }
            return Math.Abs(h - fromHeight) <= 1;
        }

        // Flood fill from (cx, cz) out to the 5-disk, 4-connected, only
        // crossing between columns whose heights differ by <= 1. This keeps
        // the paint on a single walkable surface: when the walker straddles
        // a cliff edge, the disk cells on the other side of the cliff fail
        // the per-step height check and stay unpainted, so the road doesn't
        // smear half onto one plateau and half onto its neighbour.
        //
        // Ramps still paint fully — their per-step delta is 1 by construction
        // — so a road that follows a ramp gets a continuous cobblestone
        // ribbon up the slope.
        var floodQueue = new Queue<(int x, int z)>();
        var floodVisited = new HashSet<(int, int)>();
        void StampDisk(int cx, int cz)
        {
            floodQueue.Clear();
            floodVisited.Clear();
            floodQueue.Enqueue((cx, cz));
            floodVisited.Add((cx, cz));
            int centerH = heightMap.GetHeight(cx, cz);
            if (centerH <= WATER_LEVEL)
            {
                return;
            }
            while (floodQueue.Count > 0)
            {
                var (x, z) = floodQueue.Dequeue();
                roadColumns.Add((x, z));
                int h = heightMap.GetHeight(x, z);
                // 4-connected: diagonal neighbours would let the fill squeeze
                // across a corner between two columns at different heights
                // (e.g. A=0, B=2 diagonally) even when both cardinal edges
                // are cliffs.
                int[] ndx = { 1, -1, 0, 0 };
                int[] ndz = { 0, 0, 1, -1 };
                for (int i = 0; i < 4; i++)
                {
                    int nx = x + ndx[i];
                    int nz = z + ndz[i];
                    int ox = nx - cx;
                    int oz = nz - cz;
                    if (ox * ox + oz * oz > ROAD_DISK_R2)
                    {
                        continue;
                    }
                    if (nx < worldMinX || nx > worldMaxX || nz < worldMinZ || nz > worldMaxZ)
                    {
                        continue;
                    }
                    if (!floodVisited.Add((nx, nz)))
                    {
                        continue;
                    }
                    int nh = heightMap.GetHeight(nx, nz);
                    if (nh <= WATER_LEVEL)
                    {
                        continue;
                    }
                    if (Math.Abs(nh - h) > 1)
                    {
                        continue;
                    }
                    floodQueue.Enqueue((nx, nz));
                }
            }
        }

        var walkers = new Queue<(int wx, int wz, int dir, int stepsLeft)>();
        for (int s = 0; s < startCount; s++)
        {
            for (int attempt = 0; attempt < ROAD_START_ATTEMPTS; attempt++)
            {
                int sx = rng.Next(worldMinX, worldMaxX + 1);
                int sz = rng.Next(worldMinZ, worldMaxZ + 1);
                if (heightMap.GetHeight(sx, sz) <= WATER_LEVEL)
                {
                    continue;
                }
                walkers.Enqueue((sx, sz, rng.Next(0, 8), maxSteps));
                break;
            }
        }

        while (walkers.Count > 0)
        {
            var (wx, wz, dir, stepsLeft) = walkers.Dequeue();
            for (int step = 0; step < stepsLeft; step++)
            {
                StampDisk(wx, wz);

                if (rng.NextDouble() < ROAD_TURN_CHANCE)
                {
                    dir = (dir + (rng.Next(2) == 0 ? -1 : 1) + 8) % 8;
                }

                if (rng.NextDouble() < ROAD_BRANCH_CHANCE && walkers.Count < ROAD_MAX_WALKERS)
                {
                    // ±90° so branches read as intersections, not grazing forks.
                    int branchDir = (dir + (rng.Next(2) == 0 ? -2 : 2) + 8) % 8;
                    walkers.Enqueue((wx, wz, branchDir, stepsLeft - step));
                }

                // Try heading first, then ±45°, then ±90°. A walker that
                // can't find any walkable neighbour within ±90° ends — the
                // road has hit a cliff / dead-end and shouldn't spawn a U-turn.
                int curH = heightMap.GetHeight(wx, wz);
                int[] offsets = { 0, -1, 1, -2, 2 };
                bool moved = false;
                foreach (int o in offsets)
                {
                    int tryDir = (dir + o + 8) % 8;
                    int nx = wx + dirs[tryDir].dx;
                    int nz = wz + dirs[tryDir].dz;
                    if (!CanWalkOnto(nx, nz, curH))
                    {
                        continue;
                    }
                    wx = nx;
                    wz = nz;
                    dir = tryDir;
                    moved = true;
                    break;
                }
                if (!moved)
                {
                    break;
                }
            }
        }

        // Cobblestone overlay disabled for now — the walker still runs so
        // roadColumns suppresses dirt/field overlays and grass scatter (the
        // path-smoothing footprint stays visible as cleaner ground), but the
        // path tile itself isn't painted until we settle on path art.

        return roadColumns;
    }

    // Test placement for the painted detail-sprite scatter. Walks every
    // temperate-kit surface voxel and stamps DetailGroup=1 with a noise-driven
    // strength wherever detailNoise > threshold. The runtime scatter pass
    // looks up DetailGroups[0] for group=1 and silently does nothing if the
    // palette is empty, so this is safe to leave on even before any sprite art
    // is authored. Columns covered by a cobblestone road are skipped so grass
    // blades don't poke through the pavement.
    private static void StampDetailScatter(WorldState ws, WorldGenData genData, HashSet<(int, int)> roadColumns)
    {
        var detailNoise = new FastNoiseLite();
        detailNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        detailNoise.Seed = DETAIL_NOISE_SEED;
        // Frequency comes from the first zone (single shared FastNoiseLite
        // for the whole world). Threshold and StrengthMin DO blend per-column
        // below so density can fade across zone borders even though the
        // underlying noise pattern is constant.
        detailNoise.Frequency = FirstZoneGen(genData)?.DetailNoiseFrequency ?? 0.06f;
        detailNoise.FractalOctaves = 2;

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                // Paved roads suppress grass sprites — the cobblestone is
                // authored as a ground surface, not a clearing in the grass.
                if (roadColumns.Contains((wx, wz)))
                {
                    continue;
                }
                for (int wy = worldMinY; wy < worldMaxY; wy++)
                {
                    if (!IsSurfaceVoxel(ws, wx, wy, wz))
                    {
                        continue;
                    }
                    if (KitSlot(ws.GetKitIdWorld(wx, wy, wz)) != KIT_SLOT_TEMPERATE)
                    {
                        continue;
                    }

                    BlendedZoneGen blend = SampleBlendedZoneGen(wx, wz, genData.Zones);
                    float n = detailNoise.GetNoise2D(wx, wz);
                    if (n <= blend.DetailNoiseThreshold)
                    {
                        continue;
                    }

                    // Map noise (threshold..1) to (strengthMin..255).
                    float t = (n - blend.DetailNoiseThreshold) / Math.Max(0.0001f, 1f - blend.DetailNoiseThreshold);
                    int strengthMin = (int)Math.Round(blend.DetailStrengthMin);
                    int strength = strengthMin + (int)(t * (255 - strengthMin));
                    ws.SetDetailGroupWorld(wx, wy, wz, 1);
                    ws.SetDetailStrengthWorld(wx, wy, wz, strength);
                }
            }
        }
    }

    // Stamp OVERLAY_DIRT on "surface voxels" (solid with air directly above)
    // whose local neighborhood slope is in [EDGE_MIN_DIFF, EDGE_MAX_DIFF-1].
    // Per-voxel, not per-column: correctly handles cave floors, overhangs, and
    // ledges because the ±EDGE_SCAN_WINDOW clip keeps each voxel's comparison
    // local to its own walkable layer.
    private static void StampEdgeOverlays(WorldState ws)
    {
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                for (int wy = worldMinY; wy < worldMaxY; wy++)
                {
                    if (!IsSurfaceVoxel(ws, wx, wy, wz))
                    {
                        continue;
                    }

                    int maxDiff = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        int nx = wx + dx[i];
                        int nz = wz + dz[i];
                        int neighborDiff = FindNearestSurfaceDiff(ws, nx, wy, nz, EDGE_SCAN_WINDOW);
                        if (neighborDiff > maxDiff)
                        {
                            maxDiff = neighborDiff;
                        }
                    }

                    if (maxDiff >= EDGE_MIN_DIFF && maxDiff < EDGE_MAX_DIFF)
                    {
                        ws.SetOverlayIdWorld(wx, wy, wz, OVERLAY_DIRT);
                    }
                }
            }
        }
    }

    // True iff the voxel at (wx, wy, wz) is solid (non-Barrier) and has air or
    // water directly above. That's the definition of "walkable surface" used by
    // the overlay pass — applies to plateau tops, cave floors, ledges alike.
    private static bool IsSurfaceVoxel(WorldState ws, int wx, int wy, int wz)
    {
        var self = ws.GetVoxelWorld(wx, wy, wz);
        if (!VoxelTypeInfo.IsSolid(self) || self == VoxelType.Barrier)
        {
            return false;
        }
        var above = ws.GetVoxelWorld(wx, wy + 1, wz);
        return !VoxelTypeInfo.IsSolid(above) || above == VoxelType.Barrier;
    }

    // Returns the vertical distance from `wy` to the nearest surface voxel in
    // the column at (wx, wz), searching ±window. Returns `window` if no
    // surface is found (treat as cliff — no overlay).
    private static int FindNearestSurfaceDiff(WorldState ws, int wx, int wy, int wz, int window)
    {
        int best = window;
        for (int d = 0; d <= window; d++)
        {
            if (IsSurfaceVoxel(ws, wx, wy + d, wz))
            {
                if (d < best) { best = d; }
                break;
            }
            if (d != 0 && IsSurfaceVoxel(ws, wx, wy - d, wz))
            {
                if (d < best) { best = d; }
                break;
            }
        }
        return best;
    }

    private static void GenerateProps(WorldState ws, Vector3I chunkCoord, WorldGenData genData,
        FastNoiseLite grassNoise, FastNoiseLite forestNoise, HeightMap heightMap, int skipFlags, int worldSeed)
    {
        bool skipProps = (skipFlags & SKIP_PROPS) != 0;
        bool skipMobs = (skipFlags & SKIP_MOBS) != 0;
        bool skipInteractives = (skipFlags & SKIP_INTERACTIVES) != 0;
        // Grass requires both the heightmap "this is a flat dry column" check
        // AND the actual surface voxel being solid with air directly above —
        // caves can carve through the surface, in which case props would
        // otherwise float over an open hole. Surface y comes from the
        // heightmap so the check works at any per-zone BaseElevation.
        int SurfaceYAt(int wx, int wz) => heightMap.GetHeight(wx, wz);
        bool IsGrassyAt(int wx, int wz)
        {
            if (!IsFlatDryGrassAt(wx, wz, heightMap))
            {
                return false;
            }
            int sy = SurfaceYAt(wx, wz);
            var ground = ws.GetVoxelWorld(wx, sy, wz);
            if (ground == VoxelType.Air || ground == VoxelType.Water)
            {
                return false;
            }
            return ws.GetVoxelWorld(wx, sy + 1, wz) == VoxelType.Air;
        }
        ChunkState data = ws._chunks[chunkCoord];
        var rng = new Random(StableMix(DeriveSeed(worldSeed, SEED_SALT_PROPS), chunkCoord.X, chunkCoord.Z));

        // Per-chunk tree count blends the kernel-weighted TreesPerChunkMin/Max
        // around the chunk center so adjacent zones hand off smoothly. The
        // SCENE picked for each individual tree is drawn from a kernel-weighted
        // zone per (wx, wz) — that produces the overlap zone where two
        // zones' palettes intermix instead of snapping at the chunk border.
        ZoneGenData[] zonesArr = genData.Zones ?? System.Array.Empty<ZoneGenData>();
        int chunkCenterWx = chunkCoord.X * ChunkState.SIZE + ChunkState.SIZE / 2;
        int chunkCenterWz = chunkCoord.Z * ChunkState.SIZE + ChunkState.SIZE / 2;
        float treesMinBlend = 0f;
        float treesMaxBlend = 0f;
        if (zonesArr.Length > 0)
        {
            Span<float> centerWeights = zonesArr.Length <= 32
                ? stackalloc float[zonesArr.Length]
                : new float[zonesArr.Length];
            GetZoneGenWeights(chunkCenterWx, chunkCenterWz, zonesArr.Length, centerWeights);
            for (int i = 0; i < zonesArr.Length; i++)
            {
                if (zonesArr[i] == null) { continue; }
                treesMinBlend += centerWeights[i] * zonesArr[i].TreesPerChunkMin;
                treesMaxBlend += centerWeights[i] * zonesArr[i].TreesPerChunkMax;
            }
        }
        int treesPerChunkMin = (int)Math.Round(treesMinBlend);
        int treesPerChunkMax = (int)Math.Round(treesMaxBlend);
        int treeCount = treesPerChunkMax >= treesPerChunkMin
            ? rng.Next(treesPerChunkMin, treesPerChunkMax + 1)
            : 0;

        var treedCells = new HashSet<(int, int)>();

        bool TryPlaceTree(int localX, int localZ)
        {
            if (treedCells.Contains((localX, localZ)))
            {
                return false;
            }
            int wx = chunkCoord.X * ChunkState.SIZE + localX;
            int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
            if (!IsGrassyAt(wx, wz))
            {
                return false;
            }
            PackedScene scene = PickWeightedScene(wx, wz, zonesArr, r => r.TreeScenes, rng);
            if (scene == null)
            {
                return false;
            }
            int sy = SurfaceYAt(wx, wz);
            // +1.5 (not +1) because ChunkMesherDC's shallow-Y smoothing
            // averages a flat grass column's top face to 0.5 above the
            // voxel-grid top — anchoring at +1 buries sprites half a voxel
            // into the visible ground.
            ws.AddEntity(new PropSimState(PropType.Tree,
                new Vector3(wx + 0.5f, sy + 1.5f, wz + 0.5f),
                scene));
            treedCells.Add((localX, localZ));
            return true;
        }

        if (!skipProps)
        {
            for (int i = 0; i < treeCount; i++)
            {
                TryPlaceTree(rng.Next(1, ChunkState.SIZE - 1), rng.Next(1, ChunkState.SIZE - 1));
            }

            // Forest pockets: where forest noise is high, attempt a tree at every
            // grid cell with density that ramps up from the threshold. Sampling
            // per cell (not per chunk) means forest edges fade naturally instead
            // of snapping on chunk seams.
            for (int localX = 0; localX < ChunkState.SIZE; localX++)
            {
                for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
                {
                    int wx = chunkCoord.X * ChunkState.SIZE + localX;
                    int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                    BlendedZoneGen blend = SampleBlendedZoneGen(wx, wz, genData.Zones);
                    float f = forestNoise.GetNoise2D(wx, wz);
                    if (f < blend.ForestThreshold)
                    {
                        continue;
                    }
                    float t = (f - blend.ForestThreshold) / Math.Max(0.0001f, 1f - blend.ForestThreshold);
                    float density = blend.ForestDensity * Mathf.Clamp(t, 0f, 1f);
                    if (rng.NextDouble() >= density)
                    {
                        continue;
                    }
                    TryPlaceTree(localX, localZ);
                }
            }

            for (int localX = 0; localX < ChunkState.SIZE; localX++)
            {
                for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
                {
                    int wx = chunkCoord.X * ChunkState.SIZE + localX;
                    int wz = chunkCoord.Z * ChunkState.SIZE + localZ;

                    float grassThreshold = SampleBlendedZoneGen(wx, wz, genData.Zones).GrassThreshold;
                    if (grassNoise.GetNoise2D(wx, wz) < grassThreshold)
                    {
                        continue;
                    }
                    if (!IsGrassyAt(wx, wz))
                    {
                        continue;
                    }

                    PackedScene grassScene = PickWeightedScene(wx, wz, zonesArr, r => r.TallGrassScenes, rng);
                    if (grassScene == null)
                    {
                        continue;
                    }
                    int sy = SurfaceYAt(wx, wz);
                    ws.AddEntity(new PropSimState(PropType.TallGrass, new Vector3(wx + 0.5f, sy + 1.5f, wz + 0.5f), grassScene));
                }
            }
        }

        if (!skipMobs)
        {
            // Generate goblins on grass surfaces
            for (int localX = 0; localX < ChunkState.SIZE; localX++)
            {
                for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
                {
                    int wx = chunkCoord.X * ChunkState.SIZE + localX;
                    int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                    if (!IsGrassyAt(wx, wz))
                    {
                        continue;
                    }
                    ZoneGenData rg = PickWeightedZoneData(wx, wz, zonesArr, rng);
                    if (rg == null || rg.GoblinScene == null || rg.GoblinData == null)
                    {
                        continue;
                    }
                    if (rng.NextDouble() >= rg.GoblinSpawnOutsideNighttime)
                    {
                        continue;
                    }

                    int sy = SurfaceYAt(wx, wz);
                    var mobState = new MobSimState(
                        new Vector3(wx + 0.5f, sy + 1.5f, wz + 0.5f),
                        (float)(rng.NextDouble() * Mathf.Pi * 2f),
                        rg.GoblinScene,
                        rg.GoblinData
                    );
                    mobState.SpawnAtNight = true;
                    if (rng.NextDouble() < 0.25f)
                    {
                        mobState.InitialBehavior = "Wander";
                    }
                    ws.AddEntity(mobState);
                }
            }

            // Generate kun_kun on grass surfaces. Non-dangerous mobs that flee the
            // player and burrow once they get far enough away.
            for (int localX = 0; localX < ChunkState.SIZE; localX++)
            {
                for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
                {
                    int wx = chunkCoord.X * ChunkState.SIZE + localX;
                    int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                    if (!IsGrassyAt(wx, wz))
                    {
                        continue;
                    }
                    ZoneGenData rg = PickWeightedZoneData(wx, wz, zonesArr, rng);
                    if (rg == null || rg.KunKunScene == null || rg.KunKunData == null)
                    {
                        continue;
                    }
                    if (rng.NextDouble() >= rg.KunKunChance)
                    {
                        continue;
                    }

                    int sy = SurfaceYAt(wx, wz);
                    ws.AddEntity(new MobSimState(
                        new Vector3(wx + 0.5f, sy + 1.5f, wz + 0.5f),
                        (float)(rng.NextDouble() * Mathf.Pi * 2f),
                        rg.KunKunScene,
                        rg.KunKunData
                    ));
                }
            }
        }

        if (!skipInteractives && genData.CampfireScene != null)
        {
            // Generate campfires on grass surfaces. Authored at ~1/5 the goblin
            // rate per zone; AutoLightAtNight is set so they ignite when their
            // chunk activates after dark.
            for (int localX = 0; localX < ChunkState.SIZE; localX++)
            {
                for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
                {
                    int wx = chunkCoord.X * ChunkState.SIZE + localX;
                    int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                    if (!IsGrassyAt(wx, wz))
                    {
                        continue;
                    }
                    ZoneGenData rg = PickWeightedZoneData(wx, wz, zonesArr, rng);
                    if (rg == null)
                    {
                        continue;
                    }
                    if (rng.NextDouble() >= rg.CampfireSpawnOutside)
                    {
                        continue;
                    }

                    int sy = SurfaceYAt(wx, wz);
                    var campfire = new TorchSimState(
                        new Vector3(wx + 0.5f, sy + 1.5f, wz + 0.5f),
                        genData.CampfireScene);
                    campfire.AutoLightAtNight = true;
                    campfire.Active = false;
                    ws.AddEntity(campfire);
                }
            }
        }

        if (!skipInteractives)
        {
            // Generate loot on grass surfaces
            for (int localX = 0; localX < ChunkState.SIZE; localX++)
            {
                for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
                {
                    int wx = chunkCoord.X * ChunkState.SIZE + localX;
                    int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                    if (!IsGrassyAt(wx, wz))
                    {
                        continue;
                    }
                    ZoneGenData rg = PickWeightedZoneData(wx, wz, zonesArr, rng);
                    if (rg == null || rg.LootScene == null)
                    {
                        continue;
                    }
                    if (rng.NextDouble() >= rg.LootChance)
                    {
                        continue;
                    }

                    int sy = SurfaceYAt(wx, wz);
                    ws.AddEntity(new PropSimState(
                        PropType.AutoLoot,
                        new Vector3(wx + 0.5f, sy + 1.5f, wz + 0.5f),
                        rg.LootScene
                    ));
                }
            }

            // Generate chests on grass surfaces
            for (int localX = 0; localX < ChunkState.SIZE; localX++)
            {
                for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
                {
                    int wx = chunkCoord.X * ChunkState.SIZE + localX;
                    int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                    if (!IsGrassyAt(wx, wz))
                    {
                        continue;
                    }
                    ZoneGenData rg = PickWeightedZoneData(wx, wz, zonesArr, rng);
                    if (rg == null || rg.ChestScene == null || rg.LootScene == null)
                    {
                        continue;
                    }
                    if (rng.NextDouble() >= rg.ChestChance)
                    {
                        continue;
                    }

                    int sy = SurfaceYAt(wx, wz);
                    int lootCount = rng.Next(rg.ChestLootCountMin, rg.ChestLootCountMax + 1);
                    PackedScene chestScene = rng.NextDouble() < 0.5 && PoisonChestScene != null
                        ? PoisonChestScene
                        : rg.ChestScene;
                    ws.AddEntity(new ChestSimState(new Vector3(wx + 0.5f, sy + 1.5f, wz + 0.5f),
                        chestScene,
                        lootCount,
                        rg.LootScene));
                }
            }
        }

        // Cave pockets: scan the full vertical column and spawn mobs/chests/
        // loot/torches anywhere there's a 2-voxel air pocket with a solid
        // floor and a ceiling within reach (the "is enclosed" test is what
        // distinguishes cave pockets from open surface).
        const int HEAD_CLEARANCE = 2;
        const int CAVE_CEILING_PROBE = 6;
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        for (int localX = 0; localX < ChunkState.SIZE; localX++)
        {
            for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
            {
                int wx = chunkCoord.X * ChunkState.SIZE + localX;
                int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                for (int wy = worldMinY + 1; wy <= worldMaxY - HEAD_CLEARANCE; wy++)
                {
                    var below = ws.GetVoxelWorld(wx, wy - 1, wz);
                    if (below == VoxelType.Air || below == VoxelType.Water)
                    {
                        continue;
                    }
                    bool clear = true;
                    for (int c = 0; c < HEAD_CLEARANCE; c++)
                    {
                        if (ws.GetVoxelWorld(wx, wy + c, wz) != VoxelType.Air)
                        {
                            clear = false;
                            break;
                        }
                    }
                    if (!clear)
                    {
                        continue;
                    }
                    bool isCave = false;
                    for (int c = HEAD_CLEARANCE; c <= CAVE_CEILING_PROBE; c++)
                    {
                        if (ws.GetVoxelWorld(wx, wy + c, wz) != VoxelType.Air)
                        {
                            isCave = true;
                            break;
                        }
                    }
                    if (!isCave)
                    {
                        continue;
                    }

                    var pos = new Vector3(wx + 0.5f, wy, wz + 0.5f);
                    ZoneGenData rg = PickWeightedZoneData(wx, wz, zonesArr, rng);
                    if (!skipMobs && rg != null && rg.GoblinScene != null && rg.GoblinData != null
                        && rng.NextDouble() < rg.GoblinSpawnUnderground)
                    {
                        var mobState = new MobSimState(pos,
                            (float)(rng.NextDouble() * Mathf.Pi * 2f),
                            rg.GoblinScene, rg.GoblinData);
                        if (rng.NextDouble() < 0.25f)
                        {
                            mobState.InitialBehavior = "Wander";
                        }
                        ws.AddEntity(mobState);
                    }
                    if (!skipMobs && rg != null && rg.KunKunScene != null && rg.KunKunData != null
                        && rng.NextDouble() < rg.KunKunChance)
                    {
                        ws.AddEntity(new MobSimState(pos,
                            (float)(rng.NextDouble() * Mathf.Pi * 2f),
                            rg.KunKunScene, rg.KunKunData));
                    }
                    if (!skipInteractives && rg != null && rg.LootScene != null
                        && rng.NextDouble() < rg.LootChance)
                    {
                        ws.AddEntity(new PropSimState(PropType.AutoLoot, pos, rg.LootScene));
                    }
                    if (!skipInteractives && rg != null && rg.ChestScene != null && rg.LootScene != null
                        && rng.NextDouble() < rg.ChestChance)
                    {
                        int lootCount = rng.Next(rg.ChestLootCountMin, rg.ChestLootCountMax + 1);
                        PackedScene chestScene = rng.NextDouble() < 0.5 && PoisonChestScene != null
                            ? PoisonChestScene
                            : rg.ChestScene;
                        ws.AddEntity(new ChestSimState(pos, chestScene, lootCount, rg.LootScene));
                    }
                    if (!skipInteractives && rng.NextDouble() < genData.CaveTorchChance)
                    {
                        ws.AddEntity(new TorchSimState(pos, genData.TorchScene));
                    }
                }
            }
        }
    }

    private static void ShuffleArray<T>(Random rng, T[] array)
    {
        for (int i = array.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }
}
