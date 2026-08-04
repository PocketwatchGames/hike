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
    public const int WORLDGEN_VERSION = 64;

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

    // Per-run zone-placement context (world chunk bounds, spawn chunk, edge
    // noise), set at the top of Generate alongside _activeGenData. PickZoneIndex
    // reads it to evaluate each PlacedZone's ZoneBounds.
    private static ZoneBoundsContext _zoneBoundsContext;

    // Per-run difficulty fields, built at the top of Generate from WorldGenData's
    // zone-leveling knobs. Independently seeded so a zone's monster tier and forge
    // tier vary across the world separately. Sampled by ComputeMobLevel /
    // ComputeForgeLevel at spawn placement — kept as static per-run state for the
    // same reason as _activeGenData (the spawn passes don't thread noise channels
    // through). Null outside a Generate run → the compute methods return 0.
    private static FastNoiseLite _mobLevelNoise;
    private static FastNoiseLite _forgeLevelNoise;

    // Road tread columns laid so far by CarveRoads for the current run. Read by
    // the pathfinder so later roads prefer to merge onto earlier ones. Same
    // static-lifetime rationale as _activeGenData. Reset at the top of CarveRoads.
    private static HashSet<(int, int)> _roadColumns = new();

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

    // TerrainIdOf for authoring tools that stamp a chosen kit (WorldEditor's
    // Terrain brush). Returns false when the kit has no palette slot — no zone
    // in the loaded WorldGenData references it — so the caller can warn rather
    // than silently paint palette slot 0.
    public static bool TryGetTerrainId(TerrainKitData kit, out byte id)
    {
        id = TerrainIdOf(kit);
        return kit != null && _kitIndex != null && _kitIndex.ContainsKey(kit);
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
                AddIfNew(z.surfaceKit, list, seen);
                AddIfNew(z.caveKit, list, seen);
                AddIfNew(z.submergedKit, list, seen);
                AddIfNew(z.shoreKit, list, seen);
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
            arr[i] = gen[i]?.terrain;
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
                if (k.defaultDetail != null && seen.Add(k.defaultDetail))
                {
                    list.Add(k.defaultDetail);
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
    // every chunk before this gets called.
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
    private const int SEED_SALT_ZONEBOUNDS = 0x0E;
    private const int SEED_SALT_POI = 0x0F;
    private const int SEED_SALT_FORGE = 0x11;
    private const int SEED_SALT_MOBLEVEL = 0x12;
    private const int SEED_SALT_FOUNTAIN = 0x13;
    private const int SEED_SALT_MANA_FOUNTAIN = 0x14;
    // Independent of MOBLEVEL so a zone's forge tier and monster tier vary
    // across the world separately rather than tracking one shared field.
    private const int SEED_SALT_FORGELEVEL = 0x15;
    // Post-gen distribution of ZoneGenData.distributedLoot across a zone's chests.
    private const int SEED_SALT_ZONELOOT = 0x16;
    private const int SEED_SALT_TREASURE = 0x17;

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

    // Lerp a per-zone [bandMin, bandMax] difficulty band by `noise` at `position`.
    // Raw Perlin FBm only spans ~±0.55 and clusters near 0, so a plain *0.5+0.5
    // map crushes ~90% of the world into the middle of the band. Divide by the
    // (smaller) spread magnitude and clamp so columns populate the band's extremes
    // too (see zoneLevelNoiseSpread). Returns 0 if the noise isn't built.
    private static int SampleBandedLevel(Vector3 position, float bandMin, float bandMax, FastNoiseLite noise)
    {
        if (noise == null)
        {
            return 0;
        }
        float spread = Mathf.Max(0.01f, _activeGenData.zoneLevelNoiseSpread);
        float n01 = Mathf.Clamp(noise.GetNoise2D(position.X, position.Z) / spread * 0.5f + 0.5f, 0f, 1f);
        return Mathf.RoundToInt(Mathf.Lerp(bandMin, bandMax, n01));
    }

    // Forge power tier at `position`, from the zone's [ForgeLevelMin,
    // ForgeLevelMax] band lerped by the forge-level noise field — independent of
    // the monster field so a zone's forges and monsters vary in difficulty
    // separately. The band is kernel-blended across zone borders so it crossfades
    // rather than snapping at a biome seam. Returns 0 outside a Generate run.
    public static int ComputeForgeLevel(WorldState ws, Vector3 position)
    {
        WorldGenData genData = _activeGenData;
        if (genData == null || _forgeLevelNoise == null)
        {
            return 0;
        }
        BlendedZoneGen bz = SampleBlendedZoneGen(
            Mathf.FloorToInt(position.X), Mathf.FloorToInt(position.Z), genData.ZoneGens);
        return SampleBandedLevel(position, bz.ForgeLevelMin, bz.ForgeLevelMax, _forgeLevelNoise);
    }

    // Difficulty tier for a monster placed at `position`, layered on the
    // descriptor's authored `baseLevel`. An underground spawn — a solid ceiling
    // within mobLevelUndergroundProbe voxels overhead — draws from the zone's
    // [UndergroundMobLevelMin, UndergroundMobLevelMax] band, everything else from
    // [MobLevelMin, MobLevelMax]; either way the band is lerped by the same
    // monster-level noise field, so a cave inherits the difficulty gradient of the
    // ground above it. The total is clamped to [0, mobLevelCap].
    // Sunlight isn't baked yet when mobs are placed (it runs after prop/mob
    // scatter), so "underground" is a direct upward solid scan rather than a
    // sky-exposure read. Called per worldgen mob spawn.
    public static int ComputeMobLevel(WorldState ws, Vector3 position, int baseLevel)
    {
        WorldGenData genData = _activeGenData;
        if (genData == null || _mobLevelNoise == null)
        {
            return Math.Max(0, baseLevel);
        }
        BlendedZoneGen bz = SampleBlendedZoneGen(
            Mathf.FloorToInt(position.X), Mathf.FloorToInt(position.Z), genData.ZoneGens);
        bool under = IsUnderground(ws, position, genData.mobLevelUndergroundProbe);
        float bandMin = under ? bz.UndergroundMobLevelMin : bz.MobLevelMin;
        float bandMax = under ? bz.UndergroundMobLevelMax : bz.MobLevelMax;
        int level = baseLevel + SampleBandedLevel(position, bandMin, bandMax, _mobLevelNoise);
        return Math.Clamp(level, 0, genData.mobLevelCap);
    }

    // True if a solid voxel sits within `probe` voxels straight above the spawn —
    // i.e. the mob is in a cave, tunnel, or under a roof rather than open sky.
    private static bool IsUnderground(WorldState ws, Vector3 position, int probe)
    {
        int wx = Mathf.FloorToInt(position.X);
        int wy = Mathf.FloorToInt(position.Y);
        int wz = Mathf.FloorToInt(position.Z);
        for (int dy = 1; dy <= probe; dy++)
        {
            if (VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(wx, wy + dy, wz)))
            {
                return true;
            }
        }
        return false;
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
        _activeKitPalette = BuildKitPalette(genData?.ZoneGens);
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
        if (genData?.ZoneGens != null)
        {
            foreach (ZoneGenData z in genData.ZoneGens)
            {
                if (z == null) { continue; }
                ClassifyKit(z.surfaceKit, EKitPurpose.Surface);
                ClassifyKit(z.caveKit, EKitPurpose.Cave);
                ClassifyKit(z.submergedKit, EKitPurpose.Submerged);
                ClassifyKit(z.shoreKit, EKitPurpose.Shore);
            }
        }
    }

    public static WorldState Generate(WorldGenData genData, int worldSeed, Vector3I worldSize)
    {
        BindActivePalettes(genData);
        _activeGenData = genData;
        _mobLevelNoise = MakePerlin(DeriveSeed(worldSeed, SEED_SALT_MOBLEVEL), genData.zoneLevelNoiseFrequency, 2);
        _forgeLevelNoise = MakePerlin(DeriveSeed(worldSeed, SEED_SALT_FORGELEVEL), genData.zoneLevelNoiseFrequency, 2);

        var min = new Vector3I(-worldSize.X / 2, -1, -worldSize.Z / 2);
        var max = new Vector3I(min.X + worldSize.X - 1, min.Y + worldSize.Y - 1, min.Z + worldSize.Z - 1);
        var ws = new WorldState(min, max, genData.simData);

        // Zone-placement context. The edge-noise channel wobbles box/circle zone
        // borders; sampled at chunk coords so the border resolves per chunk and
        // the existing blend kernel softens it further. Low frequency → broad,
        // gentle waves rather than a jagged fringe.
        var boundsNoise = MakePerlin(DeriveSeed(worldSeed, SEED_SALT_ZONEBOUNDS), 0.15f, 2);
        var spawnChunk = new Vector2I(
            (int)Math.Floor((double)genData.playerSpawnPosition.X / ChunkState.SIZE),
            (int)Math.Floor((double)genData.playerSpawnPosition.Z / ChunkState.SIZE));
        _zoneBoundsContext = new ZoneBoundsContext(min, max, spawnChunk,
            (cx, cz) => boundsNoise.GetNoise2D(cx, cz));

        BuildZoneStates(ws, genData, worldSeed);
        BuildRegionStates(ws, genData);

        WorldNoise noise = BuildWorldNoise(genData, worldSeed);

        // Build the integer height field once up front. Chunk and prop
        // generation read from this map instead of re-evaluating noise per
        // voxel — the shape is authored here (plateau / ramp / river) so
        // geometry is noise-free by construction.
        var heightMap = BuildHeightMap(ws, genData, noise.Terrain, noise.RampGate, noise.Elevation);

        // Load the authored subscenes and reserve the ground they will cover,
        // so the content passes between here and the stamp leave it alone —
        // they place ENTITIES, and a voxel stamp writes straight through one,
        // leaving rocks standing in the front room. Loading here (not at stamp
        // time) also reports a bad path before the expensive passes.
        var reservedSubscenes = LoadAndReserveSubscenes(genData, heightMap);

        // Resolve each authored POI name (ZoneData.PointsOfInterest) to a flat
        // column inside its zone and register it on WorldState. Runs on the bare
        // heightmap (terrain is "constructed" once BuildHeightMap is done); the
        // road pass and the POI-anchored spawn pass both read this registry.
        ResolvePointsOfInterest(ws, genData, heightMap, worldSeed);

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

        // Terrain is final here (roads regrade later and update the field
        // themselves), so resolve where the ground actually ended up. Every
        // pass below anchors placements to Surface, not the authored Height.
        DeriveSurface(ws, heightMap);

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

        GenerateAllProps(ws, genData, noise.Grass, noise.Forest, heightMap, skipFlags, worldSeed);

        // Authored fixtures (zone clusters, POI placements, region landmarks).
        // Tag everything spawned here as PlacedAsFixture so the road pass routes
        // around it and never clears or regrades under it.
        ws.TaggingFixtures = true;

        // One-off per-zone fixture clusters (hub zone = near-spawn villager /
        // companion / lit campfire / boat; other zones = their landmark
        // cluster), each authored as a ZoneGenData.Fixtures SpawnGroupData.
        PlaceZoneFixtures(ws, genData, heightMap, worldSeed);

        // ZoneGenData.ForgeCount smithing forges per zone (0 = none, e.g. the
        // spawn zone). Scene authored via genData.forge; no-op when unset.
        PlaceZoneForges(ws, genData, heightMap, worldSeed);
        PlaceZoneTreasures(ws, genData, heightMap, worldSeed);

        // A handful of fountains (healing + mana) scattered anywhere across the
        // world. Authored via genData.healingFountain / manaFountain + counts.
        PlaceFountains(ws, genData, genData.healingFountain, genData.healingFountainCount,
            SEED_SALT_FOUNTAIN, heightMap, worldSeed);
        PlaceFountains(ws, genData, genData.manaFountain, genData.manaFountainCount,
            SEED_SALT_MANA_FOUNTAIN, heightMap, worldSeed);

        // Spawn authored content anchored to named POIs (signposts now; bosses /
        // loot / villages later). Replaces the per-region random-column signpost
        // fixtures.
        PlacePoiPlacements(ws, genData, heightMap, worldSeed);

        if ((skipFlags & SKIP_INTERACTIVES) == 0)
        {
            // Per-region landmark fixtures (signpost, knowledge stone), authored
            // as each RegionGenData.Fixtures list.
            PlaceRegionFixtures(ws, genData, heightMap, worldSeed);
        }

        ws.TaggingFixtures = false;

        // Stamp the subscenes reserved up front (voxels + entities), sitting
        // each on the plateau level under its footprint. Env overrides land in a
        // second pass, after the wind/envtag default bake (below).
        var stampedSubscenes = StampReservedSubscenes(ws, reservedSubscenes, heightMap);

        // Pathfind and carve roads between connected POIs. Runs AFTER props so
        // the route can prefer naturally open ground (props add pathfinding
        // cost) and clear the props it does cross, and AFTER the subscene stamp
        // so a building is standing when the route is chosen — its reserved
        // columns are impassable, so the road bends around it rather than
        // regrading its floor out from under it. Grades cliff climbs into
        // walkable ramps by rewriting voxels in place (chunks already exist),
        // then paints the tread overlay. Before ComputeSunlight so the bake sees
        // the regraded geometry.
        CarveRoads(ws, genData, heightMap, worldSeed);
        StampGradeShapes(ws, heightMap, genData.maxGradeStep);

        // AFTER every ground-moving pass: the scatter writes per-voxel channels
        // that a later road regrade or subscene stamp would overwrite wholesale,
        // which is what used to leave a stamped building's terrain margin bald.
        // Roads suppress their own detail here rather than clearing it after.
        if ((skipFlags & SKIP_DETAILS) == 0)
        {
            StampDetailScatter(ws, genData, heightMap);
        }

        // Player spawn point, resolved after road grading so a road crossing the
        // spawn column lands the player on the regraded surface. With
        // spawnAtSurface the authored Y is replaced by the ground surface at
        // (X, Z); otherwise the explicit Y is used verbatim.
        ws.Spawn = ResolveSpawn(genData, heightMap);

        // The air pipeline. Strictly ordered, and each step feeds the next:
        //
        //   roofs      — non-voxel cover, so a roofed room reads as enclosed
        //                exactly as a cave does. Pure math, unlike foliage
        //                occluders (which need PackedScene.Instantiate on the
        //                main thread and are stamped later, in Main). Canopy is
        //                deliberately absent here: a tree should not make a
        //                cell an interior.
        //   sky        — geometry-only VERTICAL cover, for the rain / shelter
        //                consumers. Fog-free, and never feeds classification.
        //   classify   — cover → space class per env cell. Everything under a
        //                ceiling is marked indoors, flatly.
        //   sunlight   — the flooded field, which bleeds sideways through every
        //                aperture. This is the BLEED term: how much outdoors
        //                leaks back in. Never used to classify.
        //   dust       — class → serialized fog, reduced by that bleed.
        //   wind       — from the flooded sunlight, damped by the class.
        //
        // Disk-loaded chunks skip classify/dust entirely: those bytes are
        // serialized, and a painted class must survive the round trip.
        StampRoofSunOcclusion(ws);
        LightEngine.ComputeSkyExposure(ws);
        InteriornessGen.Compute(ws);
        EnvTagGen.ComputeEnvTagGrid(ws);
        // Authored classes beat inferred ones, and must land BEFORE the dust
        // and wind bakes read them — otherwise a hikescene's cells carry its
        // authored class but its air and wind were derived from the class
        // worldgen guessed.
        ApplySubsceneEnvOverrides(ws, stampedSubscenes);
        LightEngine.ComputeSunlight(ws);
        WindGen.ComputeWindGrid(ws);

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

        // Spread each zone's distributedLoot across its chests. Runs last so it
        // sees every chest already placed (cave, camp, fixture, subscene).
        DistributeZoneLoot(ws, genData.ZoneGens, worldSeed);

        _lastHeightMap = heightMap;
        _lastPlateauStep = (int)Math.Max(1, Math.Round(genData.plateauStep));
        return ws;
    }

    // Spread each zone's ZoneGenData.distributedLoot across that zone's chests.
    // Unlike perChestLoot (rolled independently at each chest), a distributedLoot
    // entry's rolled count is a TOTAL number of copies dealt out across the
    // zone's chests round-robin — distinct chests until the total exceeds the
    // chest count, then wrapping. For important / quest items that should appear a
    // fixed number of times per zone (a region recipe cookbook). A zone with no
    // chests places nothing. Deterministic: chests are stable-sorted by position
    // and shuffled with a per-zone seeded RNG, so the deal is independent of
    // entity iteration order.
    private static void DistributeZoneLoot(WorldState ws, ZoneGenData[] zones, int worldSeed)
    {
        if (ws == null || zones == null || zones.Length == 0)
        {
            return;
        }

        // Group every placed chest by its dominant zone.
        var chestsByZone = new Dictionary<int, List<ChestSimState>>();
        foreach (EntitySimState e in ws.AllChunkEntities())
        {
            if (e is not ChestSimState chest)
            {
                continue;
            }
            int wx = Mathf.FloorToInt(chest.WorldPosition.X);
            int wz = Mathf.FloorToInt(chest.WorldPosition.Z);
            int zi = DominantZoneIndex(wx, wz, zones);
            if (zi < 0)
            {
                continue;
            }
            if (!chestsByZone.TryGetValue(zi, out List<ChestSimState> bucket))
            {
                bucket = new List<ChestSimState>();
                chestsByZone[zi] = bucket;
            }
            bucket.Add(chest);
        }

        for (int zi = 0; zi < zones.Length; zi++)
        {
            ItemCountRange[] distributed = zones[zi]?.distributedLoot;
            if (distributed == null || distributed.Length == 0)
            {
                continue;
            }
            if (!chestsByZone.TryGetValue(zi, out List<ChestSimState> chests) || chests.Count == 0)
            {
                continue; // no chests in this zone — distributed loot isn't placed
            }

            // Stable order, then a seeded shuffle so the deal is deterministic
            // regardless of the Dictionary's chunk iteration order.
            chests.Sort(CompareChestByPosition);
            var rng = new Random(StableMix(DeriveSeed(worldSeed, SEED_SALT_ZONELOOT), zi, 0));
            for (int i = chests.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (chests[i], chests[j]) = (chests[j], chests[i]);
            }

            // Deal every entry's copies round-robin from one running cursor, so
            // multiple distributed items also spread across different chests.
            int cursor = 0;
            foreach (ItemCountRange entry in distributed)
            {
                if (entry?.item == null)
                {
                    continue;
                }
                int total = entry.Resolve(rng).count;
                for (int k = 0; k < total; k++)
                {
                    ChestSimState chest = chests[cursor % chests.Count];
                    cursor++;
                    AppendChestLoot(chest, new ItemCount
                    {
                        descriptor = new ItemDescriptor { item = entry.item },
                        count = 1,
                    });
                }
            }
        }
    }

    private static int CompareChestByPosition(ChestSimState a, ChestSimState b)
    {
        int c = a.WorldPosition.X.CompareTo(b.WorldPosition.X);
        if (c != 0) { return c; }
        c = a.WorldPosition.Z.CompareTo(b.WorldPosition.Z);
        if (c != 0) { return c; }
        return a.WorldPosition.Y.CompareTo(b.WorldPosition.Y);
    }

    // Append one rolled ItemCount to a chest's ejection recipe (LootItems may be
    // null for a chest authored with no base loot).
    private static void AppendChestLoot(ChestSimState chest, ItemCount item)
    {
        ItemCount[] existing = chest.LootItems;
        if (existing == null || existing.Length == 0)
        {
            chest.LootItems = new[] { item };
            return;
        }
        var merged = new ItemCount[existing.Length + 1];
        existing.CopyTo(merged, 0);
        merged[existing.Length] = item;
        chest.LootItems = merged;
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
            terrain: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_TERRAIN), genData.terrainNoiseFrequency, genData.terrainNoiseOctaves),
            tunnel: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_TUNNEL), genData.tunnelNoiseFrequency, genData.tunnelNoiseOctaves),
            cave: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_CAVE), FirstZoneGen(genData)?.caveNoiseFrequency ?? 0.04f, genData.caveNoiseOctaves),
            grass: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_GRASS), genData.grassNoiseFrequency, genData.grassNoiseOctaves),
            rampGate: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_PATH), genData.rampGateNoiseFrequency, genData.rampGateNoiseOctaves),
            forest: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_FOREST), 1f, genData.forestNoiseOctaves),
            elevation: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_ELEVATION), genData.elevationNoiseFrequency, genData.elevationNoiseOctaves));
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
        ws.Zones = new ZoneState[genData.ZoneGens.Length];
        for (int i = 0; i < genData.ZoneGens.Length; i++)
        {
            float angle = zoneRng.RandfRange(0f, Mathf.Tau);
            ws.Zones[i] = new ZoneState
            {
                Data = genData.ZoneGens[i]?.zone,
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
        RegionGenData[] regionPalette = genData.regions ?? [];
        ws.Regions = new RegionState[regionPalette.Length];
        for (int i = 0; i < regionPalette.Length; i++)
        {
            ws.Regions[i] = new RegionState { Data = regionPalette[i]?.region };
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

    // Voxel-space XZ column range of one of the 4 legacy quadrants (matching the
    // PickRegionIndex order: 0=NE, 1=NW, 2=SE, 3=SW), clipped to the world
    // extent. Returns false if the world doesn't span the quadrant (so the
    // caller skips that region). Still used by the quadrant-based region
    // fixtures; zone placement now goes through ZoneBounds.
    private static bool QuadrantColumnRange(int quadrant, int worldMinX, int worldMaxX, int worldMinZ, int worldMaxZ,
        out int xLo, out int xHi, out int zLo, out int zHi)
    {
        bool east = quadrant == 0 || quadrant == 2;    // X >= 0
        bool north = quadrant == 0 || quadrant == 1;   // Z >= 0
        xLo = east ? Math.Max(0, worldMinX) : worldMinX;
        xHi = east ? worldMaxX : Math.Min(-1, worldMaxX);
        zLo = north ? Math.Max(0, worldMinZ) : worldMinZ;
        zHi = north ? worldMaxZ : Math.Min(-1, worldMaxZ);
        return xHi >= xLo && zHi >= zLo;
    }

    // Rejection-sample a column inside [xLo,xHi]x[zLo,zHi] until `valid` passes,
    // capped at genData.FixturePlacementMaxTries. Lets the one-off fixture
    // passes land a landmark on a flat / grassy column without scanning the
    // whole footprint. Returns false if no column qualified.
    private static bool TryRollColumn(Random rng, WorldGenData genData,
        int xLo, int xHi, int zLo, int zHi, Func<int, int, bool> valid, out int rx, out int rz)
    {
        int maxTries = genData.fixturePlacementMaxTries;
        for (int i = 0; i < maxTries; i++)
        {
            int x = rng.Next(xLo, xHi + 1);
            int z = rng.Next(zLo, zHi + 1);
            if (valid(x, z)) { rx = x; rz = z; return true; }
        }
        rx = xLo;
        rz = zLo;
        return false;
    }

    // True iff (wx, wz) is a flat-dry-grass column with a real (non-air,
    // non-water) ground voxel and air directly above — the shared surface
    // validity used by the one-off fixture passes' SpawnContext.
    private static bool IsGrassySurfaceAt(WorldState ws, int wx, int wz, HeightMap heightMap)
    {
        if (!IsFlatDryGrassAt(wx, wz, heightMap)) { return false; }
        if (heightMap.IsNoSpawn(wx, wz)) { return false; }
        int sy = heightMap.GetSurface(wx, wz);
        VoxelType ground = ws.GetVoxelWorld(wx, sy, wz);
        if (ground == VoxelType.Air || ground == VoxelType.Water) { return false; }
        return ws.GetVoxelWorld(wx, sy + 1, wz) == VoxelType.Air;
    }

    // One-off per-zone fixture clusters. Each zone's ZoneGenData.Fixtures group
    // fires ONCE at an anchor column (vs the SurfaceEntities density scan). The
    // anchor comes from the zone's ZoneBounds: box/circle bounds anchor at their
    // center (the start area's near-spawn villager / campfire / dog), while
    // bounds with no fixed center (quadrant/everywhere) roll a random flat-dry
    // column inside their footprint. The group's ScatterRadius spreads members.
    private static void PlaceZoneFixtures(WorldState ws, WorldGenData genData, HeightMap heightMap, int worldSeed)
    {
        PlacedZone[] zones = genData.zones ?? System.Array.Empty<PlacedZone>();
        if (zones.Length == 0) { return; }

        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        var rng = new Random(DeriveSeed(worldSeed, SEED_SALT_FIXTURE));
        var context = new SpawnContext
        {
            SurfaceYAt = (wx, wz) => heightMap.GetSurface(wx, wz),
            IsValidColumn = (wx, wz) => IsGrassySurfaceAt(ws, wx, wz, heightMap),
            IsFlatColumn = (wx, wz) => IsFlatTerrainAt(wx, wz, heightMap),
        };

        for (int zi = 0; zi < zones.Length; zi++)
        {
            PlacedZone placed = zones[zi];
            SpawnGroupData fixtures = placed?.zoneGen?.fixtures;
            if (fixtures == null) { continue; }

            Vector3I anchorCol;
            if (placed.bounds != null
                && placed.bounds.TryGetAnchorChunk(_zoneBoundsContext, out Vector2I anchorChunk))
            {
                // Fixed-center bounds (box/circle): anchor at the bounds center,
                // converting chunk → the column at the chunk's center voxel.
                anchorCol = new Vector3I(
                    anchorChunk.X * ChunkState.SIZE + ChunkState.SIZE / 2,
                    0,
                    anchorChunk.Y * ChunkState.SIZE + ChunkState.SIZE / 2);
            }
            else
            {
                // No fixed center: roll a flat-dry column anywhere inside the
                // zone's bounds (rejection-sampled across the whole world).
                ZoneBounds bounds = placed.bounds;
                if (!TryRollColumn(rng, genData, worldMinX, worldMaxX, worldMinZ, worldMaxZ,
                        (wx, wz) => IsFlatTerrainAt(wx, wz, heightMap)
                            && (bounds == null || bounds.Contains(
                                (int)Math.Floor((double)wx / ChunkState.SIZE),
                                (int)Math.Floor((double)wz / ChunkState.SIZE),
                                _zoneBoundsContext)),
                        out int rx, out int rz))
                {
                    continue;
                }
                anchorCol = new Vector3I(rx, 0, rz);
            }

            int sy = heightMap.GetSurface(anchorCol.X, anchorCol.Z);
            var anchor = new Vector3(anchorCol.X + 0.5f, sy + 1f, anchorCol.Z + 0.5f);
            fixtures.Spawn(ws, anchor, rng, context);
        }
    }

    // Scatter smithing forges into each zone, ZoneGenData.ForgeCount per zone
    // (0 to opt out — the spawn/village zone typically does). Each forge lands
    // on its own rejection-sampled flat column inside the zone's bounds,
    // independent of the zone's fixture anchor so it never stacks on the home
    // campfire. The forge scene is authored once on genData.forge; no-op when
    // that is unset.
    // Place each zone's one buried treasure (song scroll or crowns) at a flat
    // column inside the zone, stamping its authored treasureName onto the spot so
    // a treasure map can point to it by name (BuriedSpot re-registers the anchor
    // into WorldState.TreasureSpots on stream-in). The treasure exists in the
    // world independently — the player can dig it up with or without the map.
    private static void PlaceZoneTreasures(WorldState ws, WorldGenData genData, HeightMap heightMap, int worldSeed)
    {
        PlacedZone[] zones = genData.zones ?? System.Array.Empty<PlacedZone>();
        if (zones.Length == 0) { return; }

        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        var rng = new Random(DeriveSeed(worldSeed, SEED_SALT_TREASURE));

        for (int zi = 0; zi < zones.Length; zi++)
        {
            ZoneGenData zg = zones[zi]?.zoneGen;
            BuriedSpotSpawnEntry spot = zg?.treasureSpot;
            string name = zg?.treasureName;
            // Each name is world-unique; first zone to claim it wins (guards a
            // WorldGenData that lists the same template zone twice).
            if (spot?.scene == null || spot.data == null || string.IsNullOrEmpty(name)
                || ws.TreasureSpots.ContainsKey(name))
            {
                continue;
            }
            ZoneBounds bounds = zones[zi]?.bounds;
            if (!TryRollColumn(rng, genData, worldMinX, worldMaxX, worldMinZ, worldMaxZ,
                    (wx, wz) => IsFlatTerrainAt(wx, wz, heightMap)
                        && (bounds == null || bounds.Contains(
                            (int)Math.Floor((double)wx / ChunkState.SIZE),
                            (int)Math.Floor((double)wz / ChunkState.SIZE),
                            _zoneBoundsContext)),
                    out int rx, out int rz))
            {
                continue;
            }
            int sy = heightMap.GetSurface(rx, rz);
            var anchor = new Vector3(rx + 0.5f, sy + 1f, rz + 0.5f);
            var state = new BuriedSpotSimState(anchor, spot.scene, spot.data) { TreasureName = name };
            ws.AddEntity(state);
            ws.TreasureSpots[name] = anchor;
        }
    }

    private static void PlaceZoneForges(WorldState ws, WorldGenData genData, HeightMap heightMap, int worldSeed)
    {
        ForgeSpawnEntry forge = genData.forge;
        if (forge == null) { return; }
        PlacedZone[] zones = genData.zones ?? System.Array.Empty<PlacedZone>();
        if (zones.Length == 0) { return; }

        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        var rng = new Random(DeriveSeed(worldSeed, SEED_SALT_FORGE));
        var context = new SpawnContext
        {
            SurfaceYAt = (wx, wz) => heightMap.GetSurface(wx, wz),
            IsValidColumn = (wx, wz) => IsGrassySurfaceAt(ws, wx, wz, heightMap),
            IsFlatColumn = (wx, wz) => IsFlatTerrainAt(wx, wz, heightMap),
        };

        for (int zi = 0; zi < zones.Length; zi++)
        {
            int count = zones[zi]?.zoneGen?.forgeCount ?? 0;
            if (count <= 0) { continue; }
            ZoneBounds bounds = zones[zi]?.bounds;
            for (int f = 0; f < count; f++)
            {
                if (!TryRollColumn(rng, genData, worldMinX, worldMaxX, worldMinZ, worldMaxZ,
                        (wx, wz) => IsFlatTerrainAt(wx, wz, heightMap)
                            && (bounds == null || bounds.Contains(
                                (int)Math.Floor((double)wx / ChunkState.SIZE),
                                (int)Math.Floor((double)wz / ChunkState.SIZE),
                                _zoneBoundsContext)),
                        out int rx, out int rz))
                {
                    continue;
                }
                int sy = heightMap.GetSurface(rx, rz);
                var anchor = new Vector3(rx + 0.5f, sy + 1f, rz + 0.5f);
                forge.Spawn(ws, anchor, rng, context);
            }
        }
    }

    // Scatter `count` fountains of one variant across the whole world, each on
    // its own rejection-sampled flat-grass column (no per-zone rule — a fountain
    // can land in any biome). No-op when the entry is unset or the count is zero.
    private static void PlaceFountains(WorldState ws, WorldGenData genData, FountainSpawnEntry fountain,
        int count, int seedSalt, HeightMap heightMap, int worldSeed)
    {
        if (fountain == null || count <= 0) { return; }

        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        var rng = new Random(DeriveSeed(worldSeed, seedSalt));
        var context = new SpawnContext
        {
            SurfaceYAt = (wx, wz) => heightMap.GetSurface(wx, wz),
            IsValidColumn = (wx, wz) => IsGrassySurfaceAt(ws, wx, wz, heightMap),
            IsFlatColumn = (wx, wz) => IsFlatTerrainAt(wx, wz, heightMap),
        };

        for (int i = 0; i < count; i++)
        {
            if (!TryRollColumn(rng, genData, worldMinX, worldMaxX, worldMinZ, worldMaxZ,
                    (wx, wz) => IsGrassySurfaceAt(ws, wx, wz, heightMap),
                    out int rx, out int rz))
            {
                continue;
            }
            int sy = heightMap.GetSurface(rx, rz);
            var anchor = new Vector3(rx + 0.5f, sy + 1f, rz + 0.5f);
            fountain.Spawn(ws, anchor, rng, context);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Points of interest & roads
    // ─────────────────────────────────────────────────────────────────────

    // Minimum spacing (voxels) kept between two distinct POIs rolled in the
    // same zone, so multiple named places don't land on top of each other.
    private const float POI_MIN_SPACING = 8f;

    // Resolve every authored POI name to a concrete world position and register
    // it on WorldState.PointsOfInterest. Each zone (PlacedZone) contributes the
    // names on its ZoneData.PointsOfInterest; a name is resolved by the FIRST
    // zone that lists it (names are world-unique — a ZoneData reused across
    // several placements doesn't multiply its POIs). Position is a random flat,
    // dry column inside the zone's bounds, mirroring the random-column branch of
    // PlaceZoneFixtures.
    private static void ResolvePointsOfInterest(WorldState ws, WorldGenData genData, HeightMap heightMap, int worldSeed)
    {
        ws.PointsOfInterest.Clear();
        PlacedZone[] zones = genData.zones ?? System.Array.Empty<PlacedZone>();
        if (zones.Length == 0) { return; }

        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        var rng = new Random(DeriveSeed(worldSeed, SEED_SALT_POI));

        foreach (PlacedZone placed in zones)
        {
            string[] names = placed?.zoneGen?.pointsOfInterest;
            if (names == null) { continue; }
            ZoneBounds bounds = placed.bounds;
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name) || ws.PointsOfInterest.ContainsKey(name))
                {
                    continue;
                }
                bool Valid(int wx, int wz)
                {
                    if (!IsFlatTerrainAt(wx, wz, heightMap)) { return false; }
                    // Reserved ground is impassable to the road pass, so a POI
                    // landing inside a subscene footprint would strand every
                    // road that names it — no route in or out of a building.
                    if (heightMap.IsNoSpawn(wx, wz)) { return false; }
                    if (bounds != null && !bounds.Contains(
                            (int)Math.Floor((double)wx / ChunkState.SIZE),
                            (int)Math.Floor((double)wz / ChunkState.SIZE),
                            _zoneBoundsContext))
                    {
                        return false;
                    }
                    foreach (Vector3 existing in ws.PointsOfInterest.Values)
                    {
                        float dx = existing.X - (wx + 0.5f);
                        float dz = existing.Z - (wz + 0.5f);
                        if (dx * dx + dz * dz < POI_MIN_SPACING * POI_MIN_SPACING)
                        {
                            return false;
                        }
                    }
                    return true;
                }
                if (TryRollColumn(rng, genData, worldMinX, worldMaxX, worldMinZ, worldMaxZ, Valid, out int rx, out int rz))
                {
                    int sy = heightMap.GetSurface(rx, rz);
                    ws.PointsOfInterest[name] = new Vector3(rx + 0.5f, sy + 1f, rz + 0.5f);
                }
                else
                {
                    GD.PushWarning($"WorldGen: could not place point of interest '{name}' inside its zone.");
                }
            }
        }
    }

    // Spawn authored content at each POI named by a WorldGenData PoiPlacement.
    // Position is the POI's registered ground-top anchor; entries run through
    // the same TrySpawn path the region fixtures use (so a SignpostSpawnEntry
    // behaves exactly as before, just anchored to a named place instead of a
    // rolled column).
    private static void PlacePoiPlacements(WorldState ws, WorldGenData genData, HeightMap heightMap, int worldSeed)
    {
        PoiPlacement[] placements = genData.pointsOfInterestPlacements ?? System.Array.Empty<PoiPlacement>();
        if (placements.Length == 0) { return; }

        var context = new SpawnContext
        {
            SurfaceYAt = (wx, wz) => heightMap.GetSurface(wx, wz),
            IsValidColumn = (wx, wz) => IsGrassySurfaceAt(ws, wx, wz, heightMap),
            IsFlatColumn = (wx, wz) => IsFlatTerrainAt(wx, wz, heightMap),
        };

        for (int pi = 0; pi < placements.Length; pi++)
        {
            PoiPlacement placement = placements[pi];
            if (placement == null || string.IsNullOrEmpty(placement.poiName) || placement.content?.entries == null)
            {
                continue;
            }
            if (!ws.PointsOfInterest.TryGetValue(placement.poiName, out Vector3 pos))
            {
                GD.PushWarning($"WorldGen: POI placement references unresolved point of interest '{placement.poiName}'.");
                continue;
            }
            var rng = new Random(StableMix(DeriveSeed(worldSeed, SEED_SALT_SIGNPOST), pi, 1));
            foreach (SpawnEntryData entry in placement.content.entries)
            {
                entry?.TrySpawn(ws, pos, rng, context);
            }
        }
    }

    // Pathfind and carve a road tread per authored connection. Runs AFTER props
    // (and fixtures) exist so the route can avoid them and clear the ones it
    // crosses. Roads are processed in order; each sees the columns laid by
    // earlier roads as low-cost (RoadReuseCostMultiplier) so the network branches
    // off existing roads instead of running parallel tracks. Because chunks are
    // already built, grading rewrites voxels in place (cut/fill) rather than
    // feeding GenerateChunk.
    private static void CarveRoads(WorldState ws, WorldGenData genData, HeightMap heightMap, int worldSeed)
    {
        _roadColumns = new HashSet<(int, int)>();

        RoadConnection[] roads = genData.roads ?? System.Array.Empty<RoadConnection>();
        if (roads.Length == 0) { return; }

        // Index entities the road should route around, by column. Two kinds:
        //   - scatter scenery (IsRoadObstacle: trees, tall grass, climbable /
        //     berry trees) — avoided AND cleared where the tread crosses them.
        //   - authored fixtures (PlacedAsFixture: campfires, wells, signposts,
        //     villages, ...) — avoided but NEVER cleared, and their columns are
        //     skipped when stamping the tread so the road can't regrade under
        //     them. Mobs / loot are neither — roads ignore them.
        // protectedColumns marks the fixture columns the tread must leave intact.
        var obstacleColumns = new Dictionary<(int, int), List<EntitySimState>>();
        var protectedColumns = new HashSet<(int, int)>();
        foreach (List<EntitySimState> bucket in ws._entities.Values)
        {
            foreach (EntitySimState e in bucket)
            {
                if (!e.IsRoadObstacle && !e.PlacedAsFixture) { continue; }
                var key = (Mathf.FloorToInt(e.WorldPosition.X), Mathf.FloorToInt(e.WorldPosition.Z));
                if (!obstacleColumns.TryGetValue(key, out List<EntitySimState> list))
                {
                    list = new List<EntitySimState>();
                    obstacleColumns[key] = list;
                }
                list.Add(e);
                if (e.PlacedAsFixture) { protectedColumns.Add(key); }
            }
        }

        int ci = 0;
        foreach (RoadConnection conn in roads)
        {
            int connIndex = ci++;
            if (conn == null) { continue; }
            if (!ws.PointsOfInterest.TryGetValue(conn.fromPoi ?? "", out Vector3 a)
                || !ws.PointsOfInterest.TryGetValue(conn.toPoi ?? "", out Vector3 b))
            {
                GD.PushWarning($"WorldGen: road '{conn.fromPoi}' → '{conn.toPoi}' references an unresolved point of interest; skipped.");
                continue;
            }

            int minWidth = Math.Max(1, Math.Min(conn.minWidth, conn.maxWidth));
            int maxWidth = Math.Max(minWidth, conn.maxWidth);
            var start = (Mathf.FloorToInt(a.X), Mathf.FloorToInt(a.Z));
            var goal = (Mathf.FloorToInt(b.X), Mathf.FloorToInt(b.Z));
            // Route with the widest case so the corridor it picks stays clear
            // even where the tread later swells to MaxWidth.
            List<(int, int)> path = FindRoadPath(heightMap, genData, start, goal, obstacleColumns, maxWidth);
            if (path == null || path.Count == 0)
            {
                GD.PushWarning($"WorldGen: no road route found '{conn.fromPoi}' → '{conn.toPoi}'.");
                continue;
            }

            BlockData tex = conn.texture ?? genData.roadDefaultTexture;
            byte overlay = tex != null ? (byte)tex.atlasBaseIndex : OVERLAY_DIRT;
            var widthRng = new Random(StableMix(DeriveSeed(worldSeed, SEED_SALT_ROAD), connIndex, 0));
            GradeCarvePaintRoad(ws, genData, heightMap, path, minWidth, maxWidth, overlay, widthRng, obstacleColumns, protectedColumns);
        }
    }

    // 8-connected A* over world columns between two POI columns. Cost favours
    // flat / gently sloped ground, penalizes climbing faster than
    // RoadMaxWalkableStep (× RoadCliffCostMultiplier, scaled by the excess),
    // adds cost for obstacle columns (scatter scenery AND authored fixtures) in
    // the R×R window around each step (R = road width) so roads thread through
    // open ground and around fixtures, and discounts columns already laid by
    // earlier roads (× RoadReuseCostMultiplier) so roads merge. Wet columns
    // (surface at or below water) are impassable. World is small (a few hundred
    // columns per side) so a plain A* is ample.
    private static List<(int, int)> FindRoadPath(HeightMap hm, WorldGenData genData,
        (int x, int z) start, (int x, int z) goal,
        Dictionary<(int, int), List<EntitySimState>> obstacleColumns, int width)
    {
        int minX = hm.WorldMinX, maxX = hm.WorldMaxX, minZ = hm.WorldMinZ, maxZ = hm.WorldMaxZ;
        int sizeX = maxX - minX + 1;
        int sizeZ = maxZ - minZ + 1;
        int Idx(int x, int z) => (x - minX) * sizeZ + (z - minZ);

        int maxStep = Math.Max(1, genData.roadMaxWalkableStep);
        float cliffMult = genData.roadCliffCostMultiplier;
        float reuseMult = genData.roadReuseCostMultiplier;
        float propMult = genData.roadPropCostMultiplier;
        int propScan = width / 2; // R×R window radius

        // Number of obstacle columns (scenery + fixtures) in the R×R window.
        int ObstacleCount(int x, int z)
        {
            int c = 0;
            for (int dx = -propScan; dx <= propScan; dx++)
            {
                for (int dz = -propScan; dz <= propScan; dz++)
                {
                    if (obstacleColumns.ContainsKey((x + dx, z + dz))) { c++; }
                }
            }
            return c;
        }

        // Dry land is passable; only genuinely-submerged columns (top solid voxel
        // below the water plane) are blocked. The threshold is >= WATER_LEVEL, NOT
        // > : a column whose top solid voxel sits exactly at WATER_LEVEL is dry
        // SHORELINE — its walkable top face is at h+1, above the water — matching
        // IsFlatDryGrassAt. Using > was an off-by-one that made shoreline
        // impassable, which is fatal in a swamp sitting at elevation 0 where the
        // POIs themselves land on shoreline and every route then failed.
        bool Passable(int x, int z)
        {
            if (x < minX || x > maxX || z < minZ || z > maxZ) { return false; }
            // Subscenes are stamped before this pass, so a route through one is
            // a route through a building — the tread would regrade its floor
            // out from under it. Blocked outright rather than priced: a footprint
            // is a few dozen columns in an open world, so there is always a way
            // around, and a merely expensive one still gets taken when the POIs
            // line up with it.
            if (hm.IsNoSpawn(x, z)) { return false; }
            return hm.GetSurface(x, z) >= WATER_LEVEL;
        }
        if (!Passable(start.x, start.z) || !Passable(goal.x, goal.z)) { return null; }

        var gScore = new float[sizeX * sizeZ];
        Array.Fill(gScore, float.PositiveInfinity);
        var cameFrom = new int[sizeX * sizeZ];
        Array.Fill(cameFrom, -1);
        var closed = new bool[sizeX * sizeZ];
        var open = new PriorityQueue<int, float>();

        int startIdx = Idx(start.x, start.z);
        int goalIdx = Idx(goal.x, goal.z);
        gScore[startIdx] = 0f;

        // Scale the octile-distance heuristic by the cheapest possible per-step
        // cost so it never overestimates. The reuse discount makes an on-road
        // step cost reuseMult (< 1); an octile heuristic weighted at 1.0 would
        // overestimate the remaining cost of any road-following route, making A*
        // non-optimal and returning a near-straight path that runs PARALLEL to an
        // existing road instead of merging onto it. Weighting by reuseMult keeps
        // it admissible so merges win.
        float hWeight = Math.Min(1f, reuseMult);
        float Heuristic(int x, int z)
        {
            int dx = Math.Abs(x - goal.x);
            int dz = Math.Abs(z - goal.z);
            int diag = Math.Min(dx, dz);
            return ((dx + dz) - (2f - 1.41421356f) * diag) * hWeight;
        }

        open.Enqueue(startIdx, Heuristic(start.x, start.z));

        Span<int> dirsX = stackalloc int[] { 1, -1, 0, 0, 1, 1, -1, -1 };
        Span<int> dirsZ = stackalloc int[] { 0, 0, 1, -1, 1, -1, 1, -1 };

        bool found = false;
        while (open.TryDequeue(out int current, out float _))
        {
            if (closed[current]) { continue; }
            closed[current] = true;
            if (current == goalIdx) { found = true; break; }

            int cx = current / sizeZ + minX;
            int cz = current % sizeZ + minZ;
            int curH = hm.GetSurface(cx, cz);

            for (int d = 0; d < 8; d++)
            {
                int nx = cx + dirsX[d];
                int nz = cz + dirsZ[d];
                if (!Passable(nx, nz)) { continue; }
                int nIdx = Idx(nx, nz);
                if (closed[nIdx]) { continue; }

                float dist = d < 4 ? 1f : 1.41421356f;
                int rise = Math.Abs(hm.GetSurface(nx, nz) - curH);
                float move = dist + rise; // gentle slope adds a mild cost
                if (rise > maxStep)
                {
                    move += (rise - maxStep) * cliffMult * dist;
                }
                if (propMult > 0f)
                {
                    move += ObstacleCount(nx, nz) * propMult * dist;
                }
                if (_roadColumns.Contains((nx, nz)))
                {
                    move *= reuseMult;
                }

                float tentative = gScore[current] + move;
                if (tentative < gScore[nIdx])
                {
                    gScore[nIdx] = tentative;
                    cameFrom[nIdx] = current;
                    open.Enqueue(nIdx, tentative + Heuristic(nx, nz));
                }
            }
        }

        if (!found) { return null; }

        var path = new List<(int, int)>();
        for (int idx = goalIdx; idx != -1; idx = cameFrom[idx])
        {
            path.Add((idx / sizeZ + minX, idx % sizeZ + minZ));
        }
        path.Reverse();
        return path;
    }

    // Grade the route into a walkable profile, then stamp the tread (a disc whose
    // radius follows the road's varying width around each path column) into the
    // voxel grid: cut/fill the column to the graded height, guarantee a solid
    // RoadBedDepth bed (so the road bridges caves/tunnels rather than opening
    // into them), clear detail scatter, paint the overlay, and delete any scatter
    // scenery on the tread. The width is rolled in [minWidth, maxWidth] and held
    // for a random stride (RoadStride*Meters) before re-rolling, so the road
    // swells and pinches. Columns already laid by an EARLIER road (in
    // _roadColumns) are left as-is so the existing road's texture and grade win
    // where roads overlap. Columns with an authored fixture (protectedColumns)
    // are skipped — the road leaves a gap rather than regrading under a landmark.
    // Endpoints (the POIs) are held fixed; the interior is slope-limited to
    // RoadMaxWalkableStep per cell, cutting cliff tops and filling dips.
    private static void GradeCarvePaintRoad(WorldState ws, WorldGenData genData, HeightMap hm,
        List<(int, int)> path, int minWidth, int maxWidth, byte overlay, Random widthRng,
        Dictionary<(int, int), List<EntitySimState>> obstacleColumns,
        HashSet<(int, int)> protectedColumns)
    {
        int n = path.Count;
        int maxStep = Math.Max(1, genData.roadMaxWalkableStep);
        int bedDepth = Math.Max(1, genData.roadBedDepth);
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;

        var t = new int[n];
        for (int i = 0; i < n; i++)
        {
            t[i] = hm.GetSurface(path[i].Item1, path[i].Item2);
        }

        // Gauss-Seidel slope limit on interior columns (endpoints fixed). Each
        // interior column is clamped to within maxStep of both neighbours;
        // iterate to convergence (bounded by n passes).
        for (int pass = 0; pass < n; pass++)
        {
            bool changed = false;
            for (int i = 1; i < n - 1; i++)
            {
                int upper = Math.Min(t[i - 1], t[i + 1]) + maxStep;
                int lower = Math.Max(t[i - 1], t[i + 1]) - maxStep;
                int v = t[i];
                if (lower > upper)
                {
                    v = (t[i - 1] + t[i + 1]) / 2; // locally infeasible: split the difference
                }
                else
                {
                    v = Math.Clamp(v, lower, upper);
                }
                if (v != t[i]) { t[i] = v; changed = true; }
            }
            if (!changed) { break; }
        }

        // Per-point tread radius: hold a rolled width for a random stride, then
        // re-roll. Stride is consumed in along-path distance (diagonal = √2).
        float strideMin = Math.Max(0f, genData.roadStrideMinMeters);
        float strideMax = Math.Max(strideMin, genData.roadStrideMaxMeters);
        int RollWidth() => widthRng.Next(minWidth, maxWidth + 1);
        float RollStride() => strideMin + (float)widthRng.NextDouble() * (strideMax - strideMin);
        int curWidth = RollWidth();
        float strideLeft = RollStride();

        for (int i = 0; i < n; i++)
        {
            int px = path[i].Item1;
            int pz = path[i].Item2;
            int hNew = t[i];

            // Where this road rides exactly on an earlier road (its centerline
            // is already a road column), don't stamp anything — not even the
            // wider tread. The existing road wins entirely, so a wider second
            // road can't paint a different-texture fringe alongside it. (Still
            // advance the stride below.)
            if (!_roadColumns.Contains((px, pz)))
            {
                int radius = Math.Max(0, (curWidth - 1) / 2);
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (dx * dx + dz * dz > radius * radius + radius) { continue; } // disc-ish
                        int wx = px + dx;
                        int wz = pz + dz;
                        if (wx < hm.WorldMinX || wx > hm.WorldMaxX || wz < hm.WorldMinZ || wz > hm.WorldMaxZ)
                        {
                            continue;
                        }
                        // Leave existing road columns alone so the earlier road's
                        // texture and grade win where roads overlap; skip authored
                        // fixtures so the road never regrades under a landmark.
                        // Subscene footprints are barred from the route entirely,
                        // but the tread swells to maxWidth either side of it and
                        // can still reach one.
                        if (_roadColumns.Contains((wx, wz)) || protectedColumns.Contains((wx, wz))
                            || hm.IsNoSpawn(wx, wz))
                        {
                            continue;
                        }
                        StampRoadColumn(ws, hm, wx, wz, hNew, overlay, bedDepth, worldMinY, worldMaxY);
                        RemoveScatterInColumn(ws, obstacleColumns, wx, wz);
                        _roadColumns.Add((wx, wz));
                    }
                }
            }

            // Advance the stride by the step length to the next path point.
            if (i + 1 < n)
            {
                bool diag = path[i + 1].Item1 != px && path[i + 1].Item2 != pz;
                strideLeft -= diag ? 1.41421356f : 1f;
                if (strideLeft <= 0f)
                {
                    curWidth = RollWidth();
                    strideLeft = RollStride();
                }
            }
        }
    }

    // Recompute HeightMap.Surface from the live voxels: the topmost natural
    // terrain voxel in each column. Runs once, after the carving passes and
    // before anything that places content, so every placement pass anchors to
    // ground that actually exists.
    //
    // Architecture (a stamped building's walls) is skipped on purpose — the
    // ground under a wall is still the ground, and lifting the surface onto
    // wall tops would scatter props and mobs across the battlements. Keeping
    // things OUT of a building is the reservation mask's job, not this one's.
    //
    // A column with no natural voxel at all keeps its authored Height.
    private static void DeriveSurface(WorldState ws, HeightMap hm)
    {
        int minY = ws.Min.Y * ChunkState.SIZE;
        int maxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;

        for (int wx = hm.WorldMinX; wx <= hm.WorldMaxX; wx++)
        {
            for (int wz = hm.WorldMinZ; wz <= hm.WorldMaxZ; wz++)
            {
                // Start at the authored height and walk toward the real ground,
                // so the scan costs O(drift) rather than a full column.
                int y = Math.Clamp(hm.GetHeight(wx, wz), minY, maxY);
                if (IsNaturalGround(ws, wx, y, wz))
                {
                    while (y < maxY && IsNaturalGround(ws, wx, y + 1, wz)) { y++; }
                }
                else
                {
                    while (y > minY && !IsNaturalGround(ws, wx, y, wz)) { y--; }
                    if (!IsNaturalGround(ws, wx, y, wz))
                    {
                        continue;
                    }
                }
                hm.Surface[wx - hm.WorldMinX, wz - hm.WorldMinZ] = y;
            }
        }
    }

    // Natural terrain — the materials worldgen fills ground with. Excludes
    // architecture (Stone/Wood walls) and Barrier so they never read as ground.
    private static bool IsNaturalGround(WorldState ws, int wx, int wy, int wz)
    {
        VoxelType v = ws.GetVoxelWorld(wx, wy, wz);
        return v == VoxelType.Terrain || v == VoxelType.Desert || v == VoxelType.Marsh;
    }

    // Re-derive the surface shape channel from the FINISHED geometry.
    //
    // Every pass that moves terrain — plateaus, ramp skirts, road grading —
    // used to be individually responsible for tagging what it built, and each
    // one that forgot (or defaulted through the 4-arg SetVoxelWorld) left a
    // slope stair-stepping. Deriving it once at the end instead means the tag
    // always matches the geometry actually present, and a new height-modifying
    // pass gets correct shading for free.
    //
    // Classification is LAYERED — every solid/open interface in a column, not
    // just the outdoor surface. Cave floors, cavern floors and the ground under
    // an overhang are real surfaces that grade exactly like open terrain; a
    // one-surface-per-column pass leaves all of them with the blanket Y that
    // MarkCaveSurfaceShapes stamps, so a sloping cavern floor stair-steps. It is
    // also why the height field can't drive this: hm.Height names at most one
    // voxel per column, and GenerateCaves breaches the surface as an
    // open-topped pit without updating it (~10% of columns end up with
    // hm.Height pointing at air, worst measured 23 voxels up). hm is used here
    // for horizontal bounds only.
    //
    // Only natural surface material is touched — architectural material keeps
    // its authored SharpAxes. Ceilings and walls stay snapped: a soft cave
    // ceiling interpolates downward through the ceiling-cutaway clip plane and
    // into view. A one-voxel shelf is both floor and ceiling, so it counts as a
    // ceiling and stays snapped (the same guard the per-chunk fill applies).
    private static void StampGradeShapes(WorldState ws, HeightMap hm, int maxGradeStep)
    {
        int minY = ws.Min.Y * ChunkState.SIZE;
        int maxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int sizeX = hm.WorldMaxX - hm.WorldMinX + 1;
        int sizeZ = hm.WorldMaxZ - hm.WorldMinZ + 1;

        // Every floor surface in the world (solid voxel with a non-solid voxel
        // directly above), grouped by column: column n owns
        // surfaces[starts[n] .. starts[n + 1]), descending in Y. Columns are
        // walked in a fixed order so each run is contiguous — no per-column
        // allocation, and no per-column cap that would silently drop surfaces in
        // a heavily caved column.
        var surfaces = new List<int>(sizeX * sizeZ * 2);
        var starts = new int[sizeX * sizeZ + 1];

        for (int ix = 0; ix < sizeX; ix++)
        {
            for (int iz = 0; iz < sizeZ; iz++)
            {
                starts[ix * sizeZ + iz] = surfaces.Count;
                int wx = hm.WorldMinX + ix;
                int wz = hm.WorldMinZ + iz;
                // Above the world ceiling is open sky, so a column that reaches
                // maxY still registers its top voxel as a surface.
                bool aboveSolid = false;
                for (int wy = maxY; wy >= minY; wy--)
                {
                    bool solid = VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(wx, wy, wz));
                    if (solid && !aboveSolid)
                    {
                        surfaces.Add(wy);
                    }
                    aboveSolid = solid;
                }
            }
        }
        starts[sizeX * sizeZ] = surfaces.Count;

        // Far enough outside any grade window that an axis with no facing
        // surface reads as a discontinuity rather than a slope.
        const int NO_SURFACE = 1 << 20;

        // The neighbouring column's surface that this one actually faces: the
        // nearest in Y. At a cave mouth the cave floor and the outdoor surface
        // are one continuous sheet and pair up correctly; across a wall (no
        // surface at all) the axis falls out of the window and stays snapped.
        int FacingSurfaceY(int wx, int wz, int y)
        {
            int ix = Math.Clamp(wx, hm.WorldMinX, hm.WorldMaxX) - hm.WorldMinX;
            int iz = Math.Clamp(wz, hm.WorldMinZ, hm.WorldMaxZ) - hm.WorldMinZ;
            int n = ix * sizeZ + iz;
            int bestY = y + NO_SURFACE;
            for (int k = starts[n]; k < starts[n + 1]; k++)
            {
                if (Math.Abs(surfaces[k] - y) < Math.Abs(bestY - y))
                {
                    bestY = surfaces[k];
                }
            }
            return bestY;
        }

        // Same per-axis rule as HeightMap.IsGrade (see there for why it is per
        // axis, and why the step size rather than the angle is the
        // discriminator), applied to one surface layer.
        bool IsGradeAt(int wx, int wz, int y)
        {
            return HeightMap.AxisIsGrade(y, FacingSurfaceY(wx - 1, wz, y), FacingSurfaceY(wx + 1, wz, y), maxGradeStep)
                || HeightMap.AxisIsGrade(y, FacingSurfaceY(wx, wz - 1, y), FacingSurfaceY(wx, wz + 1, y), maxGradeStep);
        }

        for (int ix = 0; ix < sizeX; ix++)
        {
            for (int iz = 0; iz < sizeZ; iz++)
            {
                int wx = hm.WorldMinX + ix;
                int wz = hm.WorldMinZ + iz;
                int n = ix * sizeZ + iz;
                for (int k = starts[n]; k < starts[n + 1]; k++)
                {
                    int y = surfaces[k];

                    // Every natural surface material, not just Terrain — desert
                    // and marsh columns are their own VoxelType and were being
                    // skipped, so their grades never got re-derived.
                    VoxelType surface = ws.GetVoxelWorld(wx, y, wz);
                    if (surface != VoxelType.Terrain && surface != VoxelType.Desert && surface != VoxelType.Marsh)
                    {
                        continue;
                    }
                    if (y > minY && !VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(wx, y - 1, wz)))
                    {
                        continue;
                    }
                    ws.SetShapeWorld(wx, y, wz, IsGradeAt(wx, wz, y)
                        ? VoxelTypeInfo.SharpAxes.None
                        : VoxelTypeInfo.SharpAxes.Y);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(CVars.gradeDebug.Value))
        {
            GradeDebug.Dump(CVars.gradeDebug.Value, ws,
                (x, z) => hm.GetSurface(x, z), (x, z) => hm.IsGrade(x, z, maxGradeStep));
        }
    }

    // Rewrite one tread column to the graded height: cut solid above / fill solid
    // below, guarantee a solid bed, clear detail, paint the overlay. Re-filled
    // voxels copy the column's existing surface-kit TerrainId so cuts and
    // embankments read as the surrounding terrain (the overlay paints the road on
    // top). Updates the heightmap so later passes (light bake) see the new surface.
    private static void StampRoadColumn(WorldState ws, HeightMap hm, int wx, int wz, int hNew,
        byte overlay, int bedDepth, int worldMinY, int worldMaxY)
    {
        int hOld = hm.Height[wx - hm.WorldMinX, wz - hm.WorldMinZ];

        // Reference kit from a voxel solid in both old and new profiles.
        int refY = Math.Clamp(Math.Min(hOld, hNew), worldMinY, worldMaxY);
        int kitId = ws.GetTerrainIdWorld(wx, refY, wz);

        // Cut: clear everything above the new surface up to the old surface.
        for (int by = hNew + 1; by <= hOld && by <= worldMaxY; by++)
        {
            if (by < worldMinY) { continue; }
            ws.SetVoxelWorld(wx, by, wz, VoxelType.Air);
            ws.SetOverlayIdWorld(wx, by, wz, 0);
            ws.SetDetailGroupWorld(wx, by, wz, 0);
            ws.SetDetailStrengthWorld(wx, by, wz, 0);
        }
        // Fill: add solid up to the new surface where we raised the column.
        for (int by = hOld + 1; by <= hNew && by <= worldMaxY; by++)
        {
            if (by < worldMinY) { continue; }
            ws.SetVoxelWorld(wx, by, wz, VoxelType.Terrain, VoxelTypeInfo.SharpAxes.Y);
            ws.SetTerrainIdWorld(wx, by, wz, kitId);
        }
        // Bed: guarantee solid rock under the deck (refill cave/tunnel hollows).
        for (int by = hNew; by > hNew - bedDepth && by >= worldMinY; by--)
        {
            if (by > worldMaxY) { continue; }
            VoxelType v = ws.GetVoxelWorld(wx, by, wz);
            if (v == VoxelType.Air || v == VoxelType.Water)
            {
                ws.SetVoxelWorld(wx, by, wz, VoxelType.Terrain, VoxelTypeInfo.SharpAxes.Y);
                ws.SetTerrainIdWorld(wx, by, wz, kitId);
            }
        }
        // Surface: flat deck, no detail scatter, road overlay on top.
        if (hNew >= worldMinY && hNew <= worldMaxY)
        {
            ws.SetVoxelWorld(wx, hNew, wz, VoxelType.Terrain, VoxelTypeInfo.SharpAxes.Y);
            ws.SetTerrainIdWorld(wx, hNew, wz, kitId);
            ws.SetDetailGroupWorld(wx, hNew, wz, 0);
            ws.SetDetailStrengthWorld(wx, hNew, wz, 0);
            ws.SetOverlayIdWorld(wx, hNew, wz, overlay);
        }

        hm.Height[wx - hm.WorldMinX, wz - hm.WorldMinZ] = hNew;
        hm.Surface[wx - hm.WorldMinX, wz - hm.WorldMinZ] = hNew;
    }

    // Delete scatter scenery (trees / grass / climbable / berry trees) standing
    // on this tread column. Authored fixtures are never here — their columns are
    // skipped before this is called — but the !PlacedAsFixture guard keeps the
    // rule explicit. Drops the column from the index so later roads don't
    // re-process removed entities.
    private static void RemoveScatterInColumn(WorldState ws, Dictionary<(int, int), List<EntitySimState>> obstacleColumns, int wx, int wz)
    {
        if (obstacleColumns.TryGetValue((wx, wz), out List<EntitySimState> list))
        {
            foreach (EntitySimState e in list)
            {
                if (e.IsRoadObstacle && !e.PlacedAsFixture)
                {
                    ws.RemoveEntity(e);
                }
            }
            obstacleColumns.Remove((wx, wz));
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

    // Load each `.hikescene` and reserve every column its footprint covers, so
    // the content passes leave that ground alone. Loading here (not at stamp
    // time) also means a bad path is reported before the expensive passes
    // rather than after them.
    private static List<(SubsceneState sub, SubscenePlacement placement)> LoadAndReserveSubscenes(WorldGenData genData, HeightMap heightMap)
    {
        var loaded = new List<(SubsceneState, SubscenePlacement)>();
        if (genData.subscenes == null || genData.subscenes.Length == 0)
        {
            return loaded;
        }
        foreach (SubscenePlacement placement in genData.subscenes)
        {
            if (placement == null || string.IsNullOrEmpty(placement.path))
            {
                continue;
            }
            SubsceneState sub;
            try
            {
                sub = SubsceneFile.Read(placement.path);
            }
            catch (Exception e)
            {
                GD.PrintErr($"WorldGen: subscene '{placement.path}' failed to load: {e.Message}");
                continue;
            }
            Vector3I origin = FootprintOrigin(sub, placement);
            for (int dx = 0; dx < sub.Size.X; dx++)
            {
                for (int dz = 0; dz < sub.Size.Z; dz++)
                {
                    heightMap.MarkNoSpawn(origin.X + dx, origin.Z + dz);
                }
            }
            loaded.Add((sub, placement));
        }
        return loaded;
    }

    private static List<(SubsceneState sub, Vector3 anchor)> StampReservedSubscenes(
        WorldState ws, List<(SubsceneState sub, SubscenePlacement placement)> reserved, HeightMap heightMap)
    {
        var stamped = new List<(SubsceneState, Vector3)>();
        foreach ((SubsceneState sub, SubscenePlacement placement) in reserved)
        {
            Vector3I origin = FootprintOrigin(sub, placement);
            int plateauY = FootprintPlateauY(heightMap, origin, sub.Size, out int levelCount);
            // Anchored ON the plateau's top voxel, not above it: a scene carries
            // its own floor, so its bottom layer replaces that voxel instead of
            // stacking a second floor on top of the ground.
            var anchor = new Vector3(placement.anchorXZ.X, plateauY, placement.anchorXZ.Y);
            int entityCount = sub.Entities?.Count ?? 0;
            int evicted = ClearEntitiesInVolume(ws, origin, anchor, sub.Size);
            SubsceneStamper.StampVoxels(ws, sub, anchor);
            stamped.Add((sub, anchor));
            GD.Print($"[WorldGen] stamped subscene {placement.path.GetFile()} at {anchor} (size={sub.Size}, entities={entityCount}, evicted={evicted}, plateau levels under footprint={levelCount})");
        }
        return stamped;
    }

    // Remove anything already standing in the volume the stamp is about to
    // fill. The stamp overwrites those voxels regardless, so an entity left
    // inside ends up embedded in a wall or loose in a room it was never
    // authored into.
    //
    // This is not just a backstop for the footprint reservation — it is the
    // only mechanism that can work for the CAVE pass. No-spawn is a per-column
    // channel, so it can keep surface content off the ground a building covers,
    // but it cannot express "these five voxels of height are taken" without
    // sterilising the whole column down to bedrock. The cave scan is
    // volumetric, so a cave pocket that happens to sit inside the building's
    // band is caught here instead.
    private static int ClearEntitiesInVolume(WorldState ws, Vector3I origin, Vector3 anchor, Vector3I size)
    {
        int minY = Mathf.FloorToInt(anchor.Y);
        var doomed = new List<EntitySimState>();
        foreach (EntitySimState e in ws.AllChunkEntities())
        {
            Vector3 p = e.WorldPosition;
            if (p.X >= origin.X && p.X < origin.X + size.X
                && p.Z >= origin.Z && p.Z < origin.Z + size.Z
                && p.Y >= minY && p.Y < minY + size.Y)
            {
                doomed.Add(e);
            }
        }
        foreach (EntitySimState e in doomed)
        {
            ws.RemoveEntity(e);
        }
        return doomed.Count;
    }

    // World XZ of the footprint's min corner. Y is unused — the reservation is
    // per column and the stamp resolves its own elevation.
    private static Vector3I FootprintOrigin(SubsceneState sub, SubscenePlacement placement)
    {
        return new Vector3I(
            Mathf.FloorToInt(placement.anchorXZ.X - sub.Anchor.X),
            0,
            Mathf.FloorToInt(placement.anchorXZ.Y - sub.Anchor.Z));
    }

    // The plateau level a subscene sits on: the most common Plateau height
    // across its footprint, ties going to the lower one so the building cuts
    // into the higher terrace instead of floating over the lower one (the stamp
    // overwrites its whole bbox, so buried is self-correcting and floating is
    // not). levelCount reports how many distinct levels the footprint spans —
    // anything above 1 means it straddles a terrace edge and wants a nudge.
    //
    // Plateau, NOT Surface: cave carving breaches the ground on ~10% of columns
    // and drops Surface tens of voxels below the terrain beside it, which drags
    // a footprint average down and sinks the building into the intact ground
    // around the hole. Plateau is the authored terrain level; carving never
    // moves it, and ramps don't tilt it.
    private static int FootprintPlateauY(HeightMap heightMap, Vector3I origin, Vector3I size, out int levelCount)
    {
        var counts = new Dictionary<int, int>();
        for (int dx = 0; dx < size.X; dx++)
        {
            for (int dz = 0; dz < size.Z; dz++)
            {
                int plateau = heightMap.GetPlateau(origin.X + dx, origin.Z + dz);
                counts.TryGetValue(plateau, out int seen);
                counts[plateau] = seen + 1;
            }
        }

        levelCount = counts.Count;
        int best = 0;
        int bestCount = -1;
        foreach (KeyValuePair<int, int> level in counts)
        {
            if (level.Value > bestCount || (level.Value == bestCount && level.Key < best))
            {
                best = level.Key;
                bestCount = level.Value;
            }
        }
        return best;
    }

    private static void ApplySubsceneEnvOverrides(WorldState ws, List<(SubsceneState sub, Vector3 anchor)> stamped)
    {
        foreach ((SubsceneState sub, Vector3 anchor) in stamped)
        {
            SubsceneStamper.StampEnvOverrides(ws, sub, anchor);
        }
    }

    // Stamp a chunk into one of the world's zones. Each PlacedZone carries a
    // ZoneBounds saying where it applies; the chunk goes to the highest-Priority
    // bounds whose Contains() is true (ties broken by first in the list).
    // Borders soften through the GetZoneGenWeights blend kernel, and box/circle
    // bounds can wobble their own edge via the context's noise — so a small
    // inset (the swamp village) melts organically into its background zone.
    // Falls back to index 0 when nothing claims the chunk (e.g. an all-quadrant
    // layout with a gap), so every chunk always gets a zone.
    private static byte PickZoneIndex(Vector3I chunkCoord, int zoneCount)
    {
        if (zoneCount <= 0) { return 0; }

        // _activeGenData is set at the top of Generate before any zone pick.
        PlacedZone[] zones = _activeGenData?.zones;
        if (zones == null) { return 0; }

        int best = -1;
        int bestPriority = int.MinValue;
        int n = Math.Min(zoneCount, zones.Length);
        for (int i = 0; i < n; i++)
        {
            ZoneBounds bounds = zones[i]?.bounds;
            if (bounds == null) { continue; }
            if (bounds.priority <= bestPriority) { continue; }
            if (bounds.Contains(chunkCoord.X, chunkCoord.Z, _zoneBoundsContext))
            {
                best = i;
                bestPriority = bounds.priority;
            }
        }
        return best >= 0 ? (byte)best : (byte)0;
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
        if (genData.ZoneGens == null) { return null; }
        for (int i = 0; i < genData.ZoneGens.Length; i++)
        {
            if (genData.ZoneGens[i] != null) { return genData.ZoneGens[i]; }
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
        GetZoneGenWeights(wx, wz, zoneCount, weights, _activeGenData?.zoneGenBlendRadius ?? 2.0f);
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

        // Per-zone monster and forge difficulty bands, kernel-blended so they
        // crossfade across zone borders. ComputeMobLevel / ComputeForgeLevel lerp
        // between each pair by their own (independent) level-noise field.
        public float MobLevelMin;
        public float MobLevelMax;
        // Same, for spawns with a ceiling overhead (caves, tunnels).
        public float UndergroundMobLevelMin;
        public float UndergroundMobLevelMax;
        public float ForgeLevelMin;
        public float ForgeLevelMax;

        // Flatten override (FlattenSurface zones). FlattenWeight is the summed
        // weight of flattening zones at this column (0..1); FlattenLevel is the
        // weight-scaled sum of their FlattenPlateau targets. BuildHeightMap pulls
        // the noisy plateau toward FlattenLevel by FlattenWeight, so the village
        // core sits at a fixed plateau (e.g. 0 = beach) while the edge blends
        // back into the surrounding noisy terrain.
        public float FlattenWeight;
        public float FlattenLevel;
    }

    private static BlendedZoneGen SampleBlendedZoneGen(int wx, int wz, ZoneGenData[] zones)
    {
        var result = new BlendedZoneGen();
        int n = zones != null ? zones.Length : 0;
        if (n == 0) { return result; }

        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        GetZoneGenWeights(wx, wz, n, weights);

        // Per-zone terrain blend reach, keyed off the DOMINANT zone: a zone that
        // authors a tighter TerrainBlendChunks holds its own terrain across its
        // whole footprint instead of letting a neighbour bleed ~ZoneGenBlendRadius
        // chunks in. Asymmetric on purpose — the village stays a flat, dry beach
        // up to its edge while the swamp around it keeps blending softly with the
        // mud/highlands. Only recompute when the dominant zone overrides the
        // global radius, so ordinary columns pay nothing.
        PlacedZone[] placed = _activeGenData?.zones;
        if (placed != null)
        {
            int dom = -1;
            float bestW = 0f;
            for (int i = 0; i < n; i++)
            {
                if (weights[i] > bestW) { bestW = weights[i]; dom = i; }
            }
            float reach = dom >= 0 && dom < placed.Length
                ? (placed[dom]?.bounds?.terrainBlendChunks ?? 0f)
                : 0f;
            if (reach > 0f)
            {
                GetZoneGenWeights(wx, wz, n, weights, reach);
            }
        }

        for (int i = 0; i < n; i++)
        {
            float w = weights[i];
            if (w <= 0f) { continue; }
            ZoneGenData rg = zones[i];
            if (rg == null) { continue; }
            result.Elevation += rg.elevation * w;
            result.ElevationRange += rg.elevationRange * w;
            result.TunnelThreshold += rg.tunnelThreshold * w;
            result.CaveThreshold += rg.caveThreshold * w;
            result.GrassThreshold += rg.grassThreshold * w;
            result.MobLevelMin += rg.mobLevelMin * w;
            result.MobLevelMax += rg.mobLevelMax * w;
            result.UndergroundMobLevelMin += rg.undergroundMobLevelMin * w;
            result.UndergroundMobLevelMax += rg.undergroundMobLevelMax * w;
            result.ForgeLevelMin += rg.forgeLevelMin * w;
            result.ForgeLevelMax += rg.forgeLevelMax * w;
            if (rg.flattenSurface)
            {
                result.FlattenWeight += w;
                result.FlattenLevel += rg.flattenPlateau * w;
            }
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
        return PickWeightedZone(wx, wz, zones, rng, _activeGenData?.zoneGenBlendRadius ?? 2.0f);
    }

    // As above but with an explicit kernel reach — the spawn pass passes the
    // dominant zone's SpawnBlendReachChunks so each zone controls how far its
    // content blends across its own border (a wider reach = a wider, softer
    // mixing band; the caller uses the crisp dominant zone when reach is 0).
    private static int PickWeightedZone(int wx, int wz, ZoneGenData[] zones, Random rng, float blendRadius)
    {
        int n = zones != null ? zones.Length : 0;
        if (n == 0) { return -1; }

        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        GetZoneGenWeights(wx, wz, n, weights, blendRadius);

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

    // Index of the single highest-weight zone at (wx, wz) — the biome a column
    // actually sits in, not a weighted roll. The default for content placement
    // (so nothing bleeds across a border) and the base the per-zone
    // SpawnBlendReachChunks softens. Same blend kernel as the weighted pick, so
    // the boundary follows the same organic seam. Returns -1 if no zone has
    // positive weight.
    private static int DominantZoneIndex(int wx, int wz, ZoneGenData[] zones)
    {
        int n = zones != null ? zones.Length : 0;
        if (n == 0) { return -1; }
        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        GetZoneGenWeights(wx, wz, n, weights);
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
        return best;
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
        int idx = PickWeightedZoneFromHash(wx, wz, zones, HashFloat01(wx, wz, KIT_HASH_SALT), _activeGenData?.kitBlendRadius ?? 2.0f);
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
            int rampSlope = _activeGenData?.rampSlope ?? 1;
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

        // The AUTHORED terrain height: what GenerateChunk fills each column up
        // to, and what Plateau is compared against to decide "flat". Carving
        // does NOT move it — read Surface for where the ground actually is.
        public readonly int[,] Height;

        // The LIVE ground surface: topmost natural terrain voxel in the column.
        // Seeded equal to Height and re-derived by DeriveSurface once carving is
        // done, because a carve can drop a column far below its authored height
        // (GenerateCaves breaches the surface as an open-topped pit on ~10% of
        // columns, worst measured 23 voxels) and every placement pass that
        // anchors to Height would otherwise aim at air. Deliberately ignores
        // architecture — a stamped building does not raise the ground under it,
        // so placement still resolves to the terrain (built ground is kept clear
        // by the separate reservation mask, not by moving the surface).
        public readonly int[,] Surface;

        // Ground an authored builder has claimed — today only subscene
        // footprints (LoadAndReserveSubscenes). Three consumers, and a new
        // builder marking this channel inherits all three: content passes place
        // nothing here, the road pass routes around rather than regrading a
        // building away, and the detail scatter decorates it normally (the
        // stamped ground is real terrain, and its margin should grass over like
        // any other). A channel rather than a per-pass rule so a builder can
        // reserve ground for reasons no geometric test could infer.
        public readonly bool[,] NoSpawn;

        public HeightMap(int worldMinX, int worldMaxX, int worldMinZ, int worldMaxZ,
            int[,] plateau, int[,] height, int[,] surface, bool[,] noSpawn)
        {
            WorldMinX = worldMinX;
            WorldMaxX = worldMaxX;
            WorldMinZ = worldMinZ;
            WorldMaxZ = worldMaxZ;
            Plateau = plateau;
            Height = height;
            Surface = surface;
            NoSpawn = noSpawn;
        }

        public bool IsNoSpawn(int wx, int wz)
        {
            if (wx < WorldMinX || wx > WorldMaxX || wz < WorldMinZ || wz > WorldMaxZ)
            {
                return false;
            }
            return NoSpawn[wx - WorldMinX, wz - WorldMinZ];
        }

        public void MarkNoSpawn(int wx, int wz)
        {
            if (wx < WorldMinX || wx > WorldMaxX || wz < WorldMinZ || wz > WorldMaxZ)
            {
                return;
            }
            NoSpawn[wx - WorldMinX, wz - WorldMinZ] = true;
        }

        // The three column accessors CLAMP to the map's edge rather than
        // throwing. Placement passes legitimately sample a disc around an
        // anchor — a fixture scatter, a subscene footprint — and a
        // site found at the world edge overhangs it, so "the nearest column" is
        // the right answer and an IndexOutOfRangeException that kills the whole
        // generate is not. IsNoSpawn / MarkNoSpawn already guard the same way.
        public int GetHeight(int wx, int wz)
        {
            ClampToMap(ref wx, ref wz);
            return Height[wx - WorldMinX, wz - WorldMinZ];
        }

        public int GetSurface(int wx, int wz)
        {
            ClampToMap(ref wx, ref wz);
            return Surface[wx - WorldMinX, wz - WorldMinZ];
        }

        public int GetPlateau(int wx, int wz)
        {
            ClampToMap(ref wx, ref wz);
            return Plateau[wx - WorldMinX, wz - WorldMinZ];
        }

        private void ClampToMap(ref int wx, ref int wz)
        {
            wx = Math.Clamp(wx, WorldMinX, WorldMaxX);
            wz = Math.Clamp(wz, WorldMinZ, WorldMaxZ);
        }

        public bool IsRamp(int wx, int wz)
        {
            return GetHeight(wx, wz) > GetPlateau(wx, wz);
        }

        // Is this column part of a GRADE (a staircase approximation of a slope)
        // rather than a real discontinuity? Terrain quantizes plateaus to
        // plateauStep voxels, so a genuine plateau edge jumps several voxels at
        // once, while ramps, graded roads and erosion all move at most
        // maxStep per column. That step size — not the apparent angle — is the
        // discriminator: a voxel staircase has no intermediate angles, every
        // adjacent pair is either flat or vertical, so an angle test can't see
        // the slope at all.
        // Tested PER AXIS, not over all four neighbours at once. A ramp climbing
        // the side of a plateau is flanked sideways by the un-ramped plateau, so
        // its cross-slope delta is the full plateau step even though it is
        // unambiguously a grade along its own axis — requiring every neighbour
        // to be gradual hardened the bottom of every such ramp into stairs while
        // leaving the top (where the sideways delta has shrunk to nothing)
        // smooth. An axis qualifies when both its neighbours are within maxStep
        // AND at least one differs: the "differs" clause is what still keeps a
        // plateau edge crisp, since its flat cross-axis is gradual but level.
        public bool IsGrade(int wx, int wz, int maxStep)
        {
            return AxisIsGrade(GetHeight(wx, wz), Delta(wx - 1, wz), Delta(wx + 1, wz), maxStep)
                || AxisIsGrade(GetHeight(wx, wz), Delta(wx, wz - 1), Delta(wx, wz + 1), maxStep);
        }

        // Public so StampGradeShapes can apply the identical rule to the live
        // surface field — the rule must exist in exactly one place.
        public static bool AxisIsGrade(int h, int lo, int hi, int maxStep)
        {
            return Math.Abs(lo - h) <= maxStep
                && Math.Abs(hi - h) <= maxStep
                && (lo != h || hi != h);
        }

        // Neighbour height, clamped into the world so edge columns compare
        // against themselves instead of reading out of bounds.
        private int Delta(int wx, int wz)
        {
            return GetHeight(Math.Clamp(wx, WorldMinX, WorldMaxX), Math.Clamp(wz, WorldMinZ, WorldMaxZ));
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
    // World-space player spawn from the authored playerSpawnPosition. X/Z are
    // voxel coords (clamped into the world); when spawnAtSurface the Y rides 2
    // voxels above the surface top so the player drops cleanly onto terrain once
    // the spawn chunk's collision is ready, matching the WorldMapState bake.
    private static Vector3 ResolveSpawn(WorldGenData genData, HeightMap heightMap)
    {
        Vector3 p = genData.playerSpawnPosition;
        if (!genData.spawnAtSurface)
        {
            return p;
        }
        int wx = Math.Clamp(Mathf.FloorToInt(p.X), heightMap.WorldMinX, heightMap.WorldMaxX);
        int wz = Math.Clamp(Mathf.FloorToInt(p.Z), heightMap.WorldMinZ, heightMap.WorldMaxZ);
        int sy = heightMap.GetSurface(wx, wz);
        return new Vector3(wx + 0.5f, sy + 2f, wz + 0.5f);
    }

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
        int step = Math.Max(1, (int)Math.Round(genData.plateauStep));
        // Ramp anchor band, macro-elevation amplitude, and shoreline falloff
        // are authored on WorldGenData (Terrain Shaping group). `|pathNoise|`
        // below RampAnchorBand marks the core of a ramp zone; the macro noise
        // adds ±MacroElevationRangePlateaus steps; the far east drops to ocean
        // over ShorelineChunks chunks down to OceanDepthPlateaus below zero.
        float rampAnchorBand = genData.rampAnchorBand;
        float macroElevationRangePlateaus = genData.macroElevationRangePlateaus;
        float oceanDepthPlateaus = genData.oceanDepthPlateaus;
        float shorelineFalloffWidth = genData.shorelineChunks * ChunkState.SIZE;

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
                BlendedZoneGen blend = SampleBlendedZoneGen(wx, wz, genData.ZoneGens);

                // Step 2: weighted noise in plateau-step units.
                float terrainN = terrainNoise.GetNoise2D(wx, wz);
                float macroN = elevationNoise.GetNoise2D(wx, wz);
                float plateaus = blend.Elevation
                               + blend.ElevationRange * terrainN
                               + macroN * macroElevationRangePlateaus;

                // Step 2.5: flatten override. Where a FlattenSurface zone has
                // weight, pull the (noisy + macro) plateau toward its fixed
                // FlattenLevel — guaranteeing e.g. the village core lands exactly
                // on the beach plateau regardless of the world-wide macro wave,
                // while the partial-weight edge blends back into the terrain.
                if (blend.FlattenWeight > 0f)
                {
                    plateaus = plateaus * (1f - blend.FlattenWeight) + blend.FlattenLevel;
                }

                // Step 3: plateau-step quantization (round to integer
                // plateau count). Done BEFORE the ocean falloff so cliffs
                // inland snap cleanly while the coast still gets a smooth
                // descent. Elevation = 0 is treated as sea level: the world-y
                // offset by WATER_LEVEL is applied at the end so authored
                // ZoneGenData.Elevation reads naturally — +1 means one
                // plateau step above sea level, -1 means one below.
                int plateauSteps = (int)Mathf.Round(plateaus);

                // Hard floor inside a flatten zone: where it clearly dominates,
                // never let the surface drop below its FlattenPlateau, so the
                // surrounding zone's deep (underwater) columns can't bleed a pond
                // into the village core. The blend already pulls heights toward
                // the target; this just removes residual below-water spikes.
                if (blend.FlattenWeight > 0.5f)
                {
                    int floorLevel = Mathf.RoundToInt(blend.FlattenLevel / blend.FlattenWeight);
                    if (plateauSteps < floorLevel) { plateauSteps = floorLevel; }
                }

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
        int rampRadiusConst = step * genData.rampSlope;
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
        int rampSlope = genData.rampSlope;
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

        // Nothing has been carved yet, so the live surface starts equal to the
        // authored height; DeriveSurface re-derives it after the carving passes.
        var surface = (int[,])height.Clone();
        var noSpawn = new bool[sizeX, sizeZ];
        return new HeightMap(worldMinX, worldMaxX, worldMinZ, worldMaxZ, plateau, height, surface, noSpawn);
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
        int h = heightMap.GetSurface(wx, wz);
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
        int h = heightMap.GetSurface(wx, wz);
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
                if (heightMap.GetSurface(nx, nz) != h)
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
        int step = Math.Max(1, (int)Math.Round(genData.plateauStep));
        int rem = ((wy % step) + step) % step;
        if (rem < step - genData.tunnelLayerHeight)
        {
            return false;
        }
        // Sample at the band's base (rem=0 row) so all voxels in the band
        // share the same noise value — guarantees the band carves all-or-nothing
        // and never leaves sub-3-tall openings. Math.Floor (not C# integer
        // division) so negative wy snaps down, not toward zero.
        int bandBase = (int)Math.Floor((double)wy / step) * step;
        float threshold = SampleBlendedZoneGen(wx, wz, genData.ZoneGens).TunnelThreshold;
        return Mathf.Abs(tunnelNoise.GetNoise3D(wx, bandBase, wz)) < threshold;
    }

    private static void GenerateChunk(ChunkState data, WorldGenData genData,
        FastNoiseLite tunnelNoise, HeightMap heightMap)
    {
        int chunkWorldX = data.ChunkCoord.X * ChunkState.SIZE;
        int chunkWorldY = data.ChunkCoord.Y * ChunkState.SIZE;
        int chunkWorldZ = data.ChunkCoord.Z * ChunkState.SIZE;
        int tunnelStep = Math.Max(1, (int)Math.Round(genData.plateauStep));

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

                // Per-column shape: the topmost solid voxel (the surface) is
                // soft on a grade, snapped on a plateau/cliff edge. All buried
                // voxels stamp Y regardless — softness must not leak downward
                // into caves or other surfaces sharing the column. The mesher's
                // "any soft voxel on Y wins" rule then propagates a grade's
                // softness horizontally into the adjacent plateau column's
                // surface cell, so the ramp base blends into the plateau.
                // Provisional only: StampGradeShapes re-derives the surface tag
                // from the finished geometry at the end of generation, and that
                // pass — not this one — is authoritative.
                byte surfaceShape = (byte)(heightMap.IsGrade(wx, wz, genData.maxGradeStep)
                    ? VoxelTypeInfo.SharpAxes.None
                    : VoxelTypeInfo.SharpAxes.Y);

                // Per-column kit pick + above-water shore band, hoisted out of
                // the y loop because both depend only on (wx, wz). The shore
                // upper bound is a per-column random value in
                // [ShoreElevationMin, ShoreElevationMax] meters above sea
                // level — keeps the shoreline jagged instead of a flat
                // isobar. Columns whose zone has no ShoreKit get an empty
                // band (shoreUpperY = WATER_LEVEL → no voxel falls in it).
                int kitZone = PickKitZone(wx, wz, genData.ZoneGens, data.ZoneIndex);
                ZoneGenData kitZoneData = kitZone >= 0 ? genData.ZoneGens[kitZone] : null;
                byte surfaceTerrainId = TerrainIdOf(kitZoneData?.surfaceKit);
                byte shoreTerrainId = surfaceTerrainId;
                int shoreUpperY = WATER_LEVEL;
                if (kitZoneData != null && kitZoneData.shoreKit != null)
                {
                    shoreTerrainId = TerrainIdOf(kitZoneData.shoreKit);
                    float shoreUpperR = HashFloat01(wx, wz, SHORE_UPPER_HASH_SALT);
                    float shoreUpperMeters = Mathf.Lerp(
                        kitZoneData.shoreElevationMin,
                        kitZoneData.shoreElevationMax,
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

    // One-off per-region landmark fixtures. For each RegionGenData.Fixtures
    // entry, roll a single valid column inside that region's quadrant footprint
    // and place the entry once (signpost, knowledge stone, ...). Replaces the
    // former per-quadrant signpost pass; the per-region text / language /
    // language-component now live on the authored entries.
    private static void PlaceRegionFixtures(WorldState ws, WorldGenData genData, HeightMap heightMap, int worldSeed)
    {
        RegionGenData[] regions = genData.regions ?? System.Array.Empty<RegionGenData>();
        if (regions.Length == 0) { return; }

        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        var context = new SpawnContext
        {
            SurfaceYAt = (wx, wz) => heightMap.GetSurface(wx, wz),
            IsValidColumn = (wx, wz) => IsGrassySurfaceAt(ws, wx, wz, heightMap),
            IsFlatColumn = (wx, wz) => IsFlatTerrainAt(wx, wz, heightMap),
        };

        for (int ri = 0; ri < regions.Length; ri++)
        {
            SpawnListData fixtures = regions[ri]?.fixtures;
            if (fixtures?.entries == null) { continue; }
            int quadrant = Math.Min(ri, 3);
            if (!QuadrantColumnRange(quadrant, worldMinX, worldMaxX, worldMinZ, worldMaxZ,
                    out int xLo, out int xHi, out int zLo, out int zHi))
            {
                continue;
            }
            // Per-region rng so each region's placement is independent and
            // deterministic across runs.
            var rng = new Random(StableMix(DeriveSeed(worldSeed, SEED_SALT_SIGNPOST), ri, 0));
            foreach (SpawnEntryData entry in fixtures.entries)
            {
                if (entry == null) { continue; }
                // Roll a column that already satisfies the entry's flat-terrain
                // requirement so TrySpawn's gate doesn't then reject it.
                bool Valid(int wx, int wz)
                {
                    if (!IsGrassySurfaceAt(ws, wx, wz, heightMap)) { return false; }
                    return !entry.RequireFlatTerrain || IsFlatTerrainAt(wx, wz, heightMap);
                }
                if (!TryRollColumn(rng, genData, xLo, xHi, zLo, zHi, Valid, out int rx, out int rz))
                {
                    continue;
                }
                int sy = heightMap.GetSurface(rx, rz);
                var pos = new Vector3(rx + 0.5f, sy + 1f, rz + 0.5f);
                entry.TrySpawn(ws, pos, rng, context);
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
        int zoneCount = genData.ZoneGens != null ? genData.ZoneGens.Length : 0;
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
                int h = heightMap.GetSurface(wx, wz);
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
            ZoneGenData rg = genData.ZoneGens[i];
            if (rg?.zone?.weather != null)
            {
                humidity = rg.zone.weather.humidity;
            }
            float desiredVolume = humidity * genData.fogVolumePerHumidity * floors.Count;
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
                    int density = (int)Mathf.Clamp(depth * genData.fogDensityPerVoxel, 0f, FOG_MAX_DENSITY);
                    if (density > 0)
                    {
                        ws.SetFogWorld(wx, wy, wz, density);
                    }
                }
            }
        }

    }

    // Rasterize roof sun occlusion so the SkyExposure column scan below sees
    // roofs as cover. Foliage is NOT stamped here: its occluders come from
    // PackedScene.Instantiate, a Node API that can't run on the worldgen worker
    // thread, so canopy is stamped later by FoliageStamper on the main thread.
    // That split is also the correct semantics — a tree canopy shouldn't make a
    // cell an interior, only a real ceiling should.
    private static void StampRoofSunOcclusion(WorldState ws)
    {
        foreach (List<EntitySimState> bucket in ws._entities.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                if (bucket[i] is RoofSimState roof)
                {
                    RoofSunStamper.Stamp(ws, roof);
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
        int step = Math.Max(1, (int)Math.Round(genData.plateauStep));
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

                // No caves under an authored flat clearing (a FlattenSurface
                // zone, e.g. the village). Caves snap their ceiling up to the
                // next plateau step and can breach the surface as an open pit;
                // on a clearing pinned to the water line that pit fills with
                // water, punching ponds into what should be solid dry ground.
                int domZone = DominantZoneIndex(wx, wz, genData.ZoneGens);
                if (domZone >= 0 && genData.ZoneGens[domZone]?.flattenSurface == true)
                {
                    continue;
                }

                // Threshold blends per-column so cave density transitions
                // smoothly across zone borders. Sampled once per column
                // since the kernel is XZ-only.
                float caveThreshold = SampleBlendedZoneGen(wx, wz, genData.ZoneGens).CaveThreshold;

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
                    if (ceilingY - runLo < genData.caveMinHeight)
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

        int step = Math.Max(1, (int)Math.Round(genData.plateauStep));
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
                        int zoneIdx = PickKitZone(wx, wz, genData.ZoneGens, ZoneIndexAtWorld(ws, wx, wy, wz));
                        ws.SetTerrainIdWorld(wx, wy, wz, TerrainIdOf(genData.ZoneGens[zoneIdx]?.caveKit));
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
        int submergedRadius = genData.submergedKitRadius;
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
                int columnZone = PickKitZone(wx, wz, genData.ZoneGens, 0);
                ZoneGenData columnZoneData = columnZone >= 0 ? genData.ZoneGens[columnZone] : null;
                byte shoreTerrainId = 0;
                int shoreLowerY = WATER_LEVEL;
                bool hasShore = columnZoneData != null && columnZoneData.shoreKit != null;
                if (hasShore)
                {
                    shoreTerrainId = TerrainIdOf(columnZoneData.shoreKit);
                    float shoreLowerR = HashFloat01(wx, wz, SHORE_LOWER_HASH_SALT);
                    float shoreLowerMeters = Mathf.Lerp(
                        columnZoneData.shoreSubmergedElevationMin,
                        columnZoneData.shoreSubmergedElevationMax,
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
                            int zoneIdx = PickKitZone(wx, wz, genData.ZoneGens, ZoneIndexAtWorld(ws, wx, wy, wz));
                            ws.SetTerrainIdWorld(wx, wy, wz, TerrainIdOf(genData.ZoneGens[zoneIdx]?.submergedKit));
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

    private static byte ResolveOverlayIndex(StringName blockName)
    {
        BlockData block = BlockCatalog.Active.GetByName(blockName);
        if (block == null)
        {
            GD.PushError($"WorldGen: BlockCatalog has no block named '{blockName}'.");
            return 0;
        }
        return (byte)block.atlasBaseIndex;
    }

    // Edge-overlay scan window / diff band (EdgeScanWindow, EdgeMinDiff,
    // EdgeMaxDiff) and the procedural overlay scatter frequencies / thresholds
    // are authored on WorldGenData. The scatter SEEDS stay fixed here — they're
    // stable RNG salts (like the SEED_SALT_* channels), not feel knobs.
    private const int OVERLAY_DIRT_SEED = 4242;

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

    // Noise-scatter dirt overlays on Surface-kit voxels.
    // Only top-surface voxels (solid with air above) are candidates so buried
    // geometry and cliff faces stay untouched. Kit gate restricts placement
    // to Surface kits — sand (underwater/cave) and cave palette stay clean.
    private static void StampProceduralOverlays(WorldState ws, WorldGenData genData)
    {
        var dirtNoise = new FastNoiseLite();
        dirtNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        dirtNoise.Seed = OVERLAY_DIRT_SEED;
        dirtNoise.Frequency = genData.overlayDirtFrequency;
        dirtNoise.FractalOctaves = 2;

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

                    if (dirtNoise.GetNoise2D(wx, wz) > genData.overlayDirtThreshold)
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
    private static void StampDetailScatter(WorldState ws, WorldGenData genData, HeightMap heightMap)
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

        // Hoisted so the shell test below doesn't allocate a delegate per voxel.
        Func<int, int, int, VoxelType> getVoxel = ws.GetVoxelWorld;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                // A road tread is bare dirt, and this pass runs after it is laid
                // — so the road is kept clear here rather than by clearing the
                // detail channel again after the fact.
                if (_roadColumns.Contains((wx, wz)))
                {
                    continue;
                }
                for (int wy = worldMinY; wy < worldMaxY; wy++)
                {
                    if (!IsSurfaceVoxel(ws, wx, wy, wz))
                    {
                        continue;
                    }
                    // Detail decorates ground, never masonry: IsSurfaceVoxel is
                    // satisfied by the top of a wall, which would run grass and
                    // flowers along the battlements.
                    if (!IsNaturalGround(ws, wx, wy, wz))
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
                    // The water mesh also dilates a voxel laterally into the
                    // shore and skins that shell cell's top face at the
                    // waterline, so its ground is submerged even though the
                    // slot above is air. Shore kits carry real detail (grass on
                    // shore_swamp, pebbles on shore_sand), so without this the
                    // waterline row sprouts sprites standing in the water.
                    if (WaterMesher.IsCoveredShell(getVoxel, wx, wy, wz))
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
                        ? (DominantZoneSurfaceKit(wx, wz, genData.ZoneGens) ?? ResolveKit(voxelTerrainId))
                        : ResolveKit(voxelTerrainId);
                    if (kit == null || kit.defaultDetail == null)
                    {
                        continue;
                    }

                    FastNoiseLite noise = isSurface ? surfaceNoise : subsurfaceNoise;
                    float n = noise.GetNoise2D(wx * kit.detailNoiseFrequency, wz * kit.detailNoiseFrequency);
                    if (n <= kit.detailNoiseThreshold)
                    {
                        continue;
                    }

                    // Map noise (threshold..1) to (strengthMin..255). The kit
                    // owns both the threshold and the floor, so a sandstone-
                    // cave kit can thin its pebble scatter without affecting
                    // a sibling kit in the same zone.
                    float t = (n - kit.detailNoiseThreshold) / Math.Max(0.0001f, 1f - kit.detailNoiseThreshold);
                    int strengthMin = kit.detailStrengthMin;
                    int strength = strengthMin + (int)(t * (255 - strengthMin));
                    strength = Mathf.Clamp(strength, 0, 255);
                    if (strength <= 0)
                    {
                        continue;
                    }

                    ws.SetDetailGroupWorld(wx, wy, wz, DetailIndexOf(kit.defaultDetail));
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
        GetZoneGenWeights(wx, wz, n, weights, _activeGenData?.kitBlendRadius ?? 2.0f);
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
        return best >= 0 ? zones[best]?.surfaceKit : null;
    }

    // Stamp OVERLAY_DIRT on "surface voxels" (solid with air directly above)
    // whose local neighborhood slope is in [EdgeMinDiff, EdgeMaxDiff-1].
    // Per-voxel, not per-column: correctly handles cave floors, overhangs, and
    // ledges because the ±EdgeScanWindow clip keeps each voxel's comparison
    // local to its own walkable layer. Currently unused (see the disabled call
    // in Generate); reads its tuning from the active WorldGenData.
    private static void StampEdgeOverlays(WorldState ws)
    {
        int edgeScanWindow = _activeGenData?.edgeScanWindow ?? 4;
        int edgeMinDiff = _activeGenData?.edgeMinDiff ?? 1;
        int edgeMaxDiff = _activeGenData?.edgeMaxDiff ?? 3;
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
        int SurfaceYAt(int wx, int wz) => heightMap.GetSurface(wx, wz);
        bool IsGrassyAt(int wx, int wz)
        {
            if (!IsFlatDryGrassAt(wx, wz, heightMap))
            {
                return false;
            }
            // Ground an authored builder has claimed — a subscene footprint,
            // reserved up front precisely because the scene is stamped after
            // this pass, so no geometric test here could see it coming.
            if (heightMap.IsNoSpawn(wx, wz))
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
        ZoneGenData[] zonesArr = genData.ZoneGens ?? System.Array.Empty<ZoneGenData>();
        int chunkCenterWx = chunkCoord.X * ChunkState.SIZE + ChunkState.SIZE / 2;
        int chunkCenterWz = chunkCoord.Z * ChunkState.SIZE + ChunkState.SIZE / 2;
        int chunkCenterSy = SurfaceYAt(chunkCenterWx, chunkCenterWz);
        TerrainKitData chunkCenterKit = ResolveKit(ws.GetTerrainIdWorld(chunkCenterWx, chunkCenterSy, chunkCenterWz));
        int treesPerChunkMin = chunkCenterKit?.treesPerChunkMin ?? 0;
        int treesPerChunkMax = chunkCenterKit?.treesPerChunkMax ?? 0;
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
            WeightedScene.Fill(scenePalette, cellKit?.treeScenes);
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
                    float f = forestNoise.GetNoise2D(wx * kit.forestNoiseFrequency, wz * kit.forestNoiseFrequency);
                    if (f < kit.forestThreshold)
                    {
                        continue;
                    }
                    float t = (f - kit.forestThreshold) / Math.Max(0.0001f, 1f - kit.forestThreshold);
                    float density = kit.forestDensity * Mathf.Clamp(t, 0f, 1f);
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

                    float grassThreshold = SampleBlendedZoneGen(wx, wz, genData.ZoneGens).GrassThreshold;
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
                    WeightedScene.Fill(scenePalette, cellKit?.tallGrassScenes);
                    if (scenePalette.Count == 0)
                    {
                        continue;
                    }
                    PackedScene grassScene = scenePalette.Choose(rng);
                    if (grassScene == null)
                    {
                        continue;
                    }
                    float grassJitter = genData.tallGrassJitter;
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
                    int sy = SurfaceYAt(wx, wz);
                    // Anchor at the ground top (top face of the surface voxel),
                    // matching the cave pass below. Every SpawnEntryData sits
                    // with its scene root on this anchor; any per-entity Y
                    // offset is authored inside the scene itself.
                    var pos = new Vector3(wx + 0.5f, sy + 1f, wz + 0.5f);

                    // One spawn list per column, for ALL entity types (no
                    // mob/non-mob distinction). The column's DOMINANT zone owns
                    // it; a zone only softens that hard edge by authoring a
                    // positive SpawnBlendReachChunks on its bounds, which widens
                    // a kernel-weighted roll so its content bleeds that many
                    // chunks across the seam (the old "few desert trees among
                    // forest pines" mixing). Reach 0 (the default) = dominant
                    // only, so a settlement like the village never inherits a
                    // neighbour's wild mobs.
                    int domIdx = DominantZoneIndex(wx, wz, zonesArr);
                    if (domIdx < 0) { continue; }
                    float reach = genData.zones[domIdx]?.bounds?.spawnBlendReachChunks ?? 0f;
                    ZoneGenData spawnZone = zonesArr[domIdx];
                    if (reach > 0f)
                    {
                        int idx = PickWeightedZone(wx, wz, zonesArr, rng, reach);
                        if (idx >= 0) { spawnZone = zonesArr[idx]; }
                    }
                    if (spawnZone?.surfaceEntities?.entries == null) { continue; }
                    // Carry this column's per-chest zone loot to any chest placed
                    // here (a camp-group chest forwards the context to its
                    // sub-entries). Distributed loot is applied in a later pass.
                    surfaceContext.ZonePerChestLoot = spawnZone.perChestLoot;
                    foreach (SpawnEntryData entry in spawnZone.surfaceEntities.entries)
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

        // Water pass: per water-surface column, roll the matching zone's
        // WaterEntities — aquatic mobs that live submerged. Mirrors the surface
        // pass but anchors at the water surface instead of dry ground. Gated on
        // the zone actually authoring a WaterEntities list (most don't), so the
        // full-column water-surface scan stays off the hot path everywhere but
        // the zones that want underwater life.
        if (!skipMobs || !skipInteractives)
        {
            int waterMinY = ws.Min.Y * ChunkState.SIZE;
            int waterMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
            // Topmost water-surface voxel Y in this column (Water with non-Water
            // directly above), or int.MinValue when the column holds no water.
            int WaterSurfaceYAt(int wx, int wz)
            {
                for (int wy = waterMaxY; wy > waterMinY; wy--)
                {
                    if (ws.GetVoxelWorld(wx, wy, wz) == VoxelType.Water
                        && ws.GetVoxelWorld(wx, wy + 1, wz) != VoxelType.Water)
                    {
                        return wy;
                    }
                }
                return int.MinValue;
            }
            // Minimum water-column depth (voxels) a spawned swimmer needs to fit.
            const int MIN_WATER_DEPTH = 2;
            var waterContext = new SpawnContext
            {
                SurfaceYAt = SurfaceYAt,
                IsValidColumn = (wx, wz) => WaterSurfaceYAt(wx, wz) != int.MinValue,
            };
            for (int localX = 0; localX < ChunkState.SIZE; localX++)
            {
                for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
                {
                    int wx = chunkCoord.X * ChunkState.SIZE + localX;
                    int wz = chunkCoord.Z * ChunkState.SIZE + localZ;

                    // Same dominant-zone-with-optional-blend pick as the surface
                    // pass, so water content respects zone borders identically.
                    int domIdx = DominantZoneIndex(wx, wz, zonesArr);
                    if (domIdx < 0) { continue; }
                    float reach = genData.zones[domIdx]?.bounds?.spawnBlendReachChunks ?? 0f;
                    ZoneGenData spawnZone = zonesArr[domIdx];
                    if (reach > 0f)
                    {
                        int idx = PickWeightedZone(wx, wz, zonesArr, rng, reach);
                        if (idx >= 0) { spawnZone = zonesArr[idx]; }
                    }
                    if (spawnZone?.waterEntities?.entries == null) { continue; }

                    int surfaceY = WaterSurfaceYAt(wx, wz);
                    if (surfaceY == int.MinValue) { continue; }
                    // Reject puddles too shallow for a swimmer to occupy.
                    if (ws.GetVoxelWorld(wx, surfaceY - (MIN_WATER_DEPTH - 1), wz) != VoxelType.Water) { continue; }

                    // Anchor inside the top water voxel, NOT on its top face
                    // (surfaceY + 1f) — that boundary floors into the air voxel
                    // above, so the mob's feet sample air and it never reads as
                    // in-water. The feet must sit in water on the first tick so
                    // the mob detects swimming, at which point buoyancy fires and
                    // settles it to its submerged depth. Spawning on the surface
                    // left aquatic mobs out of water: never swimming, never
                    // drifting on the current, and frozen in mid-air (the aquatic
                    // locomotion gate needs water, and the auto-freeze pins a
                    // zero-velocity body before gravity can drop it in).
                    var pos = new Vector3(wx + 0.5f, surfaceY + 0.5f, wz + 0.5f);
                    foreach (SpawnEntryData entry in spawnZone.waterEntities.entries)
                    {
                        if (entry == null) { continue; }
                        bool isMob = entry is MobSpawnEntry;
                        if (isMob ? skipMobs : skipInteractives) { continue; }
                        if (!entry.RollAreaChance(rng)) { continue; }
                        entry.TrySpawn(ws, pos, rng, waterContext);
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
        int HEAD_CLEARANCE = genData.caveHeadClearance;
        int CAVE_CEILING_PROBE = genData.caveCeilingProbe;
        int CAVE_WATER_MIN_DEPTH = genData.caveWaterMinDepth;
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        // Reused across cave cells; its ZoneChestLoot is repointed per cell to the
        // resolved zone so a cave chest picks up that zone's unique drops. The
        // scatter samplers stay null — cave cells are pre-validated, so leaf
        // entries place at the anchor exactly as they did with a null context.
        var caveContext = new SpawnContext();
        for (int localX = 0; localX < ChunkState.SIZE; localX++)
        {
            for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
            {
                int wx = chunkCoord.X * ChunkState.SIZE + localX;
                int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                for (int wy = worldMinY + 1; wy <= worldMaxY - HEAD_CLEARANCE; wy++)
                {
                    // Flooded cave pocket: the top voxel of a water body with rock
                    // overhead. The ceiling probe is what separates a submerged
                    // cave from open lake/ocean surface (only sky above those), so
                    // swimmers stay out of the sea unless a zone's WaterEntities
                    // asks for them there.
                    //
                    // Anchored on the pocket's TOP water voxel, not its floor: the
                    // navigation grid reports a water column's surface as its top
                    // voxel, so a floor anchor fails MobSpawnEntry's walkability
                    // gate on anything deeper than 1 voxel. Mid-voxel (+0.5) so the
                    // mob's feet sample water on its first tick and buoyancy fires
                    // — same reason the open-water pass anchors that way.
                    if (ws.GetVoxelWorld(wx, wy, wz) == VoxelType.Water
                        && ws.GetVoxelWorld(wx, wy + 1, wz) != VoxelType.Water)
                    {
                        if (ws.GetVoxelWorld(wx, wy - (CAVE_WATER_MIN_DEPTH - 1), wz) != VoxelType.Water)
                        {
                            continue;
                        }
                        bool roofed = false;
                        for (int c = 1; c <= CAVE_CEILING_PROBE; c++)
                        {
                            if (VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(wx, wy + c, wz)))
                            {
                                roofed = true;
                                break;
                            }
                        }
                        if (!roofed)
                        {
                            continue;
                        }
                        ZoneGenData waterZone = PickWeightedZoneData(wx, wz, zonesArr, rng);
                        if (waterZone?.caveWaterEntities?.entries == null)
                        {
                            continue;
                        }
                        caveContext.ZonePerChestLoot = waterZone.perChestLoot;
                        var waterPos = new Vector3(wx + 0.5f, wy + 0.5f, wz + 0.5f);
                        foreach (SpawnEntryData entry in waterZone.caveWaterEntities.entries)
                        {
                            if (entry == null) { continue; }
                            bool isWaterMob = entry is MobSpawnEntry;
                            if (isWaterMob ? skipMobs : skipInteractives) { continue; }
                            if (!entry.RollAreaChance(rng)) { continue; }
                            entry.TrySpawn(ws, waterPos, rng, caveContext);
                        }
                        continue;
                    }

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
                    if (rg?.caveEntities?.entries == null)
                    {
                        continue;
                    }
                    caveContext.ZonePerChestLoot = rg.perChestLoot;
                    var pos = new Vector3(wx + 0.5f, wy, wz + 0.5f);
                    foreach (SpawnEntryData entry in rg.caveEntities.entries)
                    {
                        if (entry == null) { continue; }
                        bool isMob = entry is MobSpawnEntry;
                        if (isMob ? skipMobs : skipInteractives) { continue; }
                        if (!entry.RollAreaChance(rng)) { continue; }
                        entry.TrySpawn(ws, pos, rng, caveContext);
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
