using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

public static class WorldGen
{
    // Manual logic-version stamp. Bump when ANY change to this file (or any
    // helper it calls) would alter generated output for the same inputs —
    // tuning a default threshold, changing a noise frequency, reordering a
    // placement pass, etc. WorldGenCache rolls this into its fingerprint so
    // every bump invalidates all cached worlds. WorldGenData .tres edits are
    // detected automatically by content-hashing and don't require a bump.
    public const int WORLDGEN_VERSION = 6;

    // Bitmask flags for the worldgen_skip CVar — see CVars.worldgenSkip.
    // Each category is checked independently inside GenerateProps; setting
    // SKIP_ALL turns the prop pass off entirely.
    public const int SKIP_DETAILS = 1;       // painted detail-sprite scatter
    public const int SKIP_PROPS = 2;         // trees + tall grass
    public const int SKIP_MOBS = 4;          // goblins, kun_kun (surface + cave)
    public const int SKIP_INTERACTIVES = 8;  // loot (surface + cave) + chests (cave)
    public const int SKIP_ALL = SKIP_DETAILS | SKIP_PROPS | SKIP_MOBS | SKIP_INTERACTIVES;

    // What role a kit plays in worldgen. Not authored on the kit itself —
    // derived at palette-build time from how each zone references the kit
    // (SurfaceKit / CaveKit / SubmergedKit), so the kit doesn't have to repeat
    // information the zone already encodes. WorldGen builds a parallel
    // purpose-by-paletteIndex array and the worldgen passes that need to gate
    // on "is this voxel on a surface kit?" (field/dirt overlays, scatter noise
    // pick, road suppression) read that array.
    public enum EKitPurpose
    {
        Surface = 0,
        Cave = 1,
        Submerged = 2,
        Shore = 3,
    }


    // Active kit and terrain palettes for the run currently inside Generate().
    // Built once at the top of Generate from the supplied WorldGenData and
    // used by per-pass kit-stamp helpers. Static because WorldGen is a static
    // class with many free-standing methods; lifetime is one Generate call
    // (the kit palette is also kept resident afterwards so authoring tools
    // like WorldEditor can read TreeScenes etc. via ActiveKitPalette).
    //
    // The terrain id stored in voxels indexes both arrays at the same slot:
    // _activeKitPalette[i] is the worldgen-side wrapper (TerrainKitData),
    // _activeTerrainPalette[i] is its `.Terrain` (the runtime visual / footstep
    // / tile entry). ChunkMesh uploads only _activeTerrainPalette; worldgen
    // passes that need scatter / forest tunings call ResolveKit for the
    // gen-side data.
    private static TerrainKitData[] _activeKitPalette;
    private static TerrainData[] _activeTerrainPalette;
    private static System.Collections.Generic.Dictionary<TerrainKitData, byte> _kitIndex;
    private static DetailGroupData[] _activeDetailPalette;
    private static System.Collections.Generic.Dictionary<DetailGroupData, byte> _detailIndex;

    // The WorldGenData for the run currently inside Generate(). Set at the top
    // of Generate alongside the palettes, so the many free-standing static
    // helpers below can read authored tuning (blend radii, overlay scatter,
    // ramp slope, etc.) without threading genData through every signature —
    // same lifetime/rationale as the active palettes. The `?? <literal>`
    // fallbacks at the few read sites keep behaviour identical if a helper is
    // ever called outside a Generate run (e.g. a debug dump before first gen).
    private static WorldGenData _activeGenData;

    // Public read-only views of the active palettes. The terrain array is
    // what ChunkMesh uploads to the shader; the kit palette is for authoring
    // tools (e.g. the editor's spawn dropdown reads TreeScenes from here).
    // Both arrays mirror each other in shape — same length, same indices.
    // ActiveDetailPalette is what ChunkMesh.SetDetailGroups consumes; per-voxel
    // DetailGroup bytes are 1-based indices into it.
    public static TerrainData[] ActiveTerrainPalette => _activeTerrainPalette;
    public static TerrainKitData[] ActiveKitPalette => _activeKitPalette;
    public static DetailGroupData[] ActiveDetailPalette => _activeDetailPalette;

    // Per-paletteIndex purpose classification, derived from how each zone
    // references its kits at palette-build time. A kit referenced as
    // SurfaceKit lands as Surface, CaveKit as Cave, SubmergedKit as
    // Submerged. The same kit referenced in multiple slots across zones
    // takes the first-encountered classification (zones declared earlier
    // win); current data has no such overlap. byte values: 0/1/2 follow
    // EKitPurpose; 0xFF means "not classified" (kit is in the palette but
    // no zone listed it in any slot — defensive only, since BuildKitPalette
    // only adds kits via zone refs).
    private static byte[] _kitPurposes;
    private const byte KIT_PURPOSE_NONE = 0xFF;

    // Resolve a gen kit ref to its global palette byte for stamping into
    // ChunkState.TerrainId. Null or unknown kits map to 0 — palette index 0 is
    // a debug fallback (no kit configured). The byte indexes both the runtime
    // (_activeTerrainPalette) and gen (_activeKitPalette) arrays.
    private static byte TerrainIdOf(TerrainKitData kit)
    {
        if (kit == null || _kitIndex == null) { return 0; }
        return _kitIndex.TryGetValue(kit, out byte i) ? i : (byte)0;
    }

    // Inverse of TerrainIdOf — resolve a stored TerrainId byte to its runtime
    // TerrainData. Used by anything that needs FlatTile/WallTile/GroundTint/etc.
    // Out-of-range or empty palette returns null.
    private static TerrainData ResolveTerrain(int TerrainId)
    {
        if (_activeTerrainPalette == null) { return null; }
        if (TerrainId < 0 || TerrainId >= _activeTerrainPalette.Length) { return null; }
        return _activeTerrainPalette[TerrainId];
    }

    // Resolve a stored TerrainId byte to its gen kit. Used by worldgen passes that
    // need DefaultDetail / DetailNoise* / Forest* / TreeScenes etc.
    private static TerrainKitData ResolveKit(int TerrainId)
    {
        if (_activeKitPalette == null) { return null; }
        if (TerrainId < 0 || TerrainId >= _activeKitPalette.Length) { return null; }
        return _activeKitPalette[TerrainId];
    }

    // True iff the kit at this palette index was classified as Surface at
    // palette-build time. False for Cave / Submerged / out-of-range. Used
    // by passes that gate on "is this voxel on walkable above-water ground?"
    // — field/dirt overlay stamping, surface scatter noise pick, road
    // suppression on the scatter pass.
    private static bool IsSurfaceKit(int TerrainId)
    {
        if (_kitPurposes == null) { return false; }
        if (TerrainId < 0 || TerrainId >= _kitPurposes.Length) { return false; }
        return _kitPurposes[TerrainId] == (byte)EKitPurpose.Surface;
    }

    // Resolve a detail-group ref to its 1-based stamp value for
    // ChunkState.DetailGroup. Returns 0 ("no detail") if the group is null
    // or wasn't included in the active palette.
    private static byte DetailIndexOf(DetailGroupData group)
    {
        if (group == null || _detailIndex == null) { return 0; }
        // Stored 0-based; the per-voxel channel is 1-based with 0 = none.
        return _detailIndex.TryGetValue(group, out byte i) ? (byte)(i + 1) : (byte)0;
    }

    // Walks the zone array and returns a deduplicated GEN kit palette in zone
    // declaration order (zone 0's SurfaceKit first, then CaveKit, then
    // SubmergedKit, then ShoreKit, then zone 1's, etc.) skipping nulls and any
    // gen kit already present. Two zones that share a gen kit cost one palette
    // slot. Index 0 is the first non-null kit encountered. TerrainId bytes stored
    // per voxel index into both this array and its runtime sibling (see
    // ExtractTerrainPalette) — both are uploaded by Main / ChunkMesh.
    public static TerrainKitData[] BuildKitPalette(ZoneGenData[] zones)
    {
        var list = new System.Collections.Generic.List<TerrainKitData>();
        var seen = new System.Collections.Generic.HashSet<TerrainKitData>();
        if (zones != null)
        {
            foreach (ZoneGenData z in zones)
            {
                if (z == null) { continue; }
                AddIfNew(z.SurfaceKit, list, seen);
                AddIfNew(z.CaveKit, list, seen);
                AddIfNew(z.SubmergedKit, list, seen);
                AddIfNew(z.ShoreKit, list, seen);
            }
        }
        return list.ToArray();
    }

    // Derive the runtime kit palette parallel to a gen palette by reading Kit
    // on each entry. Same length, same indices — TerrainId bytes index either
    // array. A null entry (gen-kit slot with no Kit authored) lands as null in
    // the runtime palette; consumers fall back to the no-kit defaults.
    public static TerrainData[] ExtractTerrainPalette(TerrainKitData[] gen)
    {
        if (gen == null) { return System.Array.Empty<TerrainData>(); }
        var arr = new TerrainData[gen.Length];
        for (int i = 0; i < gen.Length; i++)
        {
            arr[i] = gen[i]?.Terrain;
        }
        return arr;
    }

    // Walks a gen kit palette and returns a deduplicated detail-group palette
    // built from each gen kit's DefaultDetail. Same dedup rule as
    // BuildKitPalette. The returned array is uploaded via
    // ChunkMesh.SetDetailGroups; per-voxel DetailGroup bytes are 1-based
    // indices into this array.
    public static DetailGroupData[] BuildDetailPalette(TerrainKitData[] kits)
    {
        var list = new System.Collections.Generic.List<DetailGroupData>();
        var seen = new System.Collections.Generic.HashSet<DetailGroupData>();
        if (kits != null)
        {
            foreach (TerrainKitData k in kits)
            {
                if (k == null) { continue; }
                if (k.DefaultDetail != null && seen.Add(k.DefaultDetail))
                {
                    list.Add(k.DefaultDetail);
                }
            }
        }
        return list.ToArray();
    }

    private static void AddIfNew(TerrainKitData k, System.Collections.Generic.List<TerrainKitData> list, System.Collections.Generic.HashSet<TerrainKitData> seen)
    {
        if (k == null) { return; }
        if (seen.Add(k)) { list.Add(k); }
    }

    // Stamp the purpose for a kit IF it's still unclassified. First-zone-wins
    // semantics: a kit referenced as SurfaceKit in zone 0 stays Surface even
    // if zone 1 lists the same .tres as its CaveKit. Skips nulls and kits
    // missing from the active palette (defensive — only kits added by
    // BuildKitPalette get a slot, and we only call this with refs taken from
    // the same zone array).
    private static void ClassifyKit(TerrainKitData kit, EKitPurpose purpose)
    {
        if (kit == null || _kitIndex == null) { return; }
        if (!_kitIndex.TryGetValue(kit, out byte idx)) { return; }
        if (_kitPurposes[idx] == KIT_PURPOSE_NONE)
        {
            _kitPurposes[idx] = (byte)purpose;
        }
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
    public const int FOG_MAX_DENSITY = 255;   // storage cap (byte max), not a tunable
    // Fog bucket-fill tuning (FogVolumePerHumidity / FogDensityPerVoxel) and
    // the ramp-skirt slope (RampSlope) are authored on WorldGenData.

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
    // Reserved (the procedural road walker was removed) — kept in the registry
    // so the salt value isn't reused by a future channel.
    public const int SEED_SALT_ROAD     = 0x09;
    private const int SEED_SALT_ELEVATION = 0x0A;
    private const int SEED_SALT_PROPS   = 0x0B;
    private const int SEED_SALT_SIGNPOST = 0x0C;
    private const int SEED_SALT_FIXTURE = 0x0D;

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

    // Resolve the kit / detail palettes for `genData` and store them on the
    // WorldGen statics so per-voxel kit and detail stamps can do a dictionary
    // lookup instead of slot math. The gen palette is the dedup'd source of
    // truth; the runtime palette is derived parallel to it (same indices) for
    // ChunkMesh upload. Generate() calls this; Main.StartGame also calls it
    // before LoadWorldFromFile so authoring tools (WorldEditor) see the gen
    // palette regardless of whether the world came from disk or a fresh
    // Generate() run.
    public static void BindActivePalettes(WorldGenData genData)
    {
        _activeKitPalette = BuildKitPalette(genData?.Zones);
        _activeTerrainPalette = ExtractTerrainPalette(_activeKitPalette);
        _kitIndex = new System.Collections.Generic.Dictionary<TerrainKitData, byte>();
        for (int i = 0; i < _activeKitPalette.Length; i++)
        {
            TerrainKitData k = _activeKitPalette[i];
            if (k != null) { _kitIndex[k] = (byte)i; }
        }
        _activeDetailPalette = BuildDetailPalette(_activeKitPalette);
        _detailIndex = new System.Collections.Generic.Dictionary<DetailGroupData, byte>();
        for (int i = 0; i < _activeDetailPalette.Length; i++)
        {
            DetailGroupData g = _activeDetailPalette[i];
            if (g != null) { _detailIndex[g] = (byte)i; }
        }

        // Derive purpose-by-paletteIndex from zone refs. The first zone that
        // claims a kit sets its classification; later zones referencing the
        // same kit are ignored (current data has no cross-slot overlap, but
        // the rule is deterministic in case it ever does).
        _kitPurposes = new byte[_activeKitPalette.Length];
        for (int i = 0; i < _kitPurposes.Length; i++) { _kitPurposes[i] = KIT_PURPOSE_NONE; }
        if (genData?.Zones != null)
        {
            foreach (ZoneGenData z in genData.Zones)
            {
                if (z == null) { continue; }
                ClassifyKit(z.SurfaceKit, EKitPurpose.Surface);
                ClassifyKit(z.CaveKit, EKitPurpose.Cave);
                ClassifyKit(z.SubmergedKit, EKitPurpose.Submerged);
                ClassifyKit(z.ShoreKit, EKitPurpose.Shore);
            }
        }
    }

    public static WorldState Generate(WorldGenData genData, int worldSeed, Vector3I worldSize)
    {
        BindActivePalettes(genData);
        _activeGenData = genData;

        var min = new Vector3I(-worldSize.X / 2, -1, -worldSize.Z / 2);
        var max = new Vector3I(min.X + worldSize.X - 1, min.Y + worldSize.Y - 1, min.Z + worldSize.Z - 1);
        var ws = new WorldState(min, max, genData.SimData);

        BuildZoneStates(ws, genData, worldSeed);
        BuildRegionStates(ws, genData);

        WorldNoise noise = BuildWorldNoise(genData, worldSeed);

        // Build the integer height field once up front. Chunk and prop
        // generation read from this map instead of re-evaluating noise per
        // voxel — the shape is authored here (plateau / ramp / river) so
        // geometry is noise-free by construction.
        var heightMap = BuildHeightMap(ws, genData, noise.Terrain, noise.RampGate, noise.Elevation);

        GenerateChunks(ws, genData, noise.Tunnel, heightMap);

        // Tag the submerged shell as KIT_UNDERWATER. Runs after every chunk
        // (and its water voxels) exist so we can check actual water adjacency
        // instead of "wy <= WATER_LEVEL" — a y-only rule paints buried rock
        // under above-water cliffs as underwater, and the mesher's 27-voxel
        // kit vote then bleeds sand onto cliff faces nowhere near water.
        TagSubmergedKits(ws, genData);

        // Carve swiss-cheese caves through terrain. Runs after terrain+tunnel
        // generation so cave carving sees the full solid column and can
        // connect tunnels vertically where they overlap.
        GenerateCaves(ws, genData, noise.Cave);

        // One-off test stamp for underground-water visuals. Carves a wide
        // shallow cavern inland in the mountain zone (toward the desert
        // border) with the ceiling capped at the first plateau above water.
        // Will not survive pure worldgen — remove or gate this call once
        // the underwater shader work lands.
        //GenerateTestUnderwaterLake(ws, genData);

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
        // Currently disabled — the ±EdgeScanWindow / diff-threshold heuristic
        // doesn't map cleanly to the terrain shapes we actually generate, so
        // overlays end up in the wrong places. Revisit once we have a clearer
        // read on which features need the dirt treatment (probably driven by
        // authored tags from the editor rather than derived from geometry).
        // StampEdgeOverlays(ws);

        // Scatter procedural overlays (dirt patches, field/clover patches) on
        // Surface-kit voxels. Noise-driven placement is a rough
        // starting point so the authored overlay art shows up in generated
        // worlds; replace with authored tags once the custom editor lands.
        StampProceduralOverlays(ws, genData);

        // Detail-sprite scatter and prop / mob / loot spawning are gated by
        // the worldgen_skip CVar (bitmask — see SKIP_* flags). Each category is
        // checked independently so e.g. setting just "details" strips grass
        // blades without affecting trees or mobs.
        int skipFlags = CVars.worldgenSkip.Value;
        if ((skipFlags & SKIP_DETAILS) == 0)
        {
            StampDetailScatter(ws, genData);
        }
        GenerateAllProps(ws, genData, noise.Grass, noise.Forest, heightMap, skipFlags, worldSeed);

        // One-off near-spawn test fixtures (villager, knowledge stones, stash,
        // boat). Temporary scaffolding — replaced by the editor's placement pass.
        PlaceNearSpawnFixtures(ws, genData, heightMap, worldSeed);

        if ((skipFlags & SKIP_INTERACTIVES) == 0)
        {
            GenerateSignposts(ws, genData, heightMap, worldSeed);
        }

        // Stamp authored subscenes (voxels + entities). Loads each
        // `.hikescene` referenced from genData.Subscenes, computes a
        // surface-following Y anchor over its footprint, and writes
        // voxels into the world. Must run BEFORE ComputeSunlight so the
        // bake sees the final geometry. Env overrides land in a second
        // pass after the wind/envtag default bake (below).
        var stampedSubscenes = StampAuthoredSubscenes(ws, genData);

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

        // Stamp a procedural test pattern into every chunk's water-current
        // subgrid so the water shader has something to advect. Real worlds
        // will author this in the editor; this stays out of the way for
        // disk-loaded chunks (they use serialized bytes) and produces a
        // visibly varying flow field where the test world has water.
        GenerateTestWaterCurrents(ws);

        // Test override demonstrating the wind-velocity authoring path.
        // Amplifies a small region around the origin to ~3× the default
        // ambient speed so future consumers (particles, visual debug,
        // audio) can verify that authored gust regions read correctly
        // without needing the editor.
        GenerateTestStrongWind(ws);

        // Apply subscene env overrides AFTER the default bake so authored
        // dungeon/castle ambience wins over the inferred Outdoor/Cave tag.
        ApplySubsceneEnvOverrides(ws, stampedSubscenes);

        _lastHeightMap = heightMap;
        _lastPlateauStep = (int)Math.Max(1, Math.Round(genData.PlateauStep));
        return ws;
    }

    // Per-run noise channels, built once at the top of Generate from the
    // authored frequencies / octaves on WorldGenData and the per-channel seed
    // salts. Bundled into one value so Generate can hand them to each pass
    // without juggling seven locals.
    private readonly struct WorldNoise
    {
        public readonly FastNoiseLite Terrain;
        public readonly FastNoiseLite Tunnel;
        public readonly FastNoiseLite Cave;
        public readonly FastNoiseLite Grass;
        public readonly FastNoiseLite RampGate;
        public readonly FastNoiseLite Forest;
        public readonly FastNoiseLite Elevation;

        public WorldNoise(FastNoiseLite terrain, FastNoiseLite tunnel, FastNoiseLite cave,
            FastNoiseLite grass, FastNoiseLite rampGate, FastNoiseLite forest, FastNoiseLite elevation)
        {
            Terrain = terrain;
            Tunnel = tunnel;
            Cave = cave;
            Grass = grass;
            RampGate = rampGate;
            Forest = forest;
            Elevation = elevation;
        }
    }

    private static FastNoiseLite MakePerlin(int seed, float frequency, int octaves)
    {
        var noise = new FastNoiseLite();
        noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        noise.Seed = seed;
        noise.Frequency = frequency;
        noise.FractalOctaves = octaves;
        return noise;
    }

    private static WorldNoise BuildWorldNoise(WorldGenData genData, int worldSeed)
    {
        // Cave noise is a single shared field for the whole world — its
        // frequency comes from the first zone (CaveThreshold is still blended
        // per-column, so zones vary in density even though the pattern is
        // shared). The ramp gate is seeded off the path channel so its pattern
        // stays stable with the rest of the output. Forest noise keeps base
        // frequency 1; per-kit frequency is applied at sample time by scaling
        // input coords, so two kits in a zone can read different patterns.
        return new WorldNoise(
            terrain: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_TERRAIN), genData.TerrainNoiseFrequency, genData.TerrainNoiseOctaves),
            tunnel: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_TUNNEL), genData.TunnelNoiseFrequency, genData.TunnelNoiseOctaves),
            cave: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_CAVE), FirstZoneGen(genData)?.CaveNoiseFrequency ?? 0.04f, genData.CaveNoiseOctaves),
            grass: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_GRASS), genData.GrassNoiseFrequency, genData.GrassNoiseOctaves),
            rampGate: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_PATH), genData.RampGateNoiseFrequency, genData.RampGateNoiseOctaves),
            forest: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_FOREST), 1f, genData.ForestNoiseOctaves),
            elevation: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_ELEVATION), genData.ElevationNoiseFrequency, genData.ElevationNoiseOctaves));
    }

    // Build the per-world ZoneState array from the authored zone templates. The
    // ZoneData embedded in each ZoneGenData is what ZoneState carries forward —
    // the per-zone worldgen scalars on ZoneGenData stay in `genData` and blend
    // per-position during the passes. WindDirection is randomized in the XZ
    // plane so each world has its own prevailing wind per zone; Elevation
    // defaults to 0 until the editor authors it per-zone per-world.
    private static void BuildZoneStates(WorldState ws, WorldGenData genData, int worldSeed)
    {
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
    }

    // Build the per-world RegionState array from the authored region palette.
    // Independent from Zones — a region is a top-level named place identifier,
    // not a biome theme. PickRegionIndex (currently quadrant-based, mirroring
    // PickZoneIndex) decides which entry each chunk belongs to.
    private static void BuildRegionStates(WorldState ws, WorldGenData genData)
    {
        RegionData[] regionPalette = genData.Regions ?? [];
        ws.Regions = new RegionState[regionPalette.Length];
        for (int i = 0; i < regionPalette.Length; i++)
        {
            ws.Regions[i] = new RegionState { Data = regionPalette[i] };
        }
    }

    // Generate every chunk's voxels: assign zone / region index, then run the
    // per-chunk terrain + tunnel pass against the prebuilt height field.
    private static void GenerateChunks(WorldState ws, WorldGenData genData, FastNoiseLite tunnelNoise, HeightMap heightMap)
    {
        for (int x = ws.Min.X; x <= ws.Max.X; x++)
        {
            for (int y = ws.Min.Y; y <= ws.Max.Y; y++)
            {
                for (int z = ws.Min.Z; z <= ws.Max.Z; z++)
                {
                    var coord = new Vector3I(x, y, z);
                    var chunk = new ChunkState(coord);
                    chunk.ZoneIndex = PickZoneIndex(coord, ws.Zones.Length);
                    chunk.RegionIndex = PickRegionIndex(coord, ws.Regions.Length);
                    GenerateChunk(chunk, genData, tunnelNoise, heightMap);
                    ws._chunks[coord] = chunk;
                }
            }
        }
    }

    // Prop / mob / loot spawn pass (per surface column). Runs whenever *any* of
    // its skip categories is still active — internal gates pick which
    // subsections actually spawn. Block-light sources aren't pre-propagated
    // here; torch entities register themselves with WorldState.LightSources
    // when they spawn, which runs the BFS footprint at that point.
    private static void GenerateAllProps(WorldState ws, WorldGenData genData, FastNoiseLite grassNoise,
        FastNoiseLite forestNoise, HeightMap heightMap, int skipFlags, int worldSeed)
    {
        if ((skipFlags & (SKIP_PROPS | SKIP_MOBS | SKIP_INTERACTIVES)) == (SKIP_PROPS | SKIP_MOBS | SKIP_INTERACTIVES))
        {
            return;
        }
        for (int x = ws.Min.X; x <= ws.Max.X; x++)
        {
            for (int z = ws.Min.Z; z <= ws.Max.Z; z++)
            {
                var coord = new Vector3I(x, 0, z);
                GenerateProps(ws, coord, genData, grassNoise, forestNoise, heightMap, skipFlags, worldSeed);
            }
        }
    }

    // One-off near-spawn test fixtures: a friendly villager, KnowledgeStones,
    // a stash chest, and a rideable boat. All temporary scaffolding for systems
    // that lack an authored placement pass — fold each into a real population /
    // editor placement once those exist. Placement coordinates are authored on
    // WorldGenData (Placement Tuning group).
    private static void PlaceNearSpawnFixtures(WorldState ws, WorldGenData genData, HeightMap heightMap, int worldSeed)
    {
        // Voxel-space extent of the world. ws.Min/Max are in *chunks*; the
        // fixture coordinates below are voxels, so compare against the chunk
        // extent expressed in voxels to avoid the unit mismatch (a voxel X of
        // 32 is well outside the raw chunk range of -4..4).
        int stoneWorldMinX = ws.Min.X * ChunkState.SIZE;
        int stoneWorldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int stoneWorldMinZ = ws.Min.Z * ChunkState.SIZE;
        int stoneWorldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        // Pick a flat, dry land column in the same quadrant as (cx, cz) so a
        // one-off fixture doesn't land on a ramp face or in water. Rolls random
        // columns in the quadrant until one is flat-and-dry, capped at
        // FixturePlacementMaxTries, then falls back to the target. The quadrant
        // bounds (split at X=0 / Z=0, matching PickZoneIndex) keep the fixture
        // in its zone.
        var fixtureRng = new Random(DeriveSeed(worldSeed, SEED_SALT_FIXTURE));
        Vector3I FindFlatDryInZone(int cx, int cz)
        {
            int xLo = cx >= 0 ? 0 : stoneWorldMinX;
            int xHi = cx >= 0 ? stoneWorldMaxX : -1;
            int zLo = cz >= 0 ? 0 : stoneWorldMinZ;
            int zHi = cz >= 0 ? stoneWorldMaxZ : -1;
            int maxTries = genData.FixturePlacementMaxTries;
            for (int i = 0; i < maxTries; i++)
            {
                int x = fixtureRng.Next(xLo, xHi + 1);
                int z = fixtureRng.Next(zLo, zHi + 1);
                if (IsFlatTerrainAt(x, z, heightMap))
                {
                    return new Vector3I(x, 0, z);
                }
            }
            return new Vector3I(cx, 0, cz);
        }

        // One-off friendly villager placed in the mountain zone (the NE
        // quadrant — chunk X >= 0, Z >= 0; see PickZoneIndex) so the new
        // IInteractive/Talk plumbing has a concrete target without requiring an
        // editor placement. The spawn XZ only selects the quadrant —
        // FindFlatDryInZone then rolls a flat, dry column within it. Temporary
        // test fixture — fold into a proper NPC population pass once authored
        // villager spawn rules exist.
        int villagerSpawnX = genData.NearSpawnVillagerSpawn.X;
        int villagerSpawnZ = genData.NearSpawnVillagerSpawn.Y;
        if (genData.NearSpawnVillagerData != null
            && genData.NearSpawnVillagerData.MobScene != null
            && villagerSpawnX >= stoneWorldMinX && villagerSpawnX <= stoneWorldMaxX
            && villagerSpawnZ >= stoneWorldMinZ && villagerSpawnZ <= stoneWorldMaxZ)
        {
            MobData villagerData = genData.NearSpawnVillagerData;
            Vector3I spot = FindFlatDryInZone(villagerSpawnX, villagerSpawnZ);
            int sy = heightMap.GetHeight(spot.X, spot.Z);
            var pos = new Vector3(spot.X + 0.5f, sy + 1.5f, spot.Z + 0.5f);
            var villagerSim = new MobSimState(pos, 0f, villagerData.MobScene, villagerData);
            // The villager test fixture speaks the same language the
            // KnowledgeStone fixtures teach, so reading the stones
            // progressively un-scrambles the dialogue too. Set per-
            // instance via SimState rather than on MobData so the shared
            // friendly_villager.tres stays language-agnostic.
            villagerSim.Language = genData.KnowledgeStoneLanguage;
            // Branching conversation attached per-instance so the shared
            // friendly_villager.tres stays conversation-agnostic — a
            // future quest-specific villager can pin its own conversation
            // without forking the MobData.
            villagerSim.Conversation = genData.NearSpawnVillagerConversation;
            // Loyalty rewards and merchant inventory live on the placement
            // entry, not the species template — see WorldGenData for the
            // rationale.
            if (genData.NearSpawnVillagerLoyaltyGifts != null)
            {
                foreach (LoyaltyGift gift in genData.NearSpawnVillagerLoyaltyGifts)
                {
                    if (gift != null) { villagerSim.LoyaltyGifts.Add(gift); }
                }
            }
            if (genData.NearSpawnVillagerInventory != null)
            {
                foreach (MobInventoryData entry in genData.NearSpawnVillagerInventory)
                {
                    if (entry == null || entry.item == null) { continue; }
                    ItemState state = entry.item.CreateState();
                    state.stackCount = Mathf.Max(1, entry.count);
                    villagerSim.Inventory.Add(new MobInventoryItem
                    {
                        item = state,
                        loyaltyCost = entry.loyaltyCost,
                        secret = entry.secret,
                    });
                }
            }
            ws.AddEntity(villagerSim);
        }

        // KnowledgeStone test fixtures, one per language component, scattered
        // across three zones so exercising the partial-learning flow means
        // travelling between biomes rather than walking down a row at spawn:
        // swamp (SE), desert (NW) and forest (SW). See PickZoneIndex for the
        // quadrant -> zone mapping. The (position, component) layout is a fixed
        // test-fixture table (like StaircasePattern), not an authoring knob.
        // Skipped if the worldgen data doesn't carry a stone scene/language.
        var stoneComponents = new (int x, int z, ELanguageComponents component)[]
        {
            (32, -32, ELanguageComponents.Vocabulary1),   // swamp  (SE quadrant)
            (-32, 32, ELanguageComponents.Vocabulary2),   // desert (NW quadrant)
            (-32, -32, ELanguageComponents.Vocabulary3),  // forest (SW quadrant)
        };
        if (genData.KnowledgeStoneScene != null && genData.KnowledgeStoneLanguage != null)
        {
            foreach (var (sx, sz, component) in stoneComponents)
            {
                if (sx < stoneWorldMinX || sx > stoneWorldMaxX
                    || sz < stoneWorldMinZ || sz > stoneWorldMaxZ) { continue; }
                Vector3I spot = FindFlatDryInZone(sx, sz);
                int sy = heightMap.GetHeight(spot.X, spot.Z);
                var pos = new Vector3(spot.X + 0.5f, sy + 1f, spot.Z + 0.5f);
                // Wrap the per-fixture (language, component) pair in a
                // LanguageTeachable so the stone runs through the unified
                // TeachableConcept path. Resource is constructed transient
                // — never gets serialized as its own .tres; saves/loads
                // round-trip through EntitySerializer's Tag.KnowledgeStone
                // wire format which re-synthesizes a LanguageTeachable on
                // read.
                var concepts = new Godot.Collections.Array<TeachableConcept>
                {
                    new LanguageTeachable { language = genData.KnowledgeStoneLanguage, components = component },
                };
                ws.AddEntity(new KnowledgeStoneSimState(pos, genData.KnowledgeStoneScene, genData.KnowledgeStoneText, genData.KnowledgeStoneLanguage, concepts));
            }
        }

        // Near-spawn test stash. The chest_stash.tscn flips Chest._isStash so
        // interaction opens the StashScreen; the authored item list is
        // materialized into ItemStates and seeded into Contents (not
        // LootItems) so the player finds the stash pre-loaded with starter
        // items rather than ejecting them on first open.
        int stashX = genData.NearSpawnStashSpawn.X;
        int stashZ = genData.NearSpawnStashSpawn.Y;
        if (genData.NearSpawnStashScene != null
            && stashX >= stoneWorldMinX && stashX <= stoneWorldMaxX
            && stashZ >= stoneWorldMinZ && stashZ <= stoneWorldMaxZ)
        {
            int sy = heightMap.GetHeight(stashX, stashZ);
            var pos = new Vector3(stashX + 0.5f, sy + 1f, stashZ + 0.5f);
            var stashSim = new ChestSimState(pos, genData.NearSpawnStashScene);
            if (genData.NearSpawnStashItems != null)
            {
                for (int i = 0; i < genData.NearSpawnStashItems.Length; i++)
                {
                    ItemCount entry = genData.NearSpawnStashItems[i];
                    if (entry == null || entry.item == null || entry.count <= 0) { continue; }
                    ItemState state = entry.item.CreateState();
                    state.stackCount = entry.count;
                    stashSim.Contents.Add(state);
                }
            }
            ws.AddEntity(stashSim);
        }

        // Near-spawn test boat. Unlike the land fixtures above it must sit on
        // water, and procedural terrain doesn't guarantee a pond at a fixed
        // offset — so ring-scan outward from spawn for the nearest water-topped
        // column and float the boat there (origin riding the water surface,
        // WATER_LEVEL + 1). Skipped if no water is found within range.
        Vector3? FindNearestWaterSurface()
        {
            int searchRadius = genData.NearSpawnBoatSearchRadius;
            for (int r = 1; r <= searchRadius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dz = -r; dz <= r; dz++)
                    {
                        // Boundary of the ring only — inner cells were covered
                        // by a smaller radius.
                        if (Mathf.Abs(dx) != r && Mathf.Abs(dz) != r) { continue; }
                        int bx = dx;
                        int bz = dz;
                        if (bx < stoneWorldMinX || bx > stoneWorldMaxX
                            || bz < stoneWorldMinZ || bz > stoneWorldMaxZ) { continue; }
                        // Water-surfaced column: water at sea level, air above.
                        if (ws.GetVoxelWorld(bx, WATER_LEVEL, bz) != VoxelType.Water
                            || ws.GetVoxelWorld(bx, WATER_LEVEL + 1, bz) != VoxelType.Air) { continue; }
                        return new Vector3(bx + 0.5f, WATER_LEVEL + 1f, bz + 0.5f);
                    }
                }
            }
            return null;
        }

        if (genData.NearSpawnBoatScene != null)
        {
            Vector3? boatPos = FindNearestWaterSurface();
            if (boatPos.HasValue)
            {
                ws.AddEntity(new BoatSimState(boatPos.Value, 0f, genData.NearSpawnBoatScene));
            }
        }
    }

    // Authored "strong gust" region — multiplies the WindGen-baked
    // velocity by GustMultiplier inside a horizontal box around the
    // world origin. Stays bounded by the storage scale (clipping happens
    // inside SetWindVelocity), so over-amplification just clamps. Real
    // worlds will get this from the editor; this is the test seed so we
    // can prove out per-cell authoring without one.
    private static void GenerateTestStrongWind(WorldState ws)
    {
        const float GustMultiplier = 3f;
        const int RadiusXZ = 32;
        const int VoxelsPerCell = ChunkState.ENV_VOXELS_PER_CELL;
        for (int cz = ws.Min.Z; cz <= ws.Max.Z; cz++)
        {
            for (int cy = ws.Min.Y; cy <= ws.Max.Y; cy++)
            {
                for (int cx = ws.Min.X; cx <= ws.Max.X; cx++)
                {
                    ChunkState chunk = ws.GetChunk(new Vector3I(cx, cy, cz));
                    if (chunk == null) { continue; }
                    for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
                    {
                        int wx = cx * ChunkState.SIZE + sx * VoxelsPerCell + VoxelsPerCell / 2;
                        if (wx < -RadiusXZ || wx > RadiusXZ) { continue; }
                        for (int sy = 0; sy < ChunkState.ENV_SUBGRID_SIZE; sy++)
                        {
                            for (int sz = 0; sz < ChunkState.ENV_SUBGRID_SIZE; sz++)
                            {
                                int wz = cz * ChunkState.SIZE + sz * VoxelsPerCell + VoxelsPerCell / 2;
                                if (wz < -RadiusXZ || wz > RadiusXZ) { continue; }
                                Vector3 v = chunk.GetWindVelocity(sx, sy, sz) * GustMultiplier;
                                chunk.SetWindVelocity(sx, sy, sz, v.X, v.Y, v.Z);
                            }
                        }
                    }
                }
            }
        }
    }

    // Seed per-cell water currents perpendicular to the chunk's zone wind
    // direction in XZ — a 90° CCW rotation, so wind (wx, wz) maps to
    // current (-wz, wx). Matches WindGen's per-zone seeding shape: one
    // direction per chunk, stamped uniformly into every cell. Run after
    // voxel carving; cells whose voxels contain no water still get stamped,
    // but the water shader only samples on water surface fragments so
    // unused stamps cost nothing at render time.
    private static void GenerateTestWaterCurrents(WorldState ws)
    {
        const float Magnitude = 0.7f;
        for (int cz = ws.Min.Z; cz <= ws.Max.Z; cz++)
        {
            for (int cy = ws.Min.Y; cy <= ws.Max.Y; cy++)
            {
                for (int cx = ws.Min.X; cx <= ws.Max.X; cx++)
                {
                    ChunkState chunk = ws.GetChunk(new Vector3I(cx, cy, cz));
                    if (chunk == null) { continue; }

                    Vector3 zoneDir = Vector3.Zero;
                    if (ws.Zones != null && chunk.ZoneIndex < ws.Zones.Length)
                    {
                        zoneDir = ws.Zones[chunk.ZoneIndex].WindDirection;
                    }
                    float dx = zoneDir.X;
                    float dz = zoneDir.Z;
                    float len = Mathf.Sqrt(dx * dx + dz * dz);
                    if (len < 1e-6f) { continue; }
                    float invLen = 1f / len;
                    float fx = -dz * invLen * Magnitude;
                    float fz = dx * invLen * Magnitude;

                    for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
                    {
                        for (int sy = 0; sy < ChunkState.ENV_SUBGRID_SIZE; sy++)
                        {
                            for (int sz = 0; sz < ChunkState.ENV_SUBGRID_SIZE; sz++)
                            {
                                chunk.SetCurrent(sx, sy, sz, fx, fz);
                            }
                        }
                    }
                }
            }
        }
    }

    private static List<(SubsceneState sub, Vector3 anchor)> StampAuthoredSubscenes(WorldState ws, WorldGenData genData)
    {
        var stamped = new List<(SubsceneState, Vector3)>();
        if (genData.Subscenes == null || genData.Subscenes.Length == 0)
        {
            return stamped;
        }
        foreach (SubscenePlacement placement in genData.Subscenes)
        {
            if (placement == null || string.IsNullOrEmpty(placement.Path))
            {
                continue;
            }
            SubsceneState sub;
            try
            {
                sub = SubsceneFile.Read(placement.Path);
            }
            catch (Exception e)
            {
                GD.PrintErr($"WorldGen: subscene '{placement.Path}' failed to load: {e.Message}");
                continue;
            }
            Vector3 anchor = SubsceneStamper.ComputeSurfaceAnchor(ws, sub, placement.AnchorXZ);
            SubsceneStamper.StampVoxels(ws, sub, anchor);
            stamped.Add((sub, anchor));
        }
        return stamped;
    }

    private static void ApplySubsceneEnvOverrides(WorldState ws, List<(SubsceneState sub, Vector3 anchor)> stamped)
    {
        foreach ((SubsceneState sub, Vector3 anchor) in stamped)
        {
            SubsceneStamper.StampEnvOverrides(ws, sub, anchor);
        }
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

    // Stamp a chunk into one of the world's named regions. Same legacy
    // 4-quadrant split as PickZoneIndex (so default_world_gen.tres can
    // line its Regions[] up with its Zones[] in the same order until the
    // editor produces arbitrary region polygons). Independent function so
    // future region-shape changes don't drag zones along — the two are
    // orthogonal subdivisions, not parallel.
    private static byte PickRegionIndex(Vector3I chunkCoord, int regionCount)
    {
        if (regionCount <= 0) { return 0; }
        int quadrant;
        if (chunkCoord.X >= 0 && chunkCoord.Z >= 0) { quadrant = 0; }       // NE
        else if (chunkCoord.X < 0 && chunkCoord.Z >= 0) { quadrant = 1; }   // NW
        else if (chunkCoord.X >= 0 && chunkCoord.Z < 0) { quadrant = 2; }   // SE
        else { quadrant = 3; }                                              // SW
        if (quadrant >= regionCount) { quadrant = regionCount - 1; }
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
    // Reaches WorldGenData.ZoneGenBlendRadius chunks out from (wx, wz) and
    // weights each chunk's zone by its smoothstep falloff. PickZoneIndex still
    // returns one zone per chunk for ChunkState.ZoneIndex (gameplay
    // needs a single value), but the worldgen scalars blend smoothly across
    // chunk borders so a desert→forest transition isn't a hard line.
    //
    // The blend radii (ZoneGenBlendRadius for soft scalar fades like elevation
    // and density; the tighter KitBlendRadius for kit-identity stamps) are
    // authored on WorldGenData — KitBlendRadius must stay >= 1.0 or corner
    // voxels get zero weight and PickKitZone falls back to a chunk-aligned hard
    // seam, the exact thing the kernel exists to avoid.
    //
    // `weights` Span must be sized to zoneCount. Output sums to 1 (or
    // all zeros if no neighbour has a valid zone — caller's choice what
    // to do about that).
    private static void GetZoneGenWeights(int wx, int wz, int zoneCount, Span<float> weights)
    {
        GetZoneGenWeights(wx, wz, zoneCount, weights, _activeGenData?.ZoneGenBlendRadius ?? 2.0f);
    }

    private static void GetZoneGenWeights(int wx, int wz, int zoneCount, Span<float> weights, float blendRadius)
    {
        for (int i = 0; i < zoneCount; i++) { weights[i] = 0f; }
        if (zoneCount <= 0) { return; }

        int chunkX = (int)Math.Floor((double)wx / ChunkState.SIZE);
        int chunkZ = (int)Math.Floor((double)wz / ChunkState.SIZE);
        int half = Mathf.CeilToInt(blendRadius);

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
                float w = Mathf.SmoothStep(blendRadius, 0f, distChunks);
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
    // `blendRadius` overrides WorldGenData.ZoneGenBlendRadius for the weight
    // kernel — kit stamps use the tighter KitBlendRadius so out-of-biome kit
    // bleed stays localized.
    private static int PickWeightedZoneFromHash(int wx, int wz, ZoneGenData[] zones, float r01, float blendRadius)
    {
        int n = zones != null ? zones.Length : 0;
        if (n == 0) { return -1; }

        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        GetZoneGenWeights(wx, wz, n, weights, blendRadius);

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

    // Per-column salts for shore-band thickness hashes. Two distinct salts so
    // the above-water and below-water shore-band heights vary independently
    // per column (a column can be picked tall above water but shallow below,
    // and vice versa).
    private const int SHORE_UPPER_HASH_SALT = 0x53484F55; // "SHOU"
    private const int SHORE_LOWER_HASH_SALT = 0x53484F4C; // "SHOL"

    // Pick a zone for a kit stamp at (wx, wz). Falls back to the chunk's
    // ZoneIndex when the kernel produces no positive weight (off-world,
    // edge cases) so we always end up with a stamped kit. Uses the tighter
    // KitBlendRadius so out-of-biome kit stamps stay near the seam
    // instead of penetrating ~2 chunks into adjacent biomes.
    private static int PickKitZone(int wx, int wz, ZoneGenData[] zones, int fallbackZoneIndex)
    {
        int idx = PickWeightedZoneFromHash(wx, wz, zones, HashFloat01(wx, wz, KIT_HASH_SALT), _activeGenData?.KitBlendRadius ?? 2.0f);
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
            int rampSlope = _activeGenData?.RampSlope ?? 1;
            sw.WriteLine($"RampSlope={rampSlope}, PlateauStep={_lastPlateauStep}, rampRadius={_lastPlateauStep * rampSlope}");
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
        // Ramp anchor band, macro-elevation amplitude, and shoreline falloff
        // are authored on WorldGenData (Terrain Shaping group). `|pathNoise|`
        // below RampAnchorBand marks the core of a ramp zone; the macro noise
        // adds ±MacroElevationRangePlateaus steps; the far east drops to ocean
        // over ShorelineChunks chunks down to OceanDepthPlateaus below zero.
        float rampAnchorBand = genData.RampAnchorBand;
        float macroElevationRangePlateaus = genData.MacroElevationRangePlateaus;
        float oceanDepthPlateaus = genData.OceanDepthPlateaus;
        float shorelineFalloffWidth = genData.ShorelineChunks * ChunkState.SIZE;

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
                               + macroN * macroElevationRangePlateaus;

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
                // (coastT → 0) → -oceanDepthPlateaus (deep ocean below
                // sea level).
                int distFromEastEdge = worldMaxX - wx;
                float coastT = Mathf.Clamp(distFromEastEdge / shorelineFalloffWidth, 0f, 1f);
                coastT = Mathf.SmoothStep(0f, 1f, coastT);
                int effectivePlateaus = (int)Mathf.Round(
                    Mathf.Lerp(-oceanDepthPlateaus, plateauSteps, coastT));

                // Step 5: convert plateau steps → world voxels with
                // Elevation = 0 anchored at sea level. Sea is at WATER_LEVEL
                // (= -1 plateau step in voxel units), so a plateau-step value
                // of 0 lands at WATER_LEVEL and each unit of Elevation /
                // ElevationRange shifts the surface by exactly one plateau
                // step (4 voxels) above or below the water plane.
                plateau[lx, lz] = WATER_LEVEL + effectivePlateaus * step;
                rampAnchor[lx, lz] = Math.Abs(rampGateNoise.GetNoise2D(wx, wz)) < rampAnchorBand;
            }
        }

        // Dilate anchor mask by `rampRadius` cells — one full scan-radius's
        // worth on each side of the raw anchor line, so the lift scan has a
        // fully eligible neighbourhood and always produces a complete skirt.
        bool[,] rampEligible = new bool[sizeX, sizeZ];
        int rampRadiusConst = step * genData.RampSlope;
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
        // One step of rise takes `step * RampSlope` horizontal cells; that's
        // also the scan radius since anything farther would only contribute
        // a zero (or clamped-away) lift.
        int rampSlope = genData.RampSlope;
        int rampRadius = step * rampSlope;
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
                            int verticalDrop = (dist + rampSlope - 1) / rampSlope;
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
        // Scatter samplers (SpawnGroupData.ScatterRadius via SpawnContext.TryPickInRadius)
        // can probe positions outside the heightmap range when the anchor is
        // near a world edge — treat those as not-grass rather than indexing
        // the Height/Plateau arrays out of bounds.
        if (wx < heightMap.WorldMinX || wx > heightMap.WorldMaxX
            || wz < heightMap.WorldMinZ || wz > heightMap.WorldMaxZ)
        {
            return false;
        }
        int h = heightMap.GetHeight(wx, wz);
        // h is the topmost solid voxel; the walkable surface sits at h+1, so
        // "above water" is h+1 > WATER_LEVEL, i.e. h >= WATER_LEVEL. Strict
        // greater-than was wrong: it excluded shoreline plateaus (h=WATER_LEVEL)
        // whose top voxel sits exactly at the water plane but whose air-above
        // is still dry — exactly the band where forest's noise dips would
        // otherwise plant trees.
        return h == heightMap.GetPlateau(wx, wz) && h >= WATER_LEVEL;
    }

    // True iff (wx, wz) sits on an obvious flat patch — the column itself
    // is flat-dry-grass AND all 8 neighbors share the same Height. Rejects
    // both step-edge columns (neighbor lower = cliff drop) and ramp-adjacent
    // columns (neighbor higher = ramp climb). Used by spawn entries that
    // opt in via RequireFlatTerrain (mobs, campfires) where physics or
    // visuals can't tolerate a sloped step face under the spawn anchor.
    private static bool IsFlatTerrainAt(int wx, int wz, HeightMap heightMap)
    {
        if (!IsFlatDryGrassAt(wx, wz, heightMap))
        {
            return false;
        }
        int h = heightMap.GetHeight(wx, wz);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0)
                {
                    continue;
                }
                int nx = wx + dx;
                int nz = wz + dz;
                if (nx < heightMap.WorldMinX || nx > heightMap.WorldMaxX
                    || nz < heightMap.WorldMinZ || nz > heightMap.WorldMaxZ)
                {
                    return false;
                }
                if (heightMap.GetHeight(nx, nz) != h)
                {
                    return false;
                }
            }
        }
        return true;
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

                // Per-column kit pick + above-water shore band, hoisted out of
                // the y loop because both depend only on (wx, wz). The shore
                // upper bound is a per-column random value in
                // [ShoreElevationMin, ShoreElevationMax] meters above sea
                // level — keeps the shoreline jagged instead of a flat
                // isobar. Columns whose zone has no ShoreKit get an empty
                // band (shoreUpperY = WATER_LEVEL → no voxel falls in it).
                int kitZone = PickKitZone(wx, wz, genData.Zones, data.ZoneIndex);
                ZoneGenData kitZoneData = kitZone >= 0 ? genData.Zones[kitZone] : null;
                byte surfaceTerrainId = TerrainIdOf(kitZoneData?.SurfaceKit);
                byte shoreTerrainId = surfaceTerrainId;
                int shoreUpperY = WATER_LEVEL;
                if (kitZoneData != null && kitZoneData.ShoreKit != null)
                {
                    shoreTerrainId = TerrainIdOf(kitZoneData.ShoreKit);
                    float shoreUpperR = HashFloat01(wx, wz, SHORE_UPPER_HASH_SALT);
                    float shoreUpperMeters = Mathf.Lerp(
                        kitZoneData.ShoreElevationMin,
                        kitZoneData.ShoreElevationMax,
                        shoreUpperR);
                    shoreUpperY = WATER_LEVEL + (int)Math.Round(shoreUpperMeters);
                }

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
                    // the tile from the per-voxel TerrainId (see below) + surface
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

                    // Kit assignment: default every solid voxel to the zone's
                    // SurfaceKit (resolved per-column above). TagSubmergedKits
                    // runs after all chunks/water exist and re-stamps the
                    // submerged shell to the picked zone's SubmergedKit based
                    // on actual water adjacency, so buried rock under above-
                    // water cliffs stays SurfaceKit (no sand bleed on cliff
                    // faces). Cave interiors are later re-stamped to CaveKit
                    // by MarkCaveSurfaceShapes. Above-water voxels in the
                    // shore band (wy in (WATER_LEVEL, shoreUpperY]) take the
                    // zone's ShoreKit so the beach lip at water's edge reads
                    // as sand even on land.
                    data.TerrainId[x, y, z] = (wy > WATER_LEVEL && wy <= shoreUpperY)
                        ? shoreTerrainId
                        : surfaceTerrainId;
                }
            }
        }
    }

    // Place one signpost per quadrant (NE, NW, SE, SW) at a random grassy
    // column inside that quadrant. Each quadrant pulls its text from its
    // SignpostText* field on WorldGenData; empty strings, missing scene, or
    // empty quadrants are skipped. Per-quadrant rng is keyed off the world
    // seed + a stable salt + the quadrant index so placement is reproducible.
    private static void GenerateSignposts(WorldState ws, WorldGenData genData, HeightMap heightMap, int worldSeed)
    {
        if (genData.SignpostScene == null)
        {
            return;
        }

        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        // Quadrant order matches PickZoneIndex: 0=NE, 1=NW, 2=SE, 3=SW.
        var ranges = new (int xMin, int xMax, int zMin, int zMax)[]
        {
            (Math.Max(0, worldMinX), worldMaxX, Math.Max(0, worldMinZ), worldMaxZ),
            (worldMinX, Math.Min(-1, worldMaxX), Math.Max(0, worldMinZ), worldMaxZ),
            (Math.Max(0, worldMinX), worldMaxX, worldMinZ, Math.Min(-1, worldMaxZ)),
            (worldMinX, Math.Min(-1, worldMaxX), worldMinZ, Math.Min(-1, worldMaxZ)),
        };
        var texts = new string[]
        {
            genData.SignpostTextNE,
            genData.SignpostTextNW,
            genData.SignpostTextSE,
            genData.SignpostTextSW,
        };

        bool IsGrassyAt(int wx, int wz)
        {
            if (!IsFlatDryGrassAt(wx, wz, heightMap))
            {
                return false;
            }
            int sy = heightMap.GetHeight(wx, wz);
            VoxelType ground = ws.GetVoxelWorld(wx, sy, wz);
            if (ground == VoxelType.Air || ground == VoxelType.Water)
            {
                return false;
            }
            return ws.GetVoxelWorld(wx, sy + 1, wz) == VoxelType.Air;
        }

        int MAX_ATTEMPTS = genData.FixturePlacementMaxTries;
        for (int q = 0; q < 4; q++)
        {
            string text = texts[q];
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            var (xMin, xMax, zMin, zMax) = ranges[q];
            if (xMax < xMin || zMax < zMin)
            {
                continue;
            }
            var rng = new Random(StableMix(DeriveSeed(worldSeed, SEED_SALT_SIGNPOST), q, 0));
            for (int attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
            {
                int wx = rng.Next(xMin, xMax + 1);
                int wz = rng.Next(zMin, zMax + 1);
                if (!IsGrassyAt(wx, wz))
                {
                    continue;
                }
                int sy = heightMap.GetHeight(wx, wz);
                var pos = new Vector3(wx + 0.5f, sy + 1f, wz + 0.5f);
                ws.AddEntity(new SignpostSimState(pos, genData.SignpostScene, text, genData.SignpostLanguage));
                break;
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
            float desiredVolume = humidity * genData.FogVolumePerHumidity * floors.Count;
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
                    int density = (int)Mathf.Clamp(depth * genData.FogDensityPerVoxel, 0f, FOG_MAX_DENSITY);
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
                        // Stamp this chunk's zone CaveKit so the shader
                        // can paint it distinctly from the surface above.
                        // Overrides SubmergedKit for submerged caves — the
                        // cave palette wins there.
                        int zoneIdx = PickKitZone(wx, wz, genData.Zones, ZoneIndexAtWorld(ws, wx, wy, wz));
                        ws.SetTerrainIdWorld(wx, wy, wz, TerrainIdOf(genData.Zones[zoneIdx]?.CaveKit));
                    }
                }
            }
        }
    }

    // Re-tag solid voxels at or below WATER_LEVEL to KIT_UNDERWATER iff they
    // sit within WorldGenData.SubmergedKitRadius of a water voxel. Runs after every
    // chunk exists so the water pass has already filled every non-solid
    // wy<=WATER_LEVEL cell with VoxelType.Water. Semantic "near water" beats
    // the old "wy<=WATER_LEVEL" rule because the latter paints deeply buried
    // rock under cliffs as underwater — then the mesher's 27-voxel kit vote
    // for nearby DC cells drags that sand onto the visible cliff face.
    private static void TagSubmergedKits(WorldState ws, WorldGenData genData)
    {
        // Chebyshev radius for the water-adjacency search. Must be >= 2 (see
        // WorldGenData.SubmergedKitRadius for the mesher-vote rationale).
        int submergedRadius = genData.SubmergedKitRadius;
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                // Per-column kit pick + below-water shore band, hoisted out of
                // the y loop because both depend only on (wx, wz). The shore
                // lower bound is a per-column random value in
                // [ShoreSubmergedElevationMin, ShoreSubmergedElevationMax]
                // meters below sea level — keeps the underwater shoreline
                // jagged instead of a flat isobar. Falls back to ZoneIndex 0
                // here; the per-voxel ZoneIndexAtWorld fallback inside the y
                // loop only kicks in when the kernel produces no positive
                // weight, which is rare.
                int columnZone = PickKitZone(wx, wz, genData.Zones, 0);
                ZoneGenData columnZoneData = columnZone >= 0 ? genData.Zones[columnZone] : null;
                byte shoreTerrainId = 0;
                int shoreLowerY = WATER_LEVEL;
                bool hasShore = columnZoneData != null && columnZoneData.ShoreKit != null;
                if (hasShore)
                {
                    shoreTerrainId = TerrainIdOf(columnZoneData.ShoreKit);
                    float shoreLowerR = HashFloat01(wx, wz, SHORE_LOWER_HASH_SALT);
                    float shoreLowerMeters = Mathf.Lerp(
                        columnZoneData.ShoreSubmergedElevationMin,
                        columnZoneData.ShoreSubmergedElevationMax,
                        shoreLowerR);
                    shoreLowerY = WATER_LEVEL + (int)Math.Round(shoreLowerMeters);
                }

                for (int wy = worldMinY; wy <= WATER_LEVEL; wy++)
                {
                    var v = ws.GetVoxelWorld(wx, wy, wz);
                    if (!VoxelTypeInfo.IsSolid(v) || v == VoxelType.Barrier)
                    {
                        continue;
                    }

                    bool nearWater = false;
                    for (int dy = -submergedRadius; dy <= submergedRadius && !nearWater; dy++)
                    {
                        for (int dx = -submergedRadius; dx <= submergedRadius && !nearWater; dx++)
                        {
                            for (int dz = -submergedRadius; dz <= submergedRadius && !nearWater; dz++)
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
                        if (hasShore && wy >= shoreLowerY)
                        {
                            ws.SetTerrainIdWorld(wx, wy, wz, shoreTerrainId);
                        }
                        else
                        {
                            int zoneIdx = PickKitZone(wx, wz, genData.Zones, ZoneIndexAtWorld(ws, wx, wy, wz));
                            ws.SetTerrainIdWorld(wx, wy, wz, TerrainIdOf(genData.Zones[zoneIdx]?.SubmergedKit));
                        }
                    }
                }
            }
        }
    }

    // Overlay id values. 0 = no overlay. A non-zero OverlayId is a direct
    // tile_array base-layer index sampled by voxel_clip.gdshader with its
    // own alpha channel driving blend strength. Any block in the BlockCatalog
    // can be used as an overlay — add new OVERLAY_* fields by name rather
    // than reusing numbers so .hike files written with old values keep
    // mapping to the right block when new blocks are added ahead of them.
    private const byte OVERLAY_NONE = 0;
    private static readonly byte OVERLAY_DIRT = ResolveOverlayIndex("DirtOverlay");
    private static readonly byte OVERLAY_FIELD = ResolveOverlayIndex("FieldOverlay");

    private static byte ResolveOverlayIndex(StringName blockName)
    {
        BlockData block = BlockCatalog.Active.GetByName(blockName);
        if (block == null)
        {
            GD.PushError($"WorldGen: BlockCatalog has no block named '{blockName}'.");
            return 0;
        }
        return (byte)block.AtlasBaseIndex;
    }

    // Edge-overlay scan window / diff band (EdgeScanWindow, EdgeMinDiff,
    // EdgeMaxDiff) and the procedural overlay scatter frequencies / thresholds
    // are authored on WorldGenData. The scatter SEEDS stay fixed here — they're
    // stable RNG salts (like the SEED_SALT_* channels), not feel knobs.
    private const int OVERLAY_DIRT_SEED = 4242;
    private const int OVERLAY_FIELD_SEED = 7373;

    // Test placement for detail-sprite scatter. Each kit advertises its own
    // DefaultDetail group; this pass walks every surface voxel, reads the
    // voxel's kit, and stamps that kit's DefaultDetail (1-based palette
    // index) wherever detailNoise crosses the per-zone threshold. Replace
    // with authored brushes once the editor lands; the runtime is happy
    // with no DefaultDetail configured (the scatter pass short-circuits).
    private const int DETAIL_NOISE_SEED = 9191;
    // Independent seed for non-Surface kits (cave / submerged) so their
    // scatter pattern isn't visually correlated with the surface scatter
    // directly above. Selection is by kit.Purpose at sample time.
    private const int SUBSURFACE_NOISE_SEED = 9192;

    // Noise-scatter dirt and field overlays on Surface-kit voxels.
    // Only top-surface voxels (solid with air above) are candidates so buried
    // geometry and cliff faces stay untouched. Kit gate restricts placement
    // to Surface kits — sand (underwater/cave) and cave palette stay clean.
    private static void StampProceduralOverlays(WorldState ws, WorldGenData genData)
    {
        var dirtNoise = new FastNoiseLite();
        dirtNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        dirtNoise.Seed = OVERLAY_DIRT_SEED;
        dirtNoise.Frequency = genData.OverlayDirtFrequency;
        dirtNoise.FractalOctaves = 2;

        var fieldNoise = new FastNoiseLite();
        fieldNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        fieldNoise.Seed = OVERLAY_FIELD_SEED;
        fieldNoise.Frequency = genData.OverlayFieldFrequency;
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
                for (int wy = worldMinY; wy < worldMaxY; wy++)
                {
                    if (!IsSurfaceVoxel(ws, wx, wy, wz))
                    {
                        continue;
                    }
                    if (!IsSurfaceKit(ws.GetTerrainIdWorld(wx, wy, wz)))
                    {
                        continue;
                    }

                    // Field wins — denser grass masks muddy ground beneath.
                    if (fieldNoise.GetNoise2D(wx, wz) > genData.OverlayFieldThreshold)
                    {
                        ws.SetOverlayIdWorld(wx, wy, wz, OVERLAY_FIELD);
                        continue;
                    }
                    if (dirtNoise.GetNoise2D(wx, wz) > genData.OverlayDirtThreshold)
                    {
                        ws.SetOverlayIdWorld(wx, wy, wz, OVERLAY_DIRT);
                    }
                }
            }
        }
    }

    // Walks every surface voxel and stamps the voxel's kit's DefaultDetail
    // wherever the appropriate noise field crosses the kit's threshold. Two
    // noise fields are kept (Surface vs other) so cave/submerged scatter
    // doesn't visually correlate with the surface scatter directly above;
    // the kit's Purpose picks which one. Frequency is per-kit: each noise
    // object is sampled at base frequency 1, with coords pre-scaled by the
    // kit's DetailNoiseFrequency, so kits within a single zone read
    // different noise patterns (sharp transitions where kits change).
    private static void StampDetailScatter(WorldState ws, WorldGenData genData)
    {
        var surfaceNoise = new FastNoiseLite();
        surfaceNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        surfaceNoise.Seed = DETAIL_NOISE_SEED;
        surfaceNoise.Frequency = 1f;
        surfaceNoise.FractalOctaves = 2;

        var subsurfaceNoise = new FastNoiseLite();
        subsurfaceNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        subsurfaceNoise.Seed = SUBSURFACE_NOISE_SEED;
        subsurfaceNoise.Frequency = 1f;
        subsurfaceNoise.FractalOctaves = 2;

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
                for (int wy = worldMinY; wy < worldMaxY; wy++)
                {
                    if (!IsSurfaceVoxel(ws, wx, wy, wz))
                    {
                        continue;
                    }
                    // IsSurfaceVoxel accepts water above (it's "non-solid"),
                    // which suits the kit-tagging passes but would scatter
                    // upright sprites at the water surface. Caves and test
                    // lakes create new water voxels AFTER TagSubmergedKits
                    // runs — the lake floor still carries SurfaceKit and
                    // would otherwise spawn grass inside the water. Reject
                    // any surface voxel whose air-above slot is water.
                    if (ws.GetVoxelWorld(wx, wy + 1, wz) == VoxelType.Water)
                    {
                        continue;
                    }

                    // Surface detail follows the DETERMINISTIC dominant zone
                    // (argmax of the smooth zone-weight kernel), NOT this voxel's
                    // stamped kit. Kit borders are assigned by a per-column random
                    // hash (PickWeightedZoneFromHash) so the terrain reads as a
                    // jagged organic transition once the shader blends it — but
                    // detail renders each per-column pick as a discrete sprite, so
                    // that RNG shows up as a salt-and-pepper of off-biome grass
                    // (and the occasional dense off-biome clump where the hash
                    // rolled one way several columns running). Keying detail off
                    // the dominant zone instead snaps its boundary to the blend
                    // midline, so detail tracks the terrain's visual transition
                    // rather than its per-voxel randomness. Cave / submerged
                    // detail keeps the voxel's own kit (less visible, not the
                    // reported problem). The chosen kit's DetailNoise* thresholds
                    // still drive presence/strength below.
                    int voxelTerrainId = ws.GetTerrainIdWorld(wx, wy, wz);
                    bool isSurface = IsSurfaceKit(voxelTerrainId);
                    TerrainKitData kit = isSurface
                        ? (DominantZoneSurfaceKit(wx, wz, genData.Zones) ?? ResolveKit(voxelTerrainId))
                        : ResolveKit(voxelTerrainId);
                    if (kit == null || kit.DefaultDetail == null)
                    {
                        continue;
                    }

                    FastNoiseLite noise = isSurface ? surfaceNoise : subsurfaceNoise;
                    float n = noise.GetNoise2D(wx * kit.DetailNoiseFrequency, wz * kit.DetailNoiseFrequency);
                    if (n <= kit.DetailNoiseThreshold)
                    {
                        continue;
                    }

                    // Map noise (threshold..1) to (strengthMin..255). The kit
                    // owns both the threshold and the floor, so a sandstone-
                    // cave kit can thin its pebble scatter without affecting
                    // a sibling kit in the same zone.
                    float t = (n - kit.DetailNoiseThreshold) / Math.Max(0.0001f, 1f - kit.DetailNoiseThreshold);
                    int strengthMin = kit.DetailStrengthMin;
                    int strength = strengthMin + (int)(t * (255 - strengthMin));
                    strength = Mathf.Clamp(strength, 0, 255);
                    if (strength <= 0)
                    {
                        continue;
                    }

                    ws.SetDetailGroupWorld(wx, wy, wz, DetailIndexOf(kit.DefaultDetail));
                    ws.SetDetailStrengthWorld(wx, wy, wz, strength);
                }
            }
        }
    }

    // SurfaceKit of the zone with the highest weight at column (wx, wz) — the
    // deterministic dominant zone. Uses the same KitBlendRadius kernel the
    // (random) kit-border hash samples, so the dominant flips exactly at that
    // kernel's midline and the detail boundary sits where the terrain blend
    // visually crosses over. Returns null when no zone has positive weight or
    // the winner has no SurfaceKit, so the caller falls back to the voxel's own
    // stamped kit. See StampDetailScatter for why detail keys off the dominant
    // zone rather than the per-column random pick.
    private static TerrainKitData DominantZoneSurfaceKit(int wx, int wz, ZoneGenData[] zones)
    {
        int n = zones != null ? zones.Length : 0;
        if (n == 0) { return null; }
        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        GetZoneGenWeights(wx, wz, n, weights, _activeGenData?.KitBlendRadius ?? 2.0f);
        int best = -1;
        float bestW = 0f;
        for (int i = 0; i < n; i++)
        {
            if (weights[i] > bestW)
            {
                bestW = weights[i];
                best = i;
            }
        }
        return best >= 0 ? zones[best]?.SurfaceKit : null;
    }

    // Stamp OVERLAY_DIRT on "surface voxels" (solid with air directly above)
    // whose local neighborhood slope is in [EdgeMinDiff, EdgeMaxDiff-1].
    // Per-voxel, not per-column: correctly handles cave floors, overhangs, and
    // ledges because the ±EdgeScanWindow clip keeps each voxel's comparison
    // local to its own walkable layer. Currently unused (see the disabled call
    // in Generate); reads its tuning from the active WorldGenData.
    private static void StampEdgeOverlays(WorldState ws)
    {
        int edgeScanWindow = _activeGenData?.EdgeScanWindow ?? 4;
        int edgeMinDiff = _activeGenData?.EdgeMinDiff ?? 1;
        int edgeMaxDiff = _activeGenData?.EdgeMaxDiff ?? 3;
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
                        int neighborDiff = FindNearestSurfaceDiff(ws, nx, wy, nz, edgeScanWindow);
                        if (neighborDiff > maxDiff)
                        {
                            maxDiff = neighborDiff;
                        }
                    }

                    if (maxDiff >= edgeMinDiff && maxDiff < edgeMaxDiff)
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

        // Per-chunk baseline tree count comes from the kit at the chunk center.
        // Kit-level (not zone-level kernel-blended) because the per-cell tree
        // *placement* below also reads from the cell's kit, so a chunk straddling
        // a shore→inland kit border gets its baseline count from whichever side
        // its center sits on. This is fine in practice — chunk centers aren't
        // visually privileged and trees are sparse enough that small count
        // jumps at chunk boundaries don't read.
        ZoneGenData[] zonesArr = genData.Zones ?? System.Array.Empty<ZoneGenData>();
        int chunkCenterWx = chunkCoord.X * ChunkState.SIZE + ChunkState.SIZE / 2;
        int chunkCenterWz = chunkCoord.Z * ChunkState.SIZE + ChunkState.SIZE / 2;
        int chunkCenterSy = SurfaceYAt(chunkCenterWx, chunkCenterWz);
        TerrainKitData chunkCenterKit = ResolveKit(ws.GetTerrainIdWorld(chunkCenterWx, chunkCenterSy, chunkCenterWz));
        int treesPerChunkMin = chunkCenterKit?.TreesPerChunkMin ?? 0;
        int treesPerChunkMax = chunkCenterKit?.TreesPerChunkMax ?? 0;
        int treeCount = treesPerChunkMax >= treesPerChunkMin
            ? rng.Next(treesPerChunkMin, treesPerChunkMax + 1)
            : 0;

        var treedCells = new HashSet<(int, int)>();

        // One reusable palette refilled per cell (via WeightedScene.Fill) for
        // both the tree and tall-grass passes — avoids allocating a WeightedList
        // for every scattered prop.
        var scenePalette = new WeightedList<PackedScene>();

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
            int sy = SurfaceYAt(wx, wz);
            TerrainKitData cellKit = ResolveKit(ws.GetTerrainIdWorld(wx, sy, wz));
            WeightedScene.Fill(scenePalette, cellKit?.TreeScenes);
            if (scenePalette.Count == 0)
            {
                return false;
            }
            PackedScene scene = scenePalette.Choose(rng);
            if (scene == null)
            {
                return false;
            }
            // +1.5 (not +1) because ChunkMesherDC's shallow-Y smoothing
            // averages a flat grass column's top face to 0.5 above the
            // voxel-grid top — anchoring at +1 buries sprites half a voxel
            // into the visible ground.
            ws.AddEntity(new PropSimState(PropType.Tree,
                new Vector3(wx + 0.5f, sy + 1.5f, wz + 0.5f),
                scene)
            {
                RotationY = (float)rng.NextDouble() * Mathf.Tau,
            });
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
            // of snapping on chunk seams. Forest tuning is per-kit, looked up at
            // each cell's surface voxel — a shore-kit strip inside a forest zone
            // can run with its own threshold/density and stop trees abruptly.
            for (int localX = 0; localX < ChunkState.SIZE; localX++)
            {
                for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
                {
                    int wx = chunkCoord.X * ChunkState.SIZE + localX;
                    int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                    int sy = SurfaceYAt(wx, wz);
                    TerrainKitData kit = ResolveKit(ws.GetTerrainIdWorld(wx, sy, wz));
                    if (kit == null)
                    {
                        continue;
                    }
                    float f = forestNoise.GetNoise2D(wx * kit.ForestNoiseFrequency, wz * kit.ForestNoiseFrequency);
                    if (f < kit.ForestThreshold)
                    {
                        continue;
                    }
                    float t = (f - kit.ForestThreshold) / Math.Max(0.0001f, 1f - kit.ForestThreshold);
                    float density = kit.ForestDensity * Mathf.Clamp(t, 0f, 1f);
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

                    int sy = SurfaceYAt(wx, wz);
                    TerrainKitData cellKit = ResolveKit(ws.GetTerrainIdWorld(wx, sy, wz));
                    WeightedScene.Fill(scenePalette, cellKit?.TallGrassScenes);
                    if (scenePalette.Count == 0)
                    {
                        continue;
                    }
                    PackedScene grassScene = scenePalette.Choose(rng);
                    if (grassScene == null)
                    {
                        continue;
                    }
                    float grassJitter = genData.TallGrassJitter;
                    float jitterX = ((float)rng.NextDouble() * 2f - 1f) * grassJitter;
                    float jitterZ = ((float)rng.NextDouble() * 2f - 1f) * grassJitter;
                    ws.AddEntity(new PropSimState(PropType.Foliage,
                        new Vector3(wx + 0.5f + jitterX, sy + 1.5f, wz + 0.5f + jitterZ),
                        grassScene)
                    {
                        RotationY = (float)rng.NextDouble() * Mathf.Tau,
                    });
                }
            }
        }

        // Surface pass: per grass column, pick the kernel-weighted zone,
        // iterate its SurfaceEntities and roll each entry's area chance.
        // Each entry's Spawn() handles its own EntitySimState construction;
        // the loop only needs to know how to gate by skip flag and dispatch.
        // Composite entries (SpawnGroupData) read the SpawnContext to do
        // their own rejection-sampled scatter — no per-subclass special-
        // casing here.
        var surfaceContext = new SpawnContext
        {
            SurfaceYAt = SurfaceYAt,
            IsValidColumn = IsGrassyAt,
            IsFlatColumn = (wx, wz) => IsFlatTerrainAt(wx, wz, heightMap),
        };
        if (!skipMobs || !skipInteractives)
        {
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
                    if (rg?.SurfaceEntities?.Entries == null)
                    {
                        continue;
                    }
                    int sy = SurfaceYAt(wx, wz);
                    // Anchor at the ground top (top face of the surface voxel),
                    // matching the cave pass below. Every SpawnEntryData sits
                    // with its scene root on this anchor; any per-entity Y
                    // offset is authored inside the scene itself.
                    var pos = new Vector3(wx + 0.5f, sy + 1f, wz + 0.5f);
                    foreach (SpawnEntryData entry in rg.SurfaceEntities.Entries)
                    {
                        if (entry == null) { continue; }
                        bool isMob = entry is MobSpawnEntry;
                        if (isMob ? skipMobs : skipInteractives) { continue; }
                        if (!entry.RollAreaChance(rng)) { continue; }
                        entry.TrySpawn(ws, pos, rng, surfaceContext);
                    }
                }
            }
        }

        // Cave-pocket pass: scan the full vertical column and roll the
        // matching zone's CaveEntities anywhere there's a 2-voxel air
        // pocket with a solid floor and a ceiling within reach (the
        // "is enclosed" test is what distinguishes cave pockets from open
        // surface). No SpawnContext is supplied — cave-pocket cells are
        // pre-validated by the loop, and a SpawnGroupData inside a cave
        // list collapses to anchor-only placement.
        int HEAD_CLEARANCE = genData.CaveHeadClearance;
        int CAVE_CEILING_PROBE = genData.CaveCeilingProbe;
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

                    ZoneGenData rg = PickWeightedZoneData(wx, wz, zonesArr, rng);
                    if (rg?.CaveEntities?.Entries == null)
                    {
                        continue;
                    }
                    var pos = new Vector3(wx + 0.5f, wy, wz + 0.5f);
                    foreach (SpawnEntryData entry in rg.CaveEntities.Entries)
                    {
                        if (entry == null) { continue; }
                        bool isMob = entry is MobSpawnEntry;
                        if (isMob ? skipMobs : skipInteractives) { continue; }
                        if (!entry.RollAreaChance(rng)) { continue; }
                        entry.TrySpawn(ws, pos, rng, null);
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
