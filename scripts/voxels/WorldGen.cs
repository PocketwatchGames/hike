using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

// The approach-agnostic half of world generation. Terrain shape is NOT here:
// each approach implements ITerrainGenerator in its own file under
// scripts/voxels/terrain/, and WorldGenData.terrain picks which one runs. Every
// pass below reads the HeightMap that comes out and nothing else about it.
//
// To ADD a terrain approach, read scripts/data/worldgen/CLAUDE.md — the recipe,
// the HeightMap contract a new approach owes its consumers, and the headless
// verification loop are all there. Nothing in this file should need to change.
public static class WorldGen
{
    // Manual logic-version stamp. Bump when ANY change to this file (or any
    // helper it calls) would alter generated output for the same inputs —
    // tuning a default threshold, changing a noise frequency, reordering a
    // placement pass, etc. WorldGenCache rolls this into its fingerprint so
    // every bump invalidates all cached worlds. WorldGenData .tres edits are
    // detected automatically by content-hashing and don't require a bump.
    public const int WORLDGEN_VERSION = 127;

    // Bitmask flags for the worldgen_skip CVar — see CVars.worldgenSkip.
    // Each category is checked independently inside GenerateProps; setting
    // SKIP_ALL turns the prop pass off entirely.
    public const int SKIP_DETAILS = 1;       // painted detail-sprite scatter
    public const int SKIP_PROPS = 2;         // trees + tall grass
    public const int SKIP_MOBS = 4;          // goblins, kun_kun (surface + cave)
    public const int SKIP_INTERACTIVES = 8;  // loot (surface + cave) + chests (cave)
    public const int SKIP_ALL = SKIP_DETAILS | SKIP_PROPS | SKIP_MOBS | SKIP_INTERACTIVES;

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

    // Path hints contributed by the stamped subscenes of the current run — the
    // same entries registered individually in WorldState.PointsOfInterest, kept
    // here as well because the road pass needs them in two shapes: grouped by
    // placement (so a road can name a PLACEMENT and get its nearest hint) and in
    // registration order (so the spur pass runs in an order that doesn't depend
    // on Dictionary internals — worldgen has to be reproducible). Same
    // static-lifetime rationale as _activeGenData; both reset by
    // RegisterSubscenePathHints.
    private static readonly Dictionary<string, List<PathHint>> _pathHintsByPlacement = new();
    private static readonly List<PathHint> _pathHints = new();

    // One authored path hint, resolved to world space. HintTag selects the tread
    // an auto-linked spur is carved with (WorldGenData.pathHintProfiles);
    // PoiName is what a RoadConnection names.
    private readonly struct PathHint
    {
        public readonly string PoiName;
        public readonly string HintTag;
        public readonly Vector3 Position;
        public readonly bool AutoConnect;

        public PathHint(string poiName, string hintTag, Vector3 position, bool autoConnect)
        {
            PoiName = poiName;
            HintTag = hintTag;
            Position = position;
            AutoConnect = autoConnect;
        }

        public (int, int) Column => (Mathf.FloorToInt(Position.X), Mathf.FloorToInt(Position.Z));
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

    // The waterline AT ONE COLUMN: the global sea, or the inland river / lake
    // surface a terrain approach put there, whichever is higher. Every pass that
    // used to compare against WATER_LEVEL directly goes through this — chunk
    // fill, the shore-kit bands, the dry-land tests and road passability — so
    // inland water above sea level is expressible at all. Approaches that make
    // no inland water leave HeightMap.Water null and this collapses back to the
    // constant.
    public static int WaterYAt(HeightMap heightMap, int wx, int wz)
    {
        return Math.Max(WATER_LEVEL, heightMap.GetWaterY(wx, wz));
    }

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
    // Which of a stamped scene's markers a SubscenePlacement variant fills.
    private const int SEED_SALT_SUBSCENE = 0x18;
    // Tread width rolls for the spurs auto-linking path hints to the network.
    private const int SEED_SALT_PATH_HINT = 0x19;

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
    // A baker supplying its own difficulty field (the world-map painter) answers
    // this itself through the context — see SpawnContext.MobLevelOverride.
    public static int ComputeMobLevel(WorldState ws, Vector3 position, int baseLevel,
        SpawnContext context)
    {
        if (context?.MobLevelOverride != null)
        {
            return context.MobLevelOverride(position, baseLevel);
        }

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
            if (Blocks.IsSolid(ws.GetBlockWorld(wx, wy + dy, wz)))
            {
                return true;
            }
        }
        return false;
    }

    // worldSize is the HORIZONTAL extent in chunks. The vertical one isn't a run
    // parameter — FitVerticalExtent sizes it to the heightmap once terrain is
    // built, so terrain can't outgrow its own world. The extent below is a
    // one-chunk placeholder that only has to be legal until then; nothing reads
    // Y before the fit.
    public static WorldState Generate(WorldGenData genData, int worldSeed, Vector2I worldSize)
    {
        _activeGenData = genData;
        _mobLevelNoise = MakePerlin(DeriveSeed(worldSeed, SEED_SALT_MOBLEVEL), genData.zoneLevelNoiseFrequency, 2);
        _forgeLevelNoise = MakePerlin(DeriveSeed(worldSeed, SEED_SALT_FORGELEVEL), genData.zoneLevelNoiseFrequency, 2);

        var min = new Vector3I(-worldSize.X / 2, 0, -worldSize.Y / 2);
        var max = new Vector3I(min.X + worldSize.X - 1, 0, min.Z + worldSize.Y - 1);
        var ws = new WorldState(min, max, genData.simData,
            KitPalette.Build(genData.kitPalette, genData.ZoneGens));

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

        // The authored TerrainGenData subclass IS the choice of algorithm — it
        // builds the one generator this run drives. Everything below this line
        // is approach-agnostic and reads only the HeightMap that comes out.
        TerrainGenData terrain = TerrainOf(genData);
        ITerrainGenerator terrainGen = terrain.CreateGenerator(genData, worldSeed);

        // Build the integer height field once up front. Chunk and prop
        // generation read from this map instead of re-evaluating noise per
        // voxel — the shape is authored here so geometry is noise-free by
        // construction.
        var heightMap = terrainGen.BuildHeightMap(ws);

        // The terrain field is final here (later passes regrade within it, they
        // don't raise peaks), so the world can now be sized to it.
        FitVerticalExtent(ws, heightMap, terrain);

        // Load the authored subscenes and reserve the ground they will cover,
        // so the content passes between here and the stamp leave it alone —
        // they place ENTITIES, and a voxel stamp writes straight through one,
        // leaving rocks standing in the front room. Loading here (not at stamp
        // time) also reports a bad path before the expensive passes.
        var reservedSubscenes = LoadAndReserveSubscenes(genData, heightMap);

        // The POI registry is built in two passes, authored-exact before rolled:
        // a stamped scene's path hints (front doors, square gates) are places
        // whose position is already decided, and registering them first is what
        // keeps a rolled zone POI from landing on top of one.
        ws.PointsOfInterest.Clear();
        RegisterSubscenePathHints(ws, genData, reservedSubscenes, heightMap);

        // Resolve each authored POI name (ZoneData.PointsOfInterest) to a flat
        // column inside its zone and register it on WorldState. Runs on the bare
        // heightmap (terrain is "constructed" once BuildHeightMap is done); the
        // road pass and the POI-anchored spawn pass both read this registry.
        ResolvePointsOfInterest(ws, genData, heightMap, worldSeed);

        // Landforms the terrain approach placed and named (a mesa, a crater)
        // join the same registry, so roads route to them and POI placements can
        // name them without any of that machinery knowing what a mesa is. An
        // authored name always wins — the approach is the one that can be
        // re-rolled by a seed change.
        RegisterTerrainFeaturePois(ws, terrainGen);

        GenerateChunks(ws, genData, terrainGen, heightMap);

        // Tag the submerged shell as KIT_UNDERWATER. Runs after every chunk
        // (and its water voxels) exist so we can check actual water adjacency
        // instead of "wy <= WATER_LEVEL" — a y-only rule paints buried rock
        // under above-water cliffs as underwater, and the mesher's 27-voxel
        // kit vote then bleeds sand onto cliff faces nowhere near water.
        TagSubmergedKits(ws, genData, heightMap);

        // Volume carving that needs the finished grid rather than one column.
        // Runs after every chunk exists so the approach sees full solid columns
        // and can connect carved runs vertically. Approaches that hollow
        // nothing implement this as a no-op.
        terrainGen.CarveVolumes(ws);

        // Terrain is final here (roads regrade later and update the field
        // themselves), so resolve where the ground actually ended up. Every
        // pass below anchors placements to Surface, not the authored Height.
        DeriveSurface(ws, heightMap);

        // One-off test stamp for underground-water visuals. Carves a wide
        // shallow cavern inland in the mountain zone (toward the desert
        // border) with the ceiling capped at the first plateau above water.
        // Will not survive pure worldgen — remove or gate this call once
        // the underwater shader work lands.
        //GenerateTestUnderwaterLake(ws, heightMap);

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

        // Per-voxel neighborhood slope pass: stamp Dirt on 1-voxel
        // bumps, walkable ramps, and small plateau steps. The shader's
        // per-fragment slope on a box-smoothed normal cannot see features the
        // smoothing averages away; this authored signal puts them back.
        // Currently disabled — the ±EdgeScanWindow / diff-threshold heuristic
        // doesn't map cleanly to the terrain shapes we actually generate, so
        // overlays end up in the wrong places. Revisit once we have a clearer
        // read on which features need the dirt treatment (probably driven by
        // authored tags from the editor rather than derived from geometry).
        // StampEdgeDirt(ws);

        // Scatter dirt patches on Surface-kit voxels. Noise-driven placement is
        // a rough starting point so the authored dirt art shows up in generated
        // worlds; replace with authored tags once the custom editor lands.
        // Runs BEFORE the road pass, which walks columns by Blocks.IsNaturalGround
        // — so Dirt has to be flagged naturalGround or roads mis-grade across a
        // patch.
        StampDirtPatches(ws, genData);

        // Climbable dressing on tall cliff faces. Ahead of moss because moss
        // skips any voxel that already carries an overlay: this way the cliffs
        // are claimed first and moss fills in around them, where reversing the
        // two leaves every tall face bare.
        StampClimbSurfaces(ws, genData,
            (wx, wz) =>
            {
                int zi = PickKitZone(wx, wz, genData.ZoneGens, 0);
                ZoneGenData zone = zi >= 0 ? genData.ZoneGens[zi] : null;
                return zone?.climbCoverage ?? 0f;
            },
            (wx, wz) => WaterYAt(heightMap, wx, wz),
            genData.climbMinCliffHeight, true);

        // Moss overlay over exposed rock/ground. Before the road pass so a road
        // tread stamps over it rather than the reverse.
        StampMossPatches(ws, genData);

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
        var stampedSubscenes = StampReservedSubscenes(ws, reservedSubscenes, heightMap, worldSeed);

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
        StampGradeShapes(ws, heightMap, TerrainOf(genData).maxGradeStep);

        // AFTER every ground-moving pass: the scatter writes per-voxel channels
        // that a later road regrade or subscene stamp would overwrite wholesale,
        // which is what used to leave a stamped building's terrain margin bald.
        // Roads suppress their own detail here rather than clearing it after.
        if ((skipFlags & SKIP_DETAILS) == 0)
        {
            StampDetailScatter(ws, genData, (wx, wz) => _roadColumns.Contains((wx, wz)), true);
        }

        // Player spawn point, resolved after road grading so a road crossing the
        // spawn column lands the player on the regraded surface. With
        // spawnAtSurface the authored Y is replaced by the ground surface at
        // (X, Z); otherwise the explicit Y is used verbatim.
        ws.Spawn = ResolveSpawn(genData, heightMap);

        // The party's home fire, resolved by proximity to the spawn rather than
        // authored on whichever entry happened to place it — the campfire can
        // come from a fixture group or from a stamped subscene, and the world
        // editor can only ever author one unlit (a lit one would claim the
        // world's single lit fire at load).
        LightSpawnCampfire(ws, genData);

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

        // Fill every chunk's water-current subgrid: an ambient drift everywhere,
        // then the terrain approach's own river flow stamped over the columns
        // that carry it. Worldgen-only — disk-loaded chunks use their serialized
        // bytes and never reach here.
        GenerateAmbientWaterCurrents(ws);
        StampRiverCurrents(ws, heightMap);
        PlaceWaterfalls(ws, heightMap.Waterfalls);

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
        _lastPlateauStep = heightMap.LevelStep;
        _lastTerrainGen = terrainGen;
        DumpStandingWater(ws, heightMap);
        return ws;
    }

    // TEMPORARY DIAGNOSTIC — find every tall vertical run of water in the
    // finished world. Deliberately independent of HeightMap.Waterfalls: the
    // sheet test only flags a column where the scratch surface ended up ABOVE
    // the real water field, so a column that actually stands water is excluded
    // from that list by construction and cannot be found through it.
    private static void DumpStandingWater(WorldState ws, HeightMap heightMap)
    {
        const int REPORT_RUN = 4;    // runs at least this tall are interesting
        const int TOP_N = 8;
        const int STRIP = 4;

        var histogram = new Dictionary<int, int>();
        var tallest = new List<(int run, int wx, int topY, int wz)>();

        // Min/Max are CHUNK coordinates, not voxels — scale before walking.
        int minX = ws.Min.X * ChunkState.SIZE;
        int maxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int minY = ws.Min.Y * ChunkState.SIZE;
        int maxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int minZ = ws.Min.Z * ChunkState.SIZE;
        int maxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;
        GD.Print($"[StandingWater] scanning voxels x[{minX}..{maxX}] y[{minY}..{maxY}] z[{minZ}..{maxZ}]");

        for (int wx = minX; wx <= maxX; wx++)
        {
            for (int wz = minZ; wz <= maxZ; wz++)
            {
                int run = 0;
                int runTop = 0;
                for (int wy = maxY; wy >= minY; wy--)
                {
                    if (ws.GetBlockWorld(wx, wy, wz) == Blocks.WaterId)
                    {
                        if (run == 0) { runTop = wy; }
                        run++;
                        continue;
                    }
                    if (run >= REPORT_RUN)
                    {
                        histogram[run] = histogram.GetValueOrDefault(run) + 1;
                        tallest.Add((run, wx, runTop, wz));
                    }
                    run = 0;
                }
                if (run >= REPORT_RUN)
                {
                    histogram[run] = histogram.GetValueOrDefault(run) + 1;
                    tallest.Add((run, wx, runTop, wz));
                }
            }
        }

        var buckets = new List<string>();
        foreach (int depth in histogram.Keys.OrderBy(k => k))
        {
            buckets.Add($"{depth}v x{histogram[depth]}");
        }
        GD.Print($"[StandingWater] {tallest.Count} runs >= {REPORT_RUN}v: {string.Join(", ", buckets)}");

        tallest.Sort((a, b) => b.run.CompareTo(a.run));
        for (int i = 0; i < Math.Min(TOP_N, tallest.Count); i++)
        {
            (int run, int wx, int topY, int wz) = tallest[i];
            var sb = new System.Text.StringBuilder();
            sb.Append($"[StandingWater] {run}v run at ({wx}, {topY}, {wz})"
                + $" heightmap h={heightMap.GetHeight(wx, wz)}"
                + $" water={heightMap.GetWaterY(wx, wz)}\n");
            for (int wy = topY + 2; wy >= topY - run - 2; wy--)
            {
                sb.Append($"  y{wy,4} ");
                for (int d = -STRIP; d <= STRIP; d++)
                {
                    int v = ws.GetBlockWorld(wx + d, wy, wz);
                    sb.Append(v == Blocks.WaterId ? 'W' : Blocks.IsSolid(v) ? '#' : '.');
                }
                sb.Append('\n');
            }
            GD.Print(sb.ToString());
        }
    }

    // Turn measured cascades into entities — where a drop stops being a hole in
    // the water field and becomes something you can see and hear. Takes the
    // SITES rather than the HeightMap: the world-map painter measures its own
    // (WorldMapState.BuildWaterfallSites, off the painted water layer) and files
    // them through here, so the surface-Y convention below has one home.
    //
    // The entity is filed at the LIP rather than at the landing: that is where
    // the fall reads from above, and it keeps a cascade in the same chunk as the
    // river that feeds it. A tall one still spans several chunks vertically, so
    // its sheet is drawn while the lip's chunk is resident and not otherwise —
    // acceptable while the load radius is generous, and the same bargain roofs
    // and other tall entities already make.
    public static void PlaceWaterfalls(WorldState ws, IReadOnlyList<WaterfallSite> sites)
    {
        int placed = 0;
        foreach (WaterfallSite site in sites)
        {
            if (site.Lips.Count == 0) { continue; }
            var lips = new WaterfallLip[site.Lips.Count];
            for (int i = 0; i < lips.Length; i++)
            {
                lips[i] = site.Lips[i];
            }
            // Both Y values name a water SURFACE, and a surface sits one voxel
            // above the topmost voxel it caps — the site records voxels.
            ws.AddEntity(new WaterfallSimState(site.Top, site.Top.Y + 1f, site.BottomY + 1f, lips));
            placed++;
        }
        if (placed > 0)
        {
            GD.Print($"[WorldGen] placed {placed} waterfalls");
        }
    }

    // TEMPORARY DIAGNOSTIC — dumps the finished voxels through each cascade so a
    // standing column can be told from two separated pools. Remove once the
    // waterfall work is done.
    private static void DumpWaterfallColumns(WorldState ws, HeightMap heightMap)
    {
        const int STRIP = 4;   // columns either side of the site
        const int ABOVE = 2;   // rows above the lip
        const int BELOW = 3;   // rows below the landing

        foreach (WaterfallSite site in heightMap.Waterfalls)
        {
            int sx = Mathf.RoundToInt(site.Top.X - 0.5f);
            int sz = Mathf.RoundToInt(site.Top.Z - 0.5f);
            int topY = Mathf.RoundToInt(site.Top.Y);
            var sb = new System.Text.StringBuilder();
            sb.Append($"[FallDump] site ({sx}, {topY}, {sz}) {site.Height}v/{site.Columns}col\n");

            // Per-column heightmap state, so a voxel stack can be attributed to
            // the water field rather than to the chunk fill.
            for (int d = -STRIP; d <= STRIP; d++)
            {
                int wx = sx + d;
                int h = heightMap.GetHeight(wx, sz);
                int w = heightMap.GetWaterY(wx, sz);
                sb.Append($"  x{wx,5} h={h,4} water={(w == HeightMap.NoWater ? "none" : w.ToString()),5}"
                    + $" stack={(w == HeightMap.NoWater ? 0 : Math.Max(0, w - h)),3}\n");
            }

            // The voxels themselves, along X through the site. W=water, #=solid,
            // .=air. A cascade that still stands reads as an unbroken W column.
            for (int wy = topY + ABOVE; wy >= topY - site.Height - BELOW; wy--)
            {
                sb.Append($"  y{wy,4} ");
                for (int d = -STRIP; d <= STRIP; d++)
                {
                    int v = ws.GetBlockWorld(sx + d, wy, sz);
                    sb.Append(v == Blocks.WaterId ? 'W' : Blocks.IsSolid(v) ? '#' : '.');
                }
                sb.Append('\n');
            }
            GD.Print(sb.ToString());
        }
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
    // Noise channels shared by the APPROACH-AGNOSTIC passes. Every terrain
    // channel (height, ramps, tunnels, caves) now belongs to the ITerrainGenerator
    // that uses it, so adding an approach cannot widen this struct.
    private readonly struct WorldNoise
    {
        public readonly FastNoiseLite Grass;
        public readonly FastNoiseLite Forest;

        public WorldNoise(FastNoiseLite grass, FastNoiseLite forest)
        {
            Grass = grass;
            Forest = forest;
        }
    }

    public static FastNoiseLite MakePerlin(int seed, float frequency, int octaves)
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
        // Forest noise keeps base frequency 1; per-kit frequency is applied at
        // sample time by scaling input coords, so two kits in a zone can read
        // different patterns.
        return new WorldNoise(
            grass: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_GRASS), genData.grassNoiseFrequency, genData.grassNoiseOctaves),
            forest: MakePerlin(DeriveSeed(worldSeed, SEED_SALT_FOREST), 1f, genData.forestNoiseOctaves));
    }

    // Build the per-world ZoneState array from the authored zone templates. The
    // ZoneData embedded in each ZoneGenData is what ZoneState carries forward —
    // the per-zone worldgen scalars on ZoneGenData stay in `genData` and blend
    // per-position during the passes. WindDirection is randomized in the XZ
    // plane so each world has its own prevailing wind per zone; Elevation
    // defaults to 0 until the editor authors it per-zone per-world.
    // The world's terrain approach. Authoring a WorldGenData without one is a
    // content error, not a supported configuration — but worldgen runs at boot
    // and a hard failure there costs the whole session, so this reports loudly
    // and falls back to the plateau approach's defaults rather than throwing.
    private static PlateauTerrainData _fallbackTerrain;
    public static TerrainGenData TerrainOf(WorldGenData genData)
    {
        if (genData?.terrain != null)
        {
            return genData.terrain;
        }
        if (_fallbackTerrain == null)
        {
            GD.PushError("[WorldGen] WorldGenData has no terrain resource — falling back to"
                + " plateau defaults. Assign a TerrainGenData subclass to WorldGenData.terrain.");
            _fallbackTerrain = new PlateauTerrainData();
        }
        return _fallbackTerrain;
    }

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

    // Size the world's vertical extent to the terrain that was just built, so
    // world height is a consequence of terrain shape rather than a run
    // parameter the terrain can outgrow. Runs before any chunk exists.
    //
    // Two guarantees, both of which the lighting depends on:
    //   * skyHeadroomVoxels of air above the highest column. Sunlight is a
    //     top-down column scan that breaks on the first solid voxel, so a peak
    //     that reaches the top voxel lights nothing; and every light_map sample
    //     above the world top wraps toroidally into the underground band, so
    //     models and particles up there go black.
    //   * undergroundDepthVoxels of rock below the lowest column, for the
    //     carving passes to work in.
    //
    // Columns above maxSurfaceHeightVoxels are flattened first: every chunk in
    // the fitted box is allocated whether or not it holds anything, so an
    // unbounded peak would be paid for by the whole XZ footprint.
    private static void FitVerticalExtent(WorldState ws, HeightMap heightMap, TerrainGenData terrain)
    {
        int ceiling = Math.Max(1, terrain.maxSurfaceHeightVoxels);
        int sizeX = heightMap.Height.GetLength(0);
        int sizeZ = heightMap.Height.GetLength(1);
        int lowest = int.MaxValue;
        int highest = int.MinValue;
        int clampedColumns = 0;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                if (heightMap.Height[lx, lz] > ceiling)
                {
                    heightMap.Height[lx, lz] = ceiling;
                    // Plateau and Surface mirror or trail Height; clamping them
                    // to the same lid keeps "flat column" (Height == Plateau)
                    // reading true across the flattened top.
                    heightMap.Plateau[lx, lz] = Math.Min(heightMap.Plateau[lx, lz], ceiling);
                    heightMap.Surface[lx, lz] = Math.Min(heightMap.Surface[lx, lz], ceiling);
                    clampedColumns++;
                }
                int h = heightMap.Height[lx, lz];
                if (h < lowest) { lowest = h; }
                if (h > highest) { highest = h; }
            }
        }

        int minVoxelY = lowest - Math.Max(0, terrain.undergroundDepthVoxels);
        int maxVoxelY = highest + Math.Max(1, terrain.skyHeadroomVoxels);
        int minChunkY = FloorDiv(minVoxelY, ChunkState.SIZE);
        int maxChunkY = FloorDiv(maxVoxelY, ChunkState.SIZE);
        ws.SetVerticalChunkExtent(minChunkY, maxChunkY);

        int chunksTall = maxChunkY - minChunkY + 1;
        GD.Print($"[WorldGen] Vertical extent fitted: terrain y {lowest}..{highest}"
            + $" -> chunks Y {minChunkY}..{maxChunkY} ({chunksTall} tall,"
            + $" voxels y {minChunkY * ChunkState.SIZE}..{(maxChunkY + 1) * ChunkState.SIZE - 1})");
        if (clampedColumns > 0)
        {
            GD.PushWarning($"[WorldGen] {clampedColumns} column(s) flattened at the"
                + $" maxSurfaceHeightVoxels ceiling ({ceiling}) — lower the zone's"
                + " elevationRange or raise the ceiling (costs world memory).");
        }
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
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
    private static void GenerateChunks(WorldState ws, WorldGenData genData, ITerrainGenerator terrainGen, HeightMap heightMap)
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
                    GenerateChunk(chunk, genData, ws.Kits, terrainGen, heightMap);
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
        // Reserved ground is off limits unless the scene that claimed it opened
        // it to fixtures — a village square wants the villagers standing in it.
        if (heightMap.IsNoSpawn(wx, wz) && !heightMap.IsFixtureGround(wx, wz)) { return false; }
        int sy = heightMap.GetSurface(wx, wz);
        int ground = ws.GetBlockWorld(wx, sy, wz);
        if (ground == Blocks.AirId || ground == Blocks.WaterId) { return false; }
        return ws.GetBlockWorld(wx, sy + 1, wz) == Blocks.AirId;
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
        // Deliberately does NOT clear the registry — Generate clears it once and
        // registers the subscene path hints ahead of this pass, whose spacing
        // test then keeps a rolled POI away from an authored one.
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

    // Register the terrain approach's own named landforms in the POI registry.
    // Runs after the authored names so one of those always wins a collision:
    // an authored POI is a fixed part of the world's design, while a landform
    // name is re-rolled whenever the seed or the terrain tuning changes.
    private static void RegisterTerrainFeaturePois(WorldState ws, ITerrainGenerator terrainGen)
    {
        System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, Vector3>>
            features = terrainGen.GetNamedFeatures();
        if (features == null || features.Count == 0) { return; }
        int added = 0;
        foreach (System.Collections.Generic.KeyValuePair<string, Vector3> f in features)
        {
            if (string.IsNullOrEmpty(f.Key) || ws.PointsOfInterest.ContainsKey(f.Key)) { continue; }
            ws.PointsOfInterest[f.Key] = f.Value;
            added++;
        }
        GD.Print($"[WorldGen] registered {added} terrain landform(s) as points of interest.");
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
        if (roads.Length == 0 && _pathHintsByPlacement.Count == 0) { return; }

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

        // POI names an authored road already runs to, so the auto-link pass
        // doesn't spur a second path at the same door.
        var connectedHints = new HashSet<string>();

        var touchedHints = new List<string>();
        int ci = 0;
        foreach (RoadConnection conn in roads)
        {
            int connIndex = ci++;
            if (conn == null) { continue; }
            touchedHints.Clear();
            if (!TryResolveRoadEndpoints(ws, conn, touchedHints, out Vector3 a, out Vector3 b))
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
            List<(int, int)> path = FindRoadRoute(heightMap, genData, start, goal, null, obstacleColumns, maxWidth);
            if (path == null || path.Count == 0)
            {
                GD.PushWarning($"WorldGen: no road route found '{conn.fromPoi}' → '{conn.toPoi}'.");
                continue;
            }

            BlockSurfaceData tex = conn.texture ?? genData.roadDefaultTexture;
            if (tex == null)
            {
                GD.PushWarning("WorldGen: no road texture authored (WorldGenData.roadDefaultTexture); the road will show its kit block untreaded.");
            }
            byte overlay = tex != null ? (byte)tex.atlasBaseIndex : OVERLAY_NONE;
            var widthRng = new Random(StableMix(DeriveSeed(worldSeed, SEED_SALT_ROAD), connIndex, 0));
            GradeCarvePaintRoad(ws, genData, heightMap, path, minWidth, maxWidth, overlay, widthRng, obstacleColumns, protectedColumns);
            // Only now: a connection that failed to route left its door as
            // unserved as one that was never authored, and the spur pass is
            // exactly what should still reach it.
            foreach (string name in touchedHints)
            {
                connectedHints.Add(name);
            }
        }

        ConnectPathHints(ws, genData, heightMap, worldSeed, connectedHints, obstacleColumns, protectedColumns);
    }

    // Resolve a road's two endpoint names to positions. A name is normally a
    // point of interest; it may also be a stamped PLACEMENT that carries path
    // hints, in which case the road ends at whichever of its hints lies nearest
    // the other end — so "crossroads → town_square" enters the square by the
    // gate on the side the road actually arrives from, with nothing authored
    // per gate. Names the road ends at are appended to `touchedHints` for the
    // caller to commit once the route actually carves.
    private static bool TryResolveRoadEndpoints(WorldState ws, RoadConnection conn,
        List<string> touchedHints, out Vector3 a, out Vector3 b)
    {
        a = Vector3.Zero;
        b = Vector3.Zero;
        string fromName = conn.fromPoi ?? "";
        string toName = conn.toPoi ?? "";

        bool fromIsPoi = ws.PointsOfInterest.TryGetValue(fromName, out a);
        bool toIsPoi = ws.PointsOfInterest.TryGetValue(toName, out b);
        _pathHintsByPlacement.TryGetValue(fromName, out List<PathHint> fromHints);
        _pathHintsByPlacement.TryGetValue(toName, out List<PathHint> toHints);

        // A name that resolves BOTH ways (a placement whose only hint is
        // untagged registers under the bare placement name) is already the one
        // position either reading would give, so the POI lookup wins.
        if (fromIsPoi) { fromHints = null; }
        if (toIsPoi) { toHints = null; }

        if (!fromIsPoi && fromHints == null) { return false; }
        if (!toIsPoi && toHints == null) { return false; }

        if (fromHints != null && toHints != null)
        {
            // Both ends are gated scenes: take the closest pair of gates, which
            // is the shortest link between the two and the one that faces.
            float best = float.MaxValue;
            PathHint bestFrom = default, bestTo = default;
            foreach (PathHint f in fromHints)
            {
                foreach (PathHint t in toHints)
                {
                    float d = f.Position.DistanceSquaredTo(t.Position);
                    if (d < best)
                    {
                        best = d;
                        bestFrom = f;
                        bestTo = t;
                    }
                }
            }
            a = bestFrom.Position;
            b = bestTo.Position;
            touchedHints.Add(bestFrom.PoiName);
            touchedHints.Add(bestTo.PoiName);
            return true;
        }
        if (fromHints != null)
        {
            PathHint hint = NearestHint(fromHints, b);
            a = hint.Position;
            touchedHints.Add(hint.PoiName);
        }
        if (toHints != null)
        {
            PathHint hint = NearestHint(toHints, a);
            b = hint.Position;
            touchedHints.Add(hint.PoiName);
        }
        // A road authored straight at a hint's own POI name counts as reaching
        // it, so the auto-link pass leaves that door alone.
        if (fromIsPoi) { touchedHints.Add(fromName); }
        if (toIsPoi) { touchedHints.Add(toName); }
        return true;
    }

    private static PathHint NearestHint(List<PathHint> hints, Vector3 towards)
    {
        PathHint best = hints[0];
        float bestDist = best.Position.DistanceSquaredTo(towards);
        for (int i = 1; i < hints.Count; i++)
        {
            float d = hints[i].Position.DistanceSquaredTo(towards);
            if (d < bestDist)
            {
                bestDist = d;
                best = hints[i];
            }
        }
        return best;
    }

    // Spur a path from every opted-in path hint no authored road already
    // reaches (SubscenePlacement.connectPathHints) to the nearest point of the
    // network laid so far — the authored roads first, then each spur as it is
    // carved, so a village of doors chains onto the road rather than onto each
    // other in isolation. The tread comes from the hint's tag
    // (WorldGenData.pathHintProfiles), which is what makes a door a footpath and
    // a gate a road.
    private static void ConnectPathHints(WorldState ws, WorldGenData genData, HeightMap heightMap,
        int worldSeed, HashSet<string> connectedHints,
        Dictionary<(int, int), List<EntitySimState>> obstacleColumns,
        HashSet<(int, int)> protectedColumns)
    {
        if (_pathHintsByPlacement.Count == 0) { return; }

        // Goal set for the spur search. Starts as the authored road tread and
        // grows with each spur — including the hint columns themselves, which
        // the tread never stamps (they sit on reserved ground), so a second door
        // can still join a path that ended at the first.
        var network = new HashSet<(int, int)>(_roadColumns);
        foreach (PathHint hint in _pathHints)
        {
            if (connectedHints.Contains(hint.PoiName))
            {
                network.Add(hint.Column);
            }
        }

        float maxSpur = Math.Max(1f, genData.pathHintMaxSpurMeters);
        for (int hi = 0; hi < _pathHints.Count; hi++)
        {
            PathHint hint = _pathHints[hi];
            if (!hint.AutoConnect || connectedHints.Contains(hint.PoiName))
            {
                continue;
            }
            if (network.Count == 0)
            {
                GD.PushWarning($"WorldGen: path hint '{hint.PoiName}' has no road network to link to — author a RoadConnection that reaches this village first.");
                continue;
            }

            PathHintProfile profile = PathProfileFor(genData, hint.HintTag);
            int minWidth = Math.Max(1, Math.Min(profile?.minWidth ?? 2, profile?.maxWidth ?? 3));
            int maxWidth = Math.Max(minWidth, profile?.maxWidth ?? 3);

            List<(int, int)> path = FindRoadRoute(heightMap, genData, hint.Column,
                null, network, obstacleColumns, maxWidth);
            if (path == null || path.Count == 0)
            {
                GD.PushWarning($"WorldGen: no route from path hint '{hint.PoiName}' to the road network.");
                continue;
            }
            if (path.Count - 1 > maxSpur)
            {
                GD.PushWarning($"WorldGen: path hint '{hint.PoiName}' is {path.Count - 1} columns from the nearest road (limit {maxSpur}); left unconnected.");
                continue;
            }

            BlockSurfaceData tex = profile?.texture ?? genData.roadDefaultTexture;
            if (tex == null)
            {
                GD.PushWarning("WorldGen: no road texture authored (WorldGenData.roadDefaultTexture); the road will show its kit block untreaded.");
            }
            byte overlay = tex != null ? (byte)tex.atlasBaseIndex : OVERLAY_NONE;
            var widthRng = new Random(StableMix(DeriveSeed(worldSeed, SEED_SALT_PATH_HINT), hi, 0));
            GradeCarvePaintRoad(ws, genData, heightMap, path, minWidth, maxWidth, overlay, widthRng,
                obstacleColumns, protectedColumns);
            foreach ((int, int) column in path)
            {
                network.Add(column);
            }
        }
    }

    // The tread an auto-linked spur off `hintTag` is carved with. An entry with
    // an empty tag is the fallback; null means "no profiles authored", and the
    // caller falls back to a narrow RoadDefaultTexture track.
    private static PathHintProfile PathProfileFor(WorldGenData genData, string hintTag)
    {
        PathHintProfile[] profiles = genData.pathHintProfiles ?? System.Array.Empty<PathHintProfile>();
        PathHintProfile fallback = null;
        foreach (PathHintProfile profile in profiles)
        {
            if (profile == null) { continue; }
            if (profile.hintTag == hintTag) { return profile; }
            if (string.IsNullOrEmpty(profile.hintTag)) { fallback ??= profile; }
        }
        return fallback;
    }

    // 8-connected A* over world columns between two POI columns. Cost favours
    // flat / gently sloped ground, penalizes climbing faster than
    // RoadMaxWalkableStep (× RoadCliffCostMultiplier, scaled by the excess),
    // adds cost for obstacle columns (scatter scenery AND authored fixtures) in
    // the R×R window around each step (R = road width) so roads thread through
    // open ground and around fixtures, and discounts columns already laid by
    // earlier roads (× RoadReuseCostMultiplier) so roads merge. Wet columns are
    // impassable, EXCEPT inland water no deeper than RoadFordMaxDepth, which
    // costs × RoadFordCostMultiplier — see that field for why a river has to be
    // crossable at all. World is small (a few hundred columns per side) so a
    // plain A* is ample.
    //
    // Two goal shapes. `goal` is a single column (an authored road between two
    // POIs) and gets the octile heuristic. `goalColumns` is a whole SET — the
    // road network as it stands — and the search degrades to a Dijkstra that
    // stops at the first column of it reached, which is how a path hint finds
    // its nearest road without knowing which road that is. Pass exactly one.
    private static List<(int, int)> FindRoadRoute(HeightMap hm, WorldGenData genData,
        (int x, int z) start, (int x, int z)? goal, HashSet<(int, int)> goalColumns,
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
        // below the column's waterline) are blocked. The threshold is >= that
        // line, NOT > : a column whose top solid voxel sits exactly at it is dry
        // SHORELINE — its walkable top face is one voxel above the water —
        // matching IsFlatDryGrassAt. Using > was an off-by-one that made
        // shoreline impassable, which is fatal in a swamp sitting at elevation 0
        // where the POIs themselves land on shoreline and every route then
        // failed.
        int fordDepth = Math.Max(0, genData.roadFordMaxDepth);
        // Depth of the water over a column, or 0 for dry ground; -1 for
        // impassable (too deep, or sea, which is never forded however shallow).
        int WaterDepth(int x, int z)
        {
            int surface = hm.GetSurface(x, z);
            int inland = hm.GetWaterY(x, z);
            int waterY = Math.Max(WATER_LEVEL, inland);
            if (surface >= waterY) { return 0; }
            if (inland == HeightMap.NoWater || fordDepth <= 0) { return -1; }
            int d = waterY - surface;
            return d <= fordDepth ? d : -1;
        }
        bool Passable(int x, int z)
        {
            if (x < minX || x > maxX || z < minZ || z > maxZ) { return false; }
            // Subscenes are stamped before this pass, so a route through one is
            // a route through a building — the tread would regrade its floor
            // out from under it. Blocked outright rather than priced: a footprint
            // is a few dozen columns in an open world, so there is always a way
            // around, and a merely expensive one still gets taken when the POIs
            // line up with it.
            // …except through a path hint's portal: a front door sits inside its
            // own scene's reserved ground, so the route has to be let in far
            // enough to reach it. The TREAD still skips reserved columns, so
            // nothing is regraded behind the doorway.
            if (hm.IsNoSpawn(x, z) && !hm.IsRoadPortal(x, z)) { return false; }
            return WaterDepth(x, z) >= 0;
        }
        if (!Passable(start.x, start.z)) { return null; }
        if (goal.HasValue && !Passable(goal.Value.x, goal.Value.z)) { return null; }

        var gScore = new float[sizeX * sizeZ];
        Array.Fill(gScore, float.PositiveInfinity);
        var cameFrom = new int[sizeX * sizeZ];
        Array.Fill(cameFrom, -1);
        var closed = new bool[sizeX * sizeZ];
        var open = new PriorityQueue<int, float>();

        int startIdx = Idx(start.x, start.z);
        gScore[startIdx] = 0f;

        // Scale the octile-distance heuristic by the cheapest possible per-step
        // cost so it never overestimates. The reuse discount makes an on-road
        // step cost reuseMult (< 1); an octile heuristic weighted at 1.0 would
        // overestimate the remaining cost of any road-following route, making A*
        // non-optimal and returning a near-straight path that runs PARALLEL to an
        // existing road instead of merging onto it. Weighting by reuseMult keeps
        // it admissible so merges win.
        // A set goal has no single point to aim at, so the heuristic is 0 there
        // and the search is a plain Dijkstra.
        float hWeight = Math.Min(1f, reuseMult);
        float Heuristic(int x, int z)
        {
            if (!goal.HasValue) { return 0f; }
            int dx = Math.Abs(x - goal.Value.x);
            int dz = Math.Abs(z - goal.Value.z);
            int diag = Math.Min(dx, dz);
            return ((dx + dz) - (2f - 1.41421356f) * diag) * hWeight;
        }
        bool IsGoal(int idx, int x, int z)
        {
            return goal.HasValue ? idx == Idx(goal.Value.x, goal.Value.z) : goalColumns.Contains((x, z));
        }

        open.Enqueue(startIdx, Heuristic(start.x, start.z));

        Span<int> dirsX = stackalloc int[] { 1, -1, 0, 0, 1, 1, -1, -1 };
        Span<int> dirsZ = stackalloc int[] { 0, 0, 1, -1, 1, -1, 1, -1 };

        int foundIdx = -1;
        while (open.TryDequeue(out int current, out float _))
        {
            if (closed[current]) { continue; }
            closed[current] = true;

            int cx = current / sizeZ + minX;
            int cz = current % sizeZ + minZ;
            if (IsGoal(current, cx, cz)) { foundIdx = current; break; }

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
                int ford = WaterDepth(nx, nz);
                if (ford > 0)
                {
                    move += ford * genData.roadFordCostMultiplier * dist;
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

        if (foundIdx < 0) { return null; }

        var path = new List<(int, int)>();
        for (int idx = foundIdx; idx != -1; idx = cameFrom[idx])
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
        int v = ws.GetBlockWorld(wx, wy, wz);
        return Blocks.IsNaturalGround(v);
    }

    // Re-derive the surface shape channel from the FINISHED geometry.
    //
    // Every pass that moves terrain — plateaus, ramp skirts, road grading —
    // used to be individually responsible for tagging what it built, and each
    // one that forgot (or defaulted through the 4-arg SetBlockWorld) left a
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
        StampGradeShapes(ws, hm.WorldMinX, hm.WorldMaxX, hm.WorldMinZ, hm.WorldMaxZ, maxGradeStep);

        if (!string.IsNullOrWhiteSpace(CVars.gradeDebug.Value))
        {
            GradeDebug.Dump(CVars.gradeDebug.Value, ws,
                (x, z) => hm.GetSurface(x, z), (x, z) => hm.IsGrade(x, z, maxGradeStep));
        }
    }

    // Bounds-taking form, so a painted world can run the identical pass. The
    // height field was only ever used for horizontal extent here; everything
    // else is read off the finished voxels, which is what lets the painter —
    // which has no HeightMap at all — get the same grades as worldgen instead
    // of a second implementation that drifts.
    public static void StampGradeShapes(WorldState ws, int worldMinX, int worldMaxX,
        int worldMinZ, int worldMaxZ, int maxGradeStep)
    {
        int minY = ws.Min.Y * ChunkState.SIZE;
        int maxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int sizeX = worldMaxX - worldMinX + 1;
        int sizeZ = worldMaxZ - worldMinZ + 1;

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
                int wx = worldMinX + ix;
                int wz = worldMinZ + iz;
                // Above the world ceiling is open sky, so a column that reaches
                // maxY still registers its top voxel as a surface.
                bool aboveSolid = false;
                for (int wy = maxY; wy >= minY; wy--)
                {
                    bool solid = Blocks.IsSolid(ws.GetBlockWorld(wx, wy, wz));
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
            int ix = Math.Clamp(wx, worldMinX, worldMaxX) - worldMinX;
            int iz = Math.Clamp(wz, worldMinZ, worldMaxZ) - worldMinZ;
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
                int wx = worldMinX + ix;
                int wz = worldMinZ + iz;
                int n = ix * sizeZ + iz;
                for (int k = starts[n]; k < starts[n + 1]; k++)
                {
                    int y = surfaces[k];

                    // Every natural surface material, not just Terrain — desert
                    // and marsh columns are their own int and were being
                    // skipped, so their grades never got re-derived.
                    int surface = ws.GetBlockWorld(wx, y, wz);
                    if (!Blocks.IsNaturalGround(surface))
                    {
                        continue;
                    }
                    if (y > minY && !Blocks.IsSolid(ws.GetBlockWorld(wx, y - 1, wz)))
                    {
                        continue;
                    }
                    ws.SetShapeWorld(wx, y, wz, IsGradeAt(wx, wz, y)
                        ? SharpAxes.None
                        : SharpAxes.Y);
                }
            }
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
            ws.SetBlockWorld(wx, by, wz, Blocks.AirId);
            ws.SetOverlayIdWorld(wx, by, wz, 0);
            ws.SetDetailGroupWorld(wx, by, wz, 0);
            ws.SetDetailStrengthWorld(wx, by, wz, 0);
        }
        // Fill: add solid up to the new surface where we raised the column.
        for (int by = hOld + 1; by <= hNew && by <= worldMaxY; by++)
        {
            if (by < worldMinY) { continue; }
            ws.SetBlockWorld(wx, by, wz, ws.Kits.BlockFor(kitId), SharpAxes.Y);
            ws.SetTerrainIdWorld(wx, by, wz, kitId);
        }
        // Bed: guarantee solid rock under the deck (refill cave/tunnel hollows).
        for (int by = hNew; by > hNew - bedDepth && by >= worldMinY; by--)
        {
            if (by > worldMaxY) { continue; }
            int v = ws.GetBlockWorld(wx, by, wz);
            if (v == Blocks.AirId || v == Blocks.WaterId)
            {
                ws.SetBlockWorld(wx, by, wz, ws.Kits.BlockFor(kitId), SharpAxes.Y);
                ws.SetTerrainIdWorld(wx, by, wz, kitId);
            }
        }
        // Surface: flat deck, no detail scatter, road overlay on top.
        if (hNew >= worldMinY && hNew <= worldMaxY)
        {
            ws.SetBlockWorld(wx, hNew, wz, ws.Kits.BlockFor(kitId), SharpAxes.Y);
            ws.SetTerrainIdWorld(wx, hNew, wz, kitId);
            ws.SetDetailGroupWorld(wx, hNew, wz, 0);
            ws.SetDetailStrengthWorld(wx, hNew, wz, 0);
            ws.SetOverlayIdWorld(wx, hNew, wz, overlay);
        }

        hm.Height[wx - hm.WorldMinX, wz - hm.WorldMinZ] = hNew;
        hm.Surface[wx - hm.WorldMinX, wz - hm.WorldMinZ] = hNew;

        // A tread stamped through a ford filled the channel to deck height, so
        // this column is dry now. Clearing the water channel too keeps the map
        // agreeing with the voxels — every later pass reads the channel to
        // decide what is wet.
        if (hm.Water != null && hNew >= hm.Water[wx - hm.WorldMinX, wz - hm.WorldMinZ])
        {
            hm.Water[wx - hm.WorldMinX, wz - hm.WorldMinZ] = HeightMap.NoWater;
        }
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

    // The BACKGROUND drift, everywhere: per-cell current perpendicular to the
    // chunk's zone wind direction in XZ — a 90° CCW rotation, so wind (wx, wz)
    // maps to current (-wz, wx). Matches WindGen's per-zone seeding shape: one
    // direction per chunk, stamped uniformly into every cell. Run after voxel
    // carving; cells whose voxels contain no water still get stamped, but the
    // water shader only samples on water surface fragments so unused stamps cost
    // nothing at render time.
    //
    // This is what the SEA gets. Inland water overwrites it from the real
    // drainage direction — see StampRiverCurrents, which must run after this.
    private static void GenerateAmbientWaterCurrents(WorldState ws)
    {
        // MUST stay well below the slowest river, and that is a hard constraint
        // rather than a taste call. This value is stamped into EVERY cell in the
        // world, including the dry ones flanking a channel, and the shader
        // samples water_current_map TRILINEARLY on a 4 m grid while rivers are
        // ~2-11 m wide — so a river fragment's sample is a blend of its own cell
        // with neighbours carrying this. At 0.7 it beat the fastest river the
        // default world produces (0.65, per the "columns carrying a current"
        // log line) and every river read as the ambient's wind-derived
        // direction instead of its own. It only has to keep the open sea's
        // ripple texture from freezing, which needs very little.
        const float Magnitude = 0.08f;
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

    // How far above and below a column's water surface the current is stamped,
    // in voxels. Not zero, and that is the whole reason these exist: the shader
    // samples `water_current_map` TRILINEARLY at the surface fragment, so a
    // current written only into the cell the surface happens to sit in is
    // averaged against the still cells around it and comes out at a fraction of
    // its authored speed — worst case halved, where the surface lands on a cell
    // boundary. Covering a cell's worth either side makes the sample read the
    // real value wherever in the cell the surface falls.
    private const int CURRENT_STAMP_ABOVE = ChunkState.ENV_VOXELS_PER_CELL;
    private const int CURRENT_STAMP_BELOW = ChunkState.ENV_VOXELS_PER_CELL;

    // Stamp the terrain approach's per-column river flow into the env-cell
    // current subgrid, overwriting the ambient drift wherever inland water runs.
    //
    // Columns are AVERAGED into their cell rather than last-one-wins: an env cell
    // is 4 m across and a channel is 3-8 m wide, so a cell routinely holds both
    // bank and midstream columns, and taking whichever the scan met last makes
    // the current flicker between neighbouring cells down a straight river.
    private static void StampRiverCurrents(WorldState ws, HeightMap heightMap)
    {
        if (heightMap.Current == null || heightMap.Water == null) { return; }
        const int CELL = ChunkState.ENV_VOXELS_PER_CELL;

        var sums = new Dictionary<Vector3I, Vector2>();
        var counts = new Dictionary<Vector3I, int>();
        for (int wx = heightMap.WorldMinX; wx <= heightMap.WorldMaxX; wx++)
        {
            for (int wz = heightMap.WorldMinZ; wz <= heightMap.WorldMaxZ; wz++)
            {
                int waterY = heightMap.GetWaterY(wx, wz);
                if (waterY == HeightMap.NoWater) { continue; }
                Vector2 v = heightMap.GetCurrent(wx, wz);
                if (v == Vector2.Zero) { continue; }

                int cellX = FloorDiv(wx, CELL);
                int cellZ = FloorDiv(wz, CELL);
                int loY = FloorDiv(waterY - CURRENT_STAMP_BELOW, CELL);
                int hiY = FloorDiv(waterY + CURRENT_STAMP_ABOVE, CELL);
                for (int cellY = loY; cellY <= hiY; cellY++)
                {
                    var key = new Vector3I(cellX, cellY, cellZ);
                    sums.TryGetValue(key, out Vector2 sum);
                    counts.TryGetValue(key, out int n);
                    sums[key] = sum + v;
                    counts[key] = n + 1;
                }
            }
        }

        foreach (KeyValuePair<Vector3I, Vector2> kv in sums)
        {
            Vector2 v = kv.Value / counts[kv.Key];
            ws.SetCurrentAtCell(kv.Key.X, kv.Key.Y, kv.Key.Z, v.X, v.Y);
        }
        GD.Print($"[WorldGen] water currents: {sums.Count} env cells stamped from river flow");
    }

    // Load each `.hikescene` and reserve every column its footprint covers, so
    // the content passes leave that ground alone. Loading here (not at stamp
    // time) also means a bad path is reported before the expensive passes
    // rather than after them.
    private static List<ReservedSubscene> LoadAndReserveSubscenes(WorldGenData genData, HeightMap heightMap)
    {
        var loaded = new List<ReservedSubscene>();
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
            // Turn it before anything measures it: the reservation below, the
            // plateau sample and the stamp all read Size / Anchor, and a rotated
            // state already reports the footprint they should be seeing.
            sub = SubsceneRotator.Rotate(sub, (int)placement.rotation);
            Vector3I origin = FootprintOrigin(sub, placement);
            for (int dx = 0; dx < sub.Size.X; dx++)
            {
                for (int dz = 0; dz < sub.Size.Z; dz++)
                {
                    heightMap.MarkNoSpawn(origin.X + dx, origin.Z + dz);
                    if (placement.allowFixtures)
                    {
                        heightMap.MarkFixtureGround(origin.X + dx, origin.Z + dz);
                    }
                }
            }
            // Where the scene will land is fully determined here (Plateau is
            // never rewritten after BuildHeightMap), so resolve it now — the
            // path-hint POIs registered before the stamp need the scene's final
            // world position, and the stamp itself just reads it back.
            int plateauY = FootprintPlateauY((x, z) => heightMap.GetPlateau(x, z),
                heightMap.LevelStep, origin, sub.Size, out int levelCount);
            loaded.Add(new ReservedSubscene
            {
                Sub = sub,
                Placement = placement,
                Origin = origin,
                PlateauY = plateauY,
                PlateauLevelCount = levelCount,
            });
        }
        return loaded;
    }

    // One loaded, rotated subscene awaiting its stamp, plus everything measured
    // off it up front: the ground it reserved and the plateau level it sits on.
    private sealed class ReservedSubscene
    {
        public SubsceneState Sub;
        public SubscenePlacement Placement;
        public Vector3I Origin;
        public int PlateauY;
        public int PlateauLevelCount;

        // The anchor's y=0 course replaces the plateau's top voxel rather than
        // stacking on it — a scene carries its own floor. Everything the scene
        // authored below y=0 goes under the ground from here.
        public Vector3 Anchor => new Vector3(Placement.anchorXZ.X,
            PlateauY + Placement.yOffset, Placement.anchorXZ.Y);
    }

    // Turn every stamped scene's authored path hints into named points of
    // interest, BEFORE the zone POIs roll (so those keep their distance from
    // one) and before the stamp (so the hints never reach the world as
    // entities). A hint tagged "door" in a placement named "house01" registers
    // as "house01.door"; an untagged hint registers as the placement name
    // itself.
    //
    // Each hint also opens a small routing exemption in its own scene's
    // reserved footprint — a front door is inside the building's own ground, so
    // without one the road pass would find its endpoint unreachable. The tread
    // still refuses to stamp reserved columns, so a path stops at the wall.
    private static void RegisterSubscenePathHints(WorldState ws, WorldGenData genData,
        List<ReservedSubscene> reserved, HeightMap heightMap)
    {
        _pathHintsByPlacement.Clear();
        _pathHints.Clear();
        int portalRadius = Math.Max(0, genData.pathHintPortalRadius);

        foreach (ReservedSubscene entry in reserved)
        {
            SubscenePlacement placement = entry.Placement;
            List<PathHintSimState> hints = ExtractPathHints(entry.Sub);
            if (hints.Count == 0)
            {
                continue;
            }

            string placementName = string.IsNullOrEmpty(placement.placementName)
                ? placement.path.GetFile().GetBaseName()
                : placement.placementName;
            if (_pathHintsByPlacement.ContainsKey(placementName))
            {
                GD.PushWarning($"WorldGen: two subscene placements are both named '{placementName}' — the second one's path hints are dropped. Give each placement a distinct PlacementName.");
                continue;
            }

            Vector3 worldOffset = SubsceneStamper.WorldOffset(entry.Sub, entry.Anchor);
            var registered = new List<PathHint>(hints.Count);
            foreach (PathHintSimState hint in hints)
            {
                string poiName = string.IsNullOrEmpty(hint.Tag) ? placementName : $"{placementName}.{hint.Tag}";
                if (ws.PointsOfInterest.ContainsKey(poiName))
                {
                    GD.PushWarning($"WorldGen: subscene '{placement.path}' registers the point of interest '{poiName}' twice — give its path hints distinct tags.");
                    continue;
                }
                Vector3 position = hint.WorldPosition + worldOffset;
                ws.PointsOfInterest[poiName] = position;
                var registeredHint = new PathHint(poiName, hint.Tag, position, placement.connectPathHints);
                registered.Add(registeredHint);
                _pathHints.Add(registeredHint);

                int hx = Mathf.FloorToInt(position.X);
                int hz = Mathf.FloorToInt(position.Z);
                for (int dx = -portalRadius; dx <= portalRadius; dx++)
                {
                    for (int dz = -portalRadius; dz <= portalRadius; dz++)
                    {
                        heightMap.MarkRoadPortal(hx + dx, hz + dz);
                    }
                }
            }
            if (registered.Count > 0)
            {
                _pathHintsByPlacement[placementName] = registered;
            }
        }
    }

    // Removes the scene's path hints from the stamp list and returns them.
    // Consumed here like markers: a hint is a place a road may reach, never an
    // entity the world keeps. Positions are still subscene-local.
    private static List<PathHintSimState> ExtractPathHints(SubsceneState sub)
    {
        var hints = new List<PathHintSimState>();
        if (sub.Entities == null)
        {
            return hints;
        }
        for (int i = sub.Entities.Count - 1; i >= 0; i--)
        {
            if (sub.Entities[i] is PathHintSimState hint)
            {
                hints.Add(hint);
                sub.Entities.RemoveAt(i);
            }
        }
        hints.Reverse();
        return hints;
    }

    private static List<(SubsceneState sub, Vector3 anchor)> StampReservedSubscenes(
        WorldState ws, List<ReservedSubscene> reserved, HeightMap heightMap, int worldSeed)
    {
        var stamped = new List<(SubsceneState, Vector3)>();
        for (int si = 0; si < reserved.Count; si++)
        {
            ReservedSubscene entry = reserved[si];
            SubsceneState sub = entry.Sub;
            SubscenePlacement placement = entry.Placement;
            Vector3I origin = entry.Origin;
            int levelCount = entry.PlateauLevelCount;
            Vector3 anchor = entry.Anchor;
            // Pulled out BEFORE the stamp: a marker is a position this placement
            // may or may not fill, never an entity the world keeps.
            List<MarkerSimState> markers = ExtractMarkers(sub);
            int entityCount = sub.Entities?.Count ?? 0;
            // A fixture-open scene keeps what is already standing on it: the
            // fixture pass ran earlier and placed there deliberately, and a
            // plaza's stamp only re-paves the ground they stand on.
            int evicted = placement.allowFixtures
                ? 0
                : ClearEntitiesInVolume(ws, origin, SubsceneStamper.ComputeWorldOrigin(sub, anchor).Y, sub.Size);
            Vector3 markerOffset = SubsceneStamper.WorldOffset(sub, anchor);
            SubsceneStamper.StampVoxels(ws, sub, anchor);
            int fromVariants = SpawnSubsceneVariants(ws, placement, markers, markerOffset,
                new Random(StableMix(DeriveSeed(worldSeed, SEED_SALT_SUBSCENE), si, 0)));
            stamped.Add((sub, anchor));
            GD.Print($"[WorldGen] stamped subscene {placement.path.GetFile()} at {anchor} (size={sub.Size}, rot={(int)placement.rotation * 90}deg, entities={entityCount}, markers={markers.Count}, variant spawns={fromVariants}, evicted={evicted}, plateau levels under footprint={levelCount})");
        }
        return stamped;
    }

    // Removes the scene's markers from the stamp list and returns them. They
    // still hold subscene-local positions — the caller translates.
    private static List<MarkerSimState> ExtractMarkers(SubsceneState sub)
    {
        var markers = new List<MarkerSimState>();
        if (sub.Entities == null)
        {
            return markers;
        }
        for (int i = sub.Entities.Count - 1; i >= 0; i--)
        {
            if (sub.Entities[i] is MarkerSimState marker)
            {
                markers.Add(marker);
                sub.Entities.RemoveAt(i);
            }
        }
        // The removal walks backwards, so restore authored order — a variant
        // that fills every marker in a pool then does so predictably.
        markers.Reverse();
        return markers;
    }

    // Fills this placement's declared marker pools. Runs AFTER the voxel stamp:
    // the entries' own gates (lateral clearance, navgrid walkability) have to
    // see the building, not the ground it replaced. Returns how many entities
    // were actually placed.
    private static int SpawnSubsceneVariants(WorldState ws, SubscenePlacement placement,
        List<MarkerSimState> markers, Vector3 worldOffset, Random rng)
    {
        SubsceneVariant[] variants = placement.variants ?? System.Array.Empty<SubsceneVariant>();
        if (variants.Length == 0)
        {
            return 0;
        }
        // NOT short-circuited on an empty marker list: a scene with no markers
        // at all is the case most worth reporting, and the per-variant warning
        // below is what names the pool that came up empty.

        // Authored placements, like the fixture passes: roads route around them
        // and never regrade the ground under them.
        bool wasTagging = ws.TaggingFixtures;
        ws.TaggingFixtures = true;
        int spawned = 0;
        var pool = new List<MarkerSimState>();
        foreach (SubsceneVariant variant in variants)
        {
            if (variant == null || string.IsNullOrEmpty(variant.poolTag) || variant.content?.entries == null)
            {
                continue;
            }
            pool.Clear();
            foreach (MarkerSimState marker in markers)
            {
                if (marker.Tag == variant.poolTag)
                {
                    pool.Add(marker);
                }
            }
            if (pool.Count == 0)
            {
                GD.PushWarning($"WorldGen: subscene '{placement.path}' has no markers tagged '{variant.poolTag}'.");
                continue;
            }
            // Hoisted: entries is a Godot Array, so Count and the indexer both
            // cross into native.
            int entryCount = variant.content.entries.Count;
            if (entryCount == 0)
            {
                continue;
            }
            int wanted = variant.count > 0 ? variant.count : entryCount;
            int take = Math.Min(wanted, pool.Count);
            if (take < wanted)
            {
                GD.PushWarning($"WorldGen: subscene '{placement.path}' has {pool.Count} marker(s) tagged '{variant.poolTag}' but the variant wants {wanted} — {wanted - take} not placed.");
            }
            // Partial Fisher-Yates over the pool, one content entry per marker
            // (cycled): which spot each occupant gets varies with the seed, but
            // no marker is used twice and the whole list gets placed.
            var context = new SpawnContext { AuthoredPosition = true };
            for (int i = 0; i < take; i++)
            {
                int j = i + rng.Next(pool.Count - i);
                (pool[i], pool[j]) = (pool[j], pool[i]);
                MarkerSimState marker = pool[i];
                SpawnEntryData entry = variant.content.entries[i % entryCount];
                if (entry == null)
                {
                    continue;
                }
                // The context carries the marker's authored facing and nothing
                // else: the position is authored too, so there is no column
                // sampler to offer — and the terrain gates it would drive judge
                // the ground the building replaced, not the floor stood on.
                context.FacingY = marker.RotationY;
                Vector3 position = marker.WorldPosition + worldOffset;
                if (entry.TrySpawn(ws, position, rng, context))
                {
                    spawned++;
                }
                else
                {
                    // An authored marker that spawns nothing is a bug in the
                    // scene, not a fact about the terrain — say so, because the
                    // gates are all silent and the author would otherwise be
                    // left staring at an empty room.
                    GD.PushWarning($"WorldGen: '{variant.poolTag}' marker at {position} in '{placement.path}' rejected {entry.GetType().Name} — needs {entry.minSpacing}m clear of other entities and a floor its body can stand on (check it with `nav_grid`).");
                }
            }
        }
        ws.TaggingFixtures = wasTagging;
        return spawned;
    }

    // Light the campfire nearest the spawn, so the party starts at a burning
    // fire. Nearest-wins rather than first-found because a village square can
    // hold several; only one campfire in the world may be lit at a time
    // (Campfire.DouseOtherCampfires), and this is the one that starts that way.
    private static void LightSpawnCampfire(WorldState ws, WorldGenData genData)
    {
        float radius = genData.spawnCampfireRadius;
        if (radius <= 0f)
        {
            return;
        }
        float bestDistSq = radius * radius;
        CampfireSimState best = null;
        foreach (EntitySimState e in ws.AllChunkEntities())
        {
            if (e is not CampfireSimState fire)
            {
                continue;
            }
            float distSq = fire.WorldPosition.DistanceSquaredTo(ws.Spawn);
            if (distSq <= bestDistSq)
            {
                bestDistSq = distSq;
                best = fire;
            }
        }
        if (best != null)
        {
            best.Active = true;
        }
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
    // minY is the stamp's bbox floor, NOT the anchor — the anchor sits at the
    // scene's y=0 plane and a scene with a basement extends below it.
    private static int ClearEntitiesInVolume(WorldState ws, Vector3I origin, int minY, Vector3I size)
    {
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
    //
    // Snapped DOWN to the world's HeightMap.LevelStep lattice, so a building
    // floor — and the ceiling above it — lands on the same Y grid every other
    // enclosed space in the world uses, which is what the camera cutaway needs
    // to read cleanly. On the legacy path terrain is already quantized to that
    // step and the snap is an identity; on the organic path the ground is
    // continuous, so without it a floor lands on whatever arbitrary voxel the
    // surface happened to reach. Snapping down rather than to nearest keeps the
    // existing bias: the stamp overwrites its whole bbox, so cutting into the
    // ground is self-correcting where floating over it is not.
    // Takes the ground lookup rather than a HeightMap so the world-map painter,
    // which has no HeightMap, seats its stamps at the same height by the same
    // rule instead of inventing a second one.
    public static int FootprintPlateauY(Func<int, int, int> plateauAt, int levelStep,
        Vector3I origin, Vector3I size, out int levelCount)
    {
        int step = Math.Max(1, levelStep);
        var counts = new Dictionary<int, int>();
        for (int dx = 0; dx < size.X; dx++)
        {
            for (int dz = 0; dz < size.Z; dz++)
            {
                int raw = plateauAt(origin.X + dx, origin.Z + dz);
                int plateau = (int)Math.Floor((double)raw / step) * step;
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

    public static ZoneGenData FirstZoneGen(WorldGenData genData)
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
    public struct BlendedZoneGen
    {
        // Per-zone authored center elevation, kernel-blended at sample
        // time. Eventually the heightmap that feeds BuildHeightMap will be
        // an authored coarse 2D field; this per-zone scalar is the
        // stand-in until that lands.
        public float Elevation;
        public float ElevationRange;
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

        // Flatten override (flattenSurface zones). FlattenWeight is the summed
        // weight of flattening zones at this column (0..1); FlattenLevel is the
        // weight-scaled sum of their targets. An approach pulls its height
        // toward FlattenLevel by FlattenWeight, so a village core sits dead flat
        // while its edge blends back into the surrounding terrain.
        public float FlattenWeight;
        public float FlattenLevel;
    }

    public static BlendedZoneGen SampleBlendedZoneGen(int wx, int wz, ZoneGenData[] zones)
    {
        return SampleBlendedZoneGen(wx, wz, zones, Span<float>.Empty);
    }

    // As above, and ALSO writes the per-zone kernel weights into weightsOut so
    // the caller can blend fields this struct knows nothing about. That is how
    // a terrain approach folds its own per-zone knobs without this struct
    // growing a field per approach — and without paying for the weight solve
    // twice, which is the whole reason it is an out-parameter rather than a
    // second public call.
    public static BlendedZoneGen SampleBlendedZoneGen(int wx, int wz, ZoneGenData[] zones, Span<float> weightsOut)
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

        if (!weightsOut.IsEmpty && weightsOut.Length >= n)
        {
            weights.Slice(0, n).CopyTo(weightsOut);
        }

        for (int i = 0; i < n; i++)
        {
            float w = weights[i];
            if (w <= 0f) { continue; }
            ZoneGenData rg = zones[i];
            if (rg == null) { continue; }
            // Shared terrain scalars come off the zone's terrain sub-resource;
            // a zone that has not been given one blends the base defaults rather
            // than dropping out of the sum and skewing its neighbours' weight.
            ZoneTerrainData zt = rg.terrain;
            result.Elevation += (zt?.elevation ?? 0f) * w;
            result.ElevationRange += (zt?.elevationRange ?? 2f) * w;
            result.GrassThreshold += rg.grassThreshold * w;
            result.MobLevelMin += rg.mobLevelMin * w;
            result.MobLevelMax += rg.mobLevelMax * w;
            result.UndergroundMobLevelMin += rg.undergroundMobLevelMin * w;
            result.UndergroundMobLevelMax += rg.undergroundMobLevelMax * w;
            result.ForgeLevelMin += rg.forgeLevelMin * w;
            result.ForgeLevelMax += rg.forgeLevelMax * w;
            if (zt != null && zt.flattenSurface)
            {
                result.FlattenWeight += w;
                result.FlattenLevel += zt.flattenLevel * w;
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
    public static int DominantZoneIndex(int wx, int wz, ZoneGenData[] zones)
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

    // The generator that produced _lastHeightMap, so DumpDebug can ask it for
    // whatever the shared height-field dump cannot show — anything it carved,
    // above all, since a hillshade of a world with caves in it is identical to
    // one without.
    private static ITerrainGenerator _lastTerrainGen;

    // Writes three PPM images (plateau, height, ramp mask) and a stats text
    // file to `dir`. Called from the `worldgen_debug` console command and
    // from the headless auto-dump path in Main.
    // Terrain-only generate: exactly what BuildHeightMap needs, and nothing
    // else. The full Generate runs chunk fill, lighting, fog, props, mobs,
    // subscenes, roads and the air pipeline before the dump can read the height
    // field — tens of seconds, none of which the terrain iteration loop cares
    // about. This is the loop to use when tuning a TerrainGenData: it leaves
    // _lastHeightMap set, so DumpDebug works exactly as it does after a full
    // generate.
    //
    // Deliberately does NOT produce a usable WorldState — no chunk holds a
    // voxel. Anything that wants voxels wants the real Generate.
    public static HeightMap GenerateTerrainOnly(WorldGenData genData, int worldSeed, Vector2I worldSize)
    {
        _activeGenData = genData;

        var min = new Vector3I(-worldSize.X / 2, 0, -worldSize.Y / 2);
        var max = new Vector3I(min.X + worldSize.X - 1, 0, min.Z + worldSize.Y - 1);
        var ws = new WorldState(min, max, genData.simData,
            KitPalette.Build(genData.kitPalette, genData.ZoneGens));

        // The zone-placement context and the zone/region states are the only
        // setup a terrain approach reads — SampleBlendedZoneGen resolves through
        // both. Skipping either silently flattens every per-zone scalar to its
        // default, which looks like a terrain bug rather than a missing setup.
        var boundsNoise = MakePerlin(DeriveSeed(worldSeed, SEED_SALT_ZONEBOUNDS), 0.15f, 2);
        var spawnChunk = new Vector2I(
            (int)Math.Floor((double)genData.playerSpawnPosition.X / ChunkState.SIZE),
            (int)Math.Floor((double)genData.playerSpawnPosition.Z / ChunkState.SIZE));
        _zoneBoundsContext = new ZoneBoundsContext(min, max, spawnChunk,
            (cx, cz) => boundsNoise.GetNoise2D(cx, cz));
        BuildZoneStates(ws, genData, worldSeed);
        BuildRegionStates(ws, genData);

        TerrainGenData terrain = TerrainOf(genData);
        ITerrainGenerator terrainGen = terrain.CreateGenerator(genData, worldSeed);
        HeightMap heightMap = terrainGen.BuildHeightMap(ws);
        FitVerticalExtent(ws, heightMap, terrain);

        _lastHeightMap = heightMap;
        _lastPlateauStep = heightMap.LevelStep;
        _lastTerrainGen = terrainGen;
        return heightMap;
    }

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

        // Unbroken-traverse runs: how far you can walk in a straight line before
        // a wall stops you. Scanned along both axes, counting columns crossed
        // between one impassable step and the next. This is the metric for "are
        // slopes intermixed with cliffs and plateaus, or is there a huge open
        // slope here" — the transition histogram above can't see it, since a
        // world of pure slope and a world of terraced steps can share one.
        const int WallDrop = 2;         // |Δ| at or above this stops a traverse
        const int LongRunColumns = 64;  // a run past this reads as "wide open"
        var runHist = new Dictionary<int, int>();
        long runTotal = 0;
        long runCount = 0;
        long columnsInLongRuns = 0;
        int longestRun = 0;
        void CloseRun(int len)
        {
            if (len <= 0) { return; }
            int bucket = 1;
            while (bucket * 2 <= len) { bucket *= 2; }
            runHist.TryGetValue(bucket, out int rc);
            runHist[bucket] = rc + 1;
            runTotal += len;
            runCount++;
            if (len > LongRunColumns) { columnsInLongRuns += len; }
            if (len > longestRun) { longestRun = len; }
        }
        for (int z = 0; z < sizeZ; z++)
        {
            int run = 1;
            for (int x = 1; x < sizeX; x++)
            {
                if (Math.Abs(hm.Height[x, z] - hm.Height[x - 1, z]) >= WallDrop)
                {
                    CloseRun(run);
                    run = 1;
                }
                else { run++; }
            }
            CloseRun(run);
        }
        for (int x = 0; x < sizeX; x++)
        {
            int run = 1;
            for (int z = 1; z < sizeZ; z++)
            {
                if (Math.Abs(hm.Height[x, z] - hm.Height[x, z - 1]) >= WallDrop)
                {
                    CloseRun(run);
                    run = 1;
                }
                else { run++; }
            }
            CloseRun(run);
        }

        // Per-zone elevation. Attribution is by DOMINANT zone (the same rule the
        // detail passes use), so a column in a blend band counts once, for
        // whichever zone owns most of it. The median is the honest "ground level"
        // for a zone — a mean is dragged around by whatever fraction of the zone
        // is underwater, and the min is usually just its lowest sea floor.
        var zoneHeights = new Dictionary<int, List<int>>();
        ZoneGenData[] zoneGens = _activeGenData?.ZoneGens;
        if (zoneGens != null && zoneGens.Length > 0)
        {
            for (int x = 0; x < sizeX; x++)
            {
                for (int z = 0; z < sizeZ; z++)
                {
                    int zi = DominantZoneIndex(hm.WorldMinX + x, hm.WorldMinZ + z, zoneGens);
                    if (zi < 0) { continue; }
                    if (!zoneHeights.TryGetValue(zi, out List<int> list))
                    {
                        list = new List<int>();
                        zoneHeights[zi] = list;
                    }
                    list.Add(hm.Height[x, z]);
                }
            }
        }

        // Sustained grade: rise measured across a short run rather than between
        // one pair, which is the only way to see a hillside that climbs a voxel
        // every column — legal at every pair, a 45-degree ramp end to end.
        // Samples spanning a wall are excluded; a wall is not a slope.
        const int GradeRun = 3;
        long gradeSamples = 0;
        long gradeOver = 0;
        for (int x = 0; x + GradeRun < sizeX; x++)
        {
            for (int z = 0; z + GradeRun < sizeZ; z++)
            {
                CountGrade(hm.Height[x + GradeRun, z] - hm.Height[x, z]);
                CountGrade(hm.Height[x, z + GradeRun] - hm.Height[x, z]);
            }
        }
        void CountGrade(int rise)
        {
            rise = Math.Abs(rise);
            if (rise >= 3) { return; }   // a wall, not a grade
            gradeSamples++;
            if (rise > 1) { gradeOver++; }
        }

        using (var sw = new System.IO.StreamWriter($"{dir}/stats.txt"))
        {
            sw.WriteLine($"Sustained grade over {GradeRun} columns: "
                + $"{100.0 * gradeOver / Math.Max(1, gradeSamples):F2}% of open ground steeper than 1-in-{GradeRun}");
            sw.WriteLine();
            if (zoneHeights.Count > 0)
            {
                sw.WriteLine("Per-zone surface elevation (voxels relative to sea level):");
                sw.WriteLine("  zone                          columns    min    p50    p95    max   rise(p50..max)");
                foreach (KeyValuePair<int, List<int>> kv in zoneHeights.OrderBy(k => k.Key))
                {
                    List<int> hs = kv.Value;
                    hs.Sort();
                    int median = hs[hs.Count / 2];
                    int p95 = hs[(int)(hs.Count * 0.95f)];
                    int max = hs[hs.Count - 1];
                    string path = zoneGens[kv.Key]?.ResourcePath ?? "<null>";
                    string name = path.Substring(path.LastIndexOf('/') + 1).Replace(".tres", "");
                    sw.WriteLine($"  {name,-28} {hs.Count,8} {hs[0],6} {median,6} {p95,6} {max,6}   {max - median,6}");
                }
                sw.WriteLine();
            }
            sw.WriteLine($"Unbroken traverse: mean {(runCount > 0 ? (double)runTotal / runCount : 0):F1} columns, longest {longestRun}");
            sw.WriteLine($"  in runs > {LongRunColumns} columns: {100.0 * columnsInLongRuns / Math.Max(1, runTotal):F1}% of ground");
            sw.WriteLine("  run-length histogram (power-of-two bucket : count):");
            foreach (var kv in runHist.OrderBy(k => k.Key))
            {
                sw.WriteLine($"  {kv.Key,5}: {kv.Value}");
            }
            sw.WriteLine();
            sw.WriteLine($"World: {sizeX} x {sizeZ} = {total} columns");
            sw.WriteLine($"Raw: height.bin / plateau.bin"
                + (hm.Water != null ? " / water.bin (short.MinValue = dry)" : "")
                + $", int16 LE, x-major {sizeX}x{sizeZ},"
                + $" world origin ({hm.WorldMinX}, {hm.WorldMinZ})");
            // Which approach produced this, so a dump is self-identifying — the
            // stats below read very differently between them.
            sw.WriteLine($"Terrain: {_activeGenData?.terrain?.GetType().Name ?? "<none>"}"
                + $", interior level step {_lastPlateauStep}");
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
            sw.WriteLine();
            WriteWaterStats(sw, hm, total);
        }

        WritePlateauPpm($"{dir}/plateau.ppm", hm.Plateau, minP, maxP, _lastPlateauStep);
        WritePlateauPpm($"{dir}/height.ppm", hm.Height, minH, maxH, _lastPlateauStep);
        WriteRampPpm($"{dir}/ramp.ppm", hm);
        WriteHillshadePpm($"{dir}/hillshade.ppm", hm, minH, maxH);

        // Raw fields alongside the images, so a dump can be analysed
        // numerically instead of by eye. int16 little-endian, row-major in x
        // then z, sizeX*sizeZ values, world origin (WorldMinX, WorldMinZ) — the
        // layout is written into stats.txt so a reader needs nothing else.
        WriteRawField($"{dir}/height.bin", hm.Height);
        WriteRawField($"{dir}/plateau.bin", hm.Plateau);
        if (hm.Water != null)
        {
            // Sentinel-free: NoWater writes as short.MinValue so the file is a
            // plain int16 field like the other two and a reader needs no
            // knowledge of HeightMap's constant.
            WriteRawField($"{dir}/water.bin", hm.Water);
        }
        // Whatever this approach carved. The images above are a heightfield
        // view and cannot show any of it.
        _lastTerrainGen?.DumpDiagnostics(dir);

        GD.Print($"worldgen_debug: wrote {dir}/stats.txt, plateau.ppm, height.ppm, ramp.ppm,"
            + " hillshade.ppm, height.bin, plateau.bin"
            + (hm.Water != null ? ", water.bin" : ""));
    }

    // Where the water ended up: how much of the world it covers, how deep it
    // stands, how the surface levels are distributed (they must all be lattice
    // multiples — an odd one is a bug in the approach, not a tuning problem),
    // and how large the connected bodies are. The last one is the check for
    // one-column puddles, which no coverage percentage can show.
    private static void WriteWaterStats(System.IO.StreamWriter sw, HeightMap hm, int total)
    {
        if (hm.Water == null)
        {
            sw.WriteLine("Inland water: none (approach produces no water channel)");
            return;
        }
        int sizeX = hm.Water.GetLength(0);
        int sizeZ = hm.Water.GetLength(1);
        var levelHist = new Dictionary<int, int>();
        var depthHist = new Dictionary<int, int>();
        int wet = 0;
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                int wy = hm.Water[x, z];
                if (wy == HeightMap.NoWater) { continue; }
                wet++;
                levelHist.TryGetValue(wy, out int lc); levelHist[wy] = lc + 1;
                int d = wy - hm.Height[x, z];
                depthHist.TryGetValue(d, out int dc); depthHist[d] = dc + 1;
            }
        }

        // Connected bodies, 4-connected and split by surface level: two pools of
        // a cascade touch at the lip but are different bodies.
        var seen = new bool[sizeX, sizeZ];
        var bodies = new List<int>();
        var stack = new Stack<(int, int)>();
        for (int x0 = 0; x0 < sizeX; x0++)
        {
            for (int z0 = 0; z0 < sizeZ; z0++)
            {
                if (seen[x0, z0] || hm.Water[x0, z0] == HeightMap.NoWater) { continue; }
                int level = hm.Water[x0, z0];
                int size = 0;
                seen[x0, z0] = true;
                stack.Push((x0, z0));
                while (stack.Count > 0)
                {
                    (int x, int z) = stack.Pop();
                    size++;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                        int nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ) { continue; }
                        if (seen[nx, nz] || hm.Water[nx, nz] != level) { continue; }
                        seen[nx, nz] = true;
                        stack.Push((nx, nz));
                    }
                }
                bodies.Add(size);
            }
        }
        bodies.Sort();

        sw.WriteLine($"Inland water: {wet} columns ({100.0 * wet / Math.Max(1, total):F2}% of world)"
            + $" in {bodies.Count} connected bodies");
        if (bodies.Count > 0)
        {
            int singles = 0;
            foreach (int b in bodies) { if (b <= 2) { singles++; } }
            sw.WriteLine($"  body size: min {bodies[0]}, median {bodies[bodies.Count / 2]},"
                + $" max {bodies[bodies.Count - 1]}; {singles} of 1-2 columns");
        }
        sw.WriteLine("  surface-level histogram (world Y : columns) — all must be lattice multiples:");
        foreach (var kv in levelHist.OrderBy(k => k.Key))
        {
            sw.WriteLine($"  {kv.Key,4}: {kv.Value}");
        }
        sw.WriteLine("  depth histogram (water surface - ground : columns):");
        foreach (var kv in depthHist.OrderBy(k => k.Key))
        {
            sw.WriteLine($"  {kv.Key,4}: {kv.Value}");
        }
    }

    // Paints each plateau step as a distinct hue (so 4-voxel bands read as
    // flat color patches) and darkens within a band by the voxel offset from
    // the band's base (so ramp lift inside a band shows up as a gradient).
    // int16 little-endian, row-major in x then z. Terrain heights are tens of
    // voxels, so 16 bits is ample and keeps a world-sized field small enough to
    // read and re-read while iterating.
    private static void WriteRawField(string path, int[,] field)
    {
        int sizeX = field.GetLength(0);
        int sizeZ = field.GetLength(1);
        var bytes = new byte[sizeX * sizeZ * 2];
        int i = 0;
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                short v = (short)Math.Clamp(field[x, z], short.MinValue, short.MaxValue);
                bytes[i++] = (byte)(v & 0xFF);
                bytes[i++] = (byte)((v >> 8) & 0xFF);
            }
        }
        System.IO.File.WriteAllBytes(path, bytes);
    }

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

    // Relief-shaded elevation: a hypsometric ramp (blue sea → green lowland →
    // tan upland → grey rock) lit from the northwest by the per-column slope,
    // plus a red overlay on every cliff face. This is the image to read when
    // judging terrain SHAPE — the banded plateau/height dumps quantize the
    // field into flat colour patches and hide slope entirely.
    private static void WriteHillshadePpm(string path, HeightMap hm, int minH, int maxH)
    {
        const int CliffDrop = 2;          // |Δ| at or above this paints as a wall
        const float LightStrength = 0.28f; // shading contribution per voxel of slope

        int w = hm.Height.GetLength(0);
        int h = hm.Height.GetLength(1);
        using var fs = System.IO.File.Create(path);
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        fs.Write(header, 0, header.Length);
        byte[] row = new byte[w * 3];
        float span = Math.Max(1, maxH - minH);
        for (int z = h - 1; z >= 0; z--)
        {
            for (int x = 0; x < w; x++)
            {
                int v = hm.Height[x, z];
                int east = hm.Height[Math.Min(x + 1, w - 1), z];
                int north = hm.Height[x, Math.Min(z + 1, h - 1)];
                float t = (v - minH) / span;

                (float r, float g, float b) c = v < WATER_LEVEL
                    ? (0.15f, 0.30f, 0.55f)
                    : t < 0.45f ? (0.35f, 0.55f, 0.30f)
                    : t < 0.75f ? (0.65f, 0.60f, 0.38f)
                    : (0.70f, 0.70f, 0.70f);

                // Northwest key light: a face tilting away from +x/+z darkens.
                float shade = Math.Clamp(1f + LightStrength * ((v - east) + (v - north)), 0.25f, 1.8f);
                int drop = Math.Max(Math.Abs(v - east), Math.Abs(v - north));
                if (drop >= CliffDrop)
                {
                    c = (0.85f, 0.25f, 0.20f);
                }

                // Inland water last, so it paints OVER the cliff overlay — a
                // gorge is a cliff by every geometric test and the point of the
                // overlay is to see where the river actually goes. Cyan, and
                // stepped by the surface level so a cascade's pools read as
                // distinct bands rather than one flat ribbon.
                int wy = hm.Water != null ? hm.Water[x, z] : HeightMap.NoWater;
                if (wy != HeightMap.NoWater)
                {
                    float band = 0.55f + 0.45f * (((wy / Math.Max(1, hm.LevelStep)) & 1) == 0 ? 1f : 0.55f);
                    c = (0.10f * band, 0.85f * band, 0.95f * band);
                    shade = 1f;
                }
                row[x * 3 + 0] = (byte)Math.Clamp(c.r * shade * 255f, 0f, 255f);
                row[x * 3 + 1] = (byte)Math.Clamp(c.g * shade * 255f, 0f, 255f);
                row[x * 3 + 2] = (byte)Math.Clamp(c.b * shade * 255f, 0f, 255f);
            }
            fs.Write(row, 0, row.Length);
        }
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
        // "above water" is h+1 > waterline, i.e. h >= waterline. Strict
        // greater-than was wrong: it excluded shoreline plateaus (h=WATER_LEVEL)
        // whose top voxel sits exactly at the water plane but whose air-above
        // is still dry — exactly the band where forest's noise dips would
        // otherwise plant trees. Measured against the COLUMN's waterline, so a
        // river bed or lake floor above sea level is wet ground too.
        return h == heightMap.GetPlateau(wx, wz) && h >= WaterYAt(heightMap, wx, wz);
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

    private static void GenerateChunk(ChunkState data, WorldGenData genData, KitPalette palette,
        ITerrainGenerator terrainGen, HeightMap heightMap)
    {
        int chunkWorldX = data.ChunkCoord.X * ChunkState.SIZE;
        int chunkWorldY = data.ChunkCoord.Y * ChunkState.SIZE;
        int chunkWorldZ = data.ChunkCoord.Z * ChunkState.SIZE;

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
                byte surfaceShape = (byte)(heightMap.IsGrade(wx, wz, TerrainOf(genData).maxGradeStep)
                    ? SharpAxes.None
                    : SharpAxes.Y);

                // Per-column kit pick + above-water shore band, hoisted out of
                // the y loop because both depend only on (wx, wz). The shore
                // upper bound is a per-column random value in
                // [ShoreElevationMin, ShoreElevationMax] meters above sea
                // level — keeps the shoreline jagged instead of a flat
                // isobar. Columns whose zone has no ShoreKit get an empty
                // band (shoreUpperY = WATER_LEVEL → no voxel falls in it).
                // The waterline this column answers to — the sea, or a river /
                // lake surface above it. Both the fill below and the shore band
                // are measured from it, so a lake gets the same beach lip at its
                // own level that the coast gets at sea level.
                int waterY = WaterYAt(heightMap, wx, wz);
                int kitZone = PickKitZone(wx, wz, genData.ZoneGens, data.ZoneIndex);
                ZoneGenData kitZoneData = kitZone >= 0 ? genData.ZoneGens[kitZone] : null;
                byte surfaceTerrainId = palette.SlotOf(kitZoneData?.surfaceKit);
                byte shoreTerrainId = surfaceTerrainId;
                int shoreUpperY = waterY;
                if (kitZoneData != null && kitZoneData.shoreKit != null)
                {
                    shoreTerrainId = palette.SlotOf(kitZoneData.shoreKit);
                    float shoreUpperR = HashFloat01(wx, wz, SHORE_UPPER_HASH_SALT);
                    float shoreUpperMeters = Mathf.Lerp(
                        kitZoneData.shoreElevationMin,
                        kitZoneData.shoreElevationMax,
                        shoreUpperR);
                    shoreUpperY = waterY + (int)Math.Round(shoreUpperMeters);
                }

                // Sand is a BEACH, not a contour line. The elevation band alone
                // dressed every solid voxel inside it, which put sand on the FACE
                // of any cliff rising out of the sea and on lowland plains nowhere
                // near water — and because the block carries footsteps and climb
                // growth, all of that followed the wrong material too.
                //
                // Two extra tests, hoisted here because both depend only on the
                // column: its top must be the voxel in the band (a wall face is
                // not a beach, so the surface voxel alone qualifies below), and it
                // must actually be beside water. The submerged pass answers that
                // by finding a Water voxel; up here the grid is still being
                // written, so the heightmap answers instead — a neighbouring
                // column holds water when its ground sits below its own waterline.
                bool columnIsBeach = false;
                if (kitZoneData != null && kitZoneData.shoreKit != null
                    && solidHeight > waterY && solidHeight <= shoreUpperY)
                {
                    int shoreReach = Math.Max(kitZoneData.shoreWaterDistance, 1);
                    for (int dx = -shoreReach; dx <= shoreReach && !columnIsBeach; dx++)
                    {
                        for (int dz = -shoreReach; dz <= shoreReach && !columnIsBeach; dz++)
                        {
                            int nx = wx + dx;
                            int nz = wz + dz;
                            if (heightMap.GetHeight(nx, nz) < WaterYAt(heightMap, nx, nz))
                            {
                                columnIsBeach = true;
                            }
                        }
                    }
                }

                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    int wy = chunkWorldY + y;

                    // Caves carve only when the column reaches the plateau
                    // ceiling above the band — that ceiling row (rem=0) is
                    // never carved by IsCaveAt, so it serves as the cave roof
                    // and we get a guaranteed full-height cave.
                    bool solid = wy <= solidHeight && !terrainGen.IsCarvedAt(wx, wy, wz, solidHeight);

                    if (!solid)
                    {
                        // A carved voxel the approach has SEALED stays air even
                        // under the waterline: it is enclosed rock with no way
                        // in, so nothing can run into it. Without the test a
                        // cave descending below sea level fills to its ceiling,
                        // which looks exactly like a cave that never generated.
                        // Only carved voxels can be sealed, so terrain that was
                        // never hollowed is unaffected — as are approaches that
                        // carve nothing, which answer false.
                        if (wy <= waterY && !terrainGen.IsSealedFromWaterAt(wx, wy, wz))
                        {
                            data.Voxels[x, y, z] = (byte)Blocks.WaterId;
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
                    // via SetBlockWorld and take the non-AUTO shader path.
                    int kit = (columnIsBeach && wy == solidHeight) ? shoreTerrainId : surfaceTerrainId;
                    data.Voxels[x, y, z] = (byte)palette.BlockFor(kit);

                    // Cave interior surfaces always snap flat, regardless of
                    // whether the outdoor ridge above this column is a plateau
                    // or a path-band ramp. Without this override a cave carved
                    // under a ramp column inherits the column's None tag, the
                    // ceiling vertex interpolates downward, and the ceiling
                    // pokes below the clip plane into the player's view.
                    // A voxel is a cave surface if the cell directly above it
                    // OR directly below it is a carved tunnel cell.
                    bool aboveIsCarved = wy + 1 <= solidHeight
                        && terrainGen.IsCarvedAt(wx, wy + 1, wz, solidHeight);
                    bool belowIsCarved = wy - 1 >= 0
                        && terrainGen.IsCarvedAt(wx, wy - 1, wz, solidHeight);
                    byte voxelShape = wy == solidHeight ? surfaceShape : (byte)SharpAxes.Y;
                    data.Shape[x, y, z] = (aboveIsCarved || belowIsCarved)
                        ? (byte)SharpAxes.Y
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
                    data.TerrainId[x, y, z] = (byte)kit;
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
                    if (ws.GetBlockWorld(wx, wy, wz) != Blocks.AirId)
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
                    if (ws.GetBlockWorld(wx, wy, wz) != Blocks.AirId)
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
    private static void GenerateTestUnderwaterLake(WorldState ws, HeightMap heightMap)
    {
        const int CenterX = 20;
        const int CenterZ = 32;
        const int HalfSize = 25;            // 50x50 footprint
        const int FloorY = WATER_LEVEL - 3; // 4 voxels of standing water

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;

        // The world's enclosed-space lattice, so this cavern's roof sits at a
        // height the camera cutaway reads like any other ceiling.
        int ceilingY = WATER_LEVEL + heightMap.LevelStep;

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
                    var v = ws.GetBlockWorld(wx, wy, wz);
                    if (v != Blocks.AirId && v != Blocks.WaterId)
                    {
                        naturalSurfaceY = wy;
                        break;
                    }
                }

                int topCarve = Math.Min(ceilingY - 1, naturalSurfaceY);
                for (int wy = FloorY + 1; wy <= topCarve; wy++)
                {
                    var fill = wy <= WATER_LEVEL ? Blocks.WaterId : Blocks.AirId;
                    ws.SetBlockWorld(wx, wy, wz, fill);
                }

                // Stamp a solid floor only where the natural seabed sat
                // above it. Where the column was already deeper than FloorY
                // (open ocean), leave the existing geometry so the lake
                // merges seamlessly with the sea.
                if (naturalSurfaceY > FloorY)
                {
                    ws.SetBlockWorld(wx, FloorY, wz,
                        ws.Kits.BlockFor(ws.GetTerrainIdWorld(wx, FloorY, wz)), SharpAxes.Y);
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
                    var v = ws.GetBlockWorld(wx, wy, wz);
                    if (Blocks.IsSolid(v) && v != Blocks.BarrierId)
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
            var v = ws.GetBlockWorld(wx, wy, wz);
            if (v != Blocks.AirId && v != Blocks.WaterId) { return false; }
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
                    var v = ws.GetBlockWorld(wx, wy, wz);
                    if (!Blocks.IsSolid(v) || v == Blocks.BarrierId)
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
                        ws.SetShapeWorld(wx, wy, wz, SharpAxes.Y);
                        // Stamp this chunk's zone CaveKit so the shader
                        // can paint it distinctly from the surface above.
                        // Overrides SubmergedKit for submerged caves — the
                        // cave palette wins there.
                        int zoneIdx = PickKitZone(wx, wz, genData.ZoneGens, ZoneIndexAtWorld(ws, wx, wy, wz));
                        RestampKit(ws, wx, wy, wz, ws.Kits.SlotOf(genData.ZoneGens[zoneIdx]?.caveKit));
                    }
                }
            }
        }
    }

    // Re-tag solid voxels at or below WATER_LEVEL to KIT_UNDERWATER iff they
    // sit within WorldGenData.SubmergedKitRadius of a water voxel. Runs after every
    // chunk exists so the water pass has already filled every non-solid
    // wy<=WATER_LEVEL cell with Blocks.WaterId. Semantic "near water" beats
    // the old "wy<=WATER_LEVEL" rule because the latter paints deeply buried
    // rock under cliffs as underwater — then the mesher's 27-voxel kit vote
    // for nearby DC cells drags that sand onto the visible cliff face.
    private static void TagSubmergedKits(WorldState ws, WorldGenData genData, HeightMap heightMap)
    {
        // Chebyshev radius for the water-adjacency search. Must be >= 2 (see
        // WorldGenData.SubmergedKitRadius for the mesher-vote rationale).
        int submergedRadius = genData.submergedKitRadius;
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;
        long dressed = 0, skippedCliff = 0;

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
                int waterY = WaterYAt(heightMap, wx, wz);
                int columnZone = PickKitZone(wx, wz, genData.ZoneGens, 0);
                ZoneGenData columnZoneData = columnZone >= 0 ? genData.ZoneGens[columnZone] : null;
                byte shoreTerrainId = 0;
                int shoreLowerY = waterY;
                bool hasShore = columnZoneData != null && columnZoneData.shoreKit != null;

                // Sand belongs to columns that ARE seabed. A CLIFF column — ground
                // standing above the waterline — is not one at any depth, and
                // dressing the rock buried inside it is precisely what the
                // mesher's 27-voxel vote then bleeds onto the visible face (see
                // the note at the call site). Measured: 6000 such voxels inside
                // mountain cliffs running into the sea, which is what put beach —
                // and beach's lichen — at their base.
                //
                // Skipping the whole column rather than just the visible face is
                // what keeps the SEABED winning that same vote: a seabed column
                // stays dressed to full depth, so its surface is not one lonely
                // sand voxel against the rock beneath it.
                if (heightMap.GetHeight(wx, wz) > waterY)
                {
                    skippedCliff++;
                    continue;
                }

                for (int wy = worldMinY; wy <= waterY; wy++)
                {
                    var v = ws.GetBlockWorld(wx, wy, wz);
                    if (!Blocks.IsSolid(v) || v == Blocks.BarrierId)
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
                                if (ws.GetBlockWorld(wx + dx, wy + dy, wz + dz) == Blocks.WaterId)
                                {
                                    nearWater = true;
                                }
                            }
                        }
                    }

                    // Sand dresses the TOP of the seabed, never the FACE of a
                    // wall descending through it. Without this a cliff running
                    // down into the water took a band of beach across its face —
                    // and with it beach's climb growth, so a wall meant to be
                    // climbable from the water up broke into the cliff's own
                    // growth, a gap, then lichen at the waterline.
                    //
                    // Mirrors the above-water rule in chunk fill, which asks the
                    // same question as `wy == solidHeight`. A sloped seabed still
                    // qualifies at every step, because each of those voxels does
                    // have its top open — only true verticals are excluded.
                    if (nearWater)
                    {
                        dressed++;
                        if (hasShore && wy >= shoreLowerY)
                        {
                            RestampKit(ws, wx, wy, wz, shoreTerrainId);
                        }
                        else
                        {
                            int zoneIdx = PickKitZone(wx, wz, genData.ZoneGens, ZoneIndexAtWorld(ws, wx, wy, wz));
                            RestampKit(ws, wx, wy, wz, ws.Kits.SlotOf(genData.ZoneGens[zoneIdx]?.submergedKit));
                        }
                    }
                }
            }
        }

        GD.Print($"WorldGen: submerged kits — {dressed} seabed voxels dressed, "
            + $"{skippedCliff} cliff columns skipped");
    }

    // Overlay id values. 0 = no overlay. A non-zero OverlayId is a direct
    // tile_array base-layer index sampled by voxel_clip.gdshader with its
    // own alpha channel driving blend strength. Any block in the BlockSurfaceCatalog
    // can be used as an overlay — add new OVERLAY_* fields by name rather
    // than reusing numbers so .hike files written with old values keep
    // mapping to the right block when new blocks are added ahead of them.
    //
    // An overlay is an ADDITIVE SKIN over whatever block is underneath: the road
    // tread below, creeping moss later. It names a LAYER, not a block, so it
    // carries no material properties (footstep, speed, dig yield), and there is
    // only ONE slot per voxel. A material that wants to BE the ground is a block
    // — which is why dirt patches write Dirt through StampDirtPatches rather
    // than taking the slot moss will need.
    private const byte OVERLAY_NONE = 0;

    // Re-stamp a voxel's kit AND the block that kit resolves to.
    //
    // Appearance lives on the BLOCK now, not on the kit channel — so a pass that
    // writes only TerrainId changes nothing you can see: the voxel keeps
    // whatever block the column fill gave it. Every later kit re-stamp
    // (submerged shell, underwater shore band, cave surfaces) has to go through
    // here, the way the road pass already writes both.
    //
    // Shape is PRESERVED. These voxels carry authored terrain shapes (a ramp
    // column's None), and re-stamping at the block's default Y would re-harden
    // them into 1-voxel steps.
    private static void RestampKit(WorldState ws, int wx, int wy, int wz, int kitId)
    {
        ws.SetTerrainIdWorld(wx, wy, wz, kitId);
        ws.SetBlockWorld(wx, wy, wz, ws.Kits.BlockFor(kitId), ws.GetShapeWorld(wx, wy, wz));
    }

    private static readonly byte DIRT_BLOCK = ResolveBlockId("Dirt");

    private static byte ResolveBlockId(StringName blockName)
    {
        BlockData block = BlockCatalog.Active.GetByName(blockName);
        if (block == null)
        {
            GD.PushError($"WorldGen: block '{blockName}' is missing from the catalog.");
            return 0;
        }
        return (byte)block.blockId;
    }

    // Edge-overlay scan window / diff band (EdgeScanWindow, EdgeMinDiff,
    // EdgeMaxDiff) and the procedural overlay scatter frequencies / thresholds
    // are authored on WorldGenData. The scatter SEEDS stay fixed here — they're
    // stable RNG salts (like the SEED_SALT_* channels), not feel knobs.
    private const int DIRT_PATCH_SEED = 4242;
    private const int MOSS_PATCH_SEED = 4243;
    private const int MOSS_CAPILLARY_SEED = 4244;
    private const int MOSS_PATCHINESS_SEED = 4245;
    private const int CLIMB_PATCH_SEED = 4246;
    // FastNoiseLite's CellValue is BELL-SHAPED, not uniform. Measured over 576k
    // samples it spans -0.96..0.90, but the middle is only ~0.56 as wide as a
    // uniform field would be, so thresholding it directly at the authored
    // coverage delivered 9% for an authored 25%. Remapping about the median
    // makes the knob mean what it says across the useful range. Same class of
    // correction as MOSS_NOISE_GAIN, and measured the same way — re-measure it
    // if the cellular settings change, don't assume it carries over.
    private const float CLIMB_CELL_SPREAD = 0.563f;

    // FastNoiseLite's fractal Perlin does not reach ±1 — the river width channel
    // measured only -0.38..0.51 on this world (see WIDTH_NOISE_GAIN), and the
    // moss channel is the same shape. |noise| therefore spans well under 0..1,
    // so the gain restores the authored coverage's reach over the vein width.
    private const float MOSS_NOISE_GAIN = 2.2f;

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

    // Noise-scatter dirt patches on Surface-kit voxels.
    // Only top-surface voxels (solid with air above) are candidates so buried
    // geometry and cliff faces stay untouched. Kit gate restricts placement
    // to Surface kits — sand (underwater/cave) and cave palette stay clean.
    //
    // Writes the Dirt BLOCK, not an overlay: dirt is the ground here, so it
    // should carry its own footstep type, speed and dig yield, and it must not
    // occupy the single overlay slot. The voxel's authored SHAPE is preserved —
    // a ramp voxel re-stamped at the block's default Y would re-harden into a
    // 1-voxel step. TerrainId (the kit channel) is left alone too, so detail
    // scatter and the kit tunings still see the terrain they were authored for.
    private static void StampDirtPatches(WorldState ws, WorldGenData genData)
    {
        var dirtNoise = new FastNoiseLite();
        dirtNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        dirtNoise.Seed = DIRT_PATCH_SEED;
        dirtNoise.Frequency = genData.dirtPatchFrequency;
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
                    if (!ws.Kits.IsSurfaceKit(ws.GetTerrainIdWorld(wx, wy, wz)))
                    {
                        continue;
                    }

                    if (dirtNoise.GetNoise2D(wx, wz) > genData.dirtPatchThreshold)
                    {
                        ws.SetBlockWorld(wx, wy, wz, DIRT_BLOCK, ws.GetShapeWorld(wx, wy, wz));
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
    //
    // Two knobs, because the painter's bake runs this same pass over a painted
    // world (see WorldMapState.BuildWorld): `skipColumn` names the columns whose
    // surface is a deliberate tread — worldgen's roads, the painter's paving —
    // and `dominantZoneKit` is off there, since a painted world assigns kits per
    // column deterministically and has no zone-weight kernel to take an argmax
    // of. Everything else — the surface walk, the gates, the noise, the strength
    // ramp — is shared rather than reimplemented per caller.
    public static void StampDetailScatter(WorldState ws, WorldGenData genData,
        Func<int, int, bool> skipColumn, bool dominantZoneKit)
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
        Func<int, int, int, int> getVoxel = ws.GetBlockWorld;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                // A road tread is bare dirt, and this pass runs after it is laid
                // — so the road is kept clear here rather than by clearing the
                // detail channel again after the fact.
                if (skipColumn != null && skipColumn(wx, wz))
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
                    if (ws.GetBlockWorld(wx, wy + 1, wz) == Blocks.WaterId)
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
                    bool isSurface = ws.Kits.IsSurfaceKit(voxelTerrainId);
                    TerrainKitData kit = isSurface && dominantZoneKit
                        ? (DominantZoneSurfaceKit(wx, wz, genData.ZoneGens) ?? ws.Kits.KitAt(voxelTerrainId))
                        : ws.Kits.KitAt(voxelTerrainId);
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

                    ws.SetDetailGroupWorld(wx, wy, wz, ws.Kits.DetailSlotOf(kit.defaultDetail));
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

    // Scatter the moss OVERLAY over exposed rock and ground.
    //
    // Moss is an overlay, not a block: it is a skin over whatever is underneath,
    // so the stone stays stone underfoot and in the sim. Coverage is per zone and
    // split surface/cave, because caves are damp and want much more of it (and
    // the desert wants none, above or below).
    //
    // Unlike the dirt pass this walks every AIR-EXPOSED voxel, not just the ones
    // with air above — cliff faces are the whole point, and a face has air to the
    // side. Whether a given layer is allowed onto a wall is the shader's call via
    // BlockSurfaceData.overlayOnCliffs, so this pass just marks the rock.
    //
    // SHAPE: moss creeps in strands, so the field is a VEIN distance, not a
    // blob threshold. Thresholding noise itself keeps whichever side of the
    // contour is above the cut — a filled region, hence round patches, and no
    // amount of frequency tuning makes it stringy. |noise| instead measures
    // distance from the noise's ZERO SET, a curved sheet through the world
    // whose intersection with the terrain is a meandering line, so a low cut
    // keeps a thin band either side of it. Three things then make that read as
    // growth: a domain warp so the strands crook rather than flow, a second
    // finer network unioned in (min = the union of both bands) for hairlines
    // branching off the trunks, and a long-wavelength coverage modulation so a
    // strand thins and dies along its length.
    // The four horizontal faces a wall can present. Vertical faces are excluded
    // deliberately: a floor or a ceiling is not something the climb affordance
    // attaches to, and dressing them would put ivy on every cliff TOP.
    private static readonly (int dx, int dz, EVoxelFace face)[] ClimbFaces =
    {
        (1, 0, EVoxelFace.PosX),
        (-1, 0, EVoxelFace.NegX),
        (0, 1, EVoxelFace.PosZ),
        (0, -1, EVoxelFace.NegZ),
    };

    // Dresses tall cliff faces with a climbable overlay, in cellular patches.
    //
    // Height is measured as an unbroken run of exposure on ONE face, walked up
    // the column, rather than from the heightfield: the heightfield knows what a
    // column's top is, not how much of a given side of it stands open, and those
    // differ at every overhang, bench and cave mouth. Runs also cost nothing
    // extra — the walk is already happening.
    //
    // Cellular rather than the vein noise moss uses, because the shapes want to
    // read differently: moss is strands seeping down a face, ivy is colonies
    // that own a patch of it. CellValue gives one random value per cell, so a
    // threshold keeps WHOLE cells and their irregular borders; a distance return
    // would give circular blooms with soft edges instead.
    // The two per-column answers are supplied rather than read, because the
    // world-map painter runs this same pass over a world it built itself: it has
    // no HeightMap and its coverage is a painted scalar, not a zone's. Everything
    // else — the face walk, the run heights, the patch noise, the per-block
    // growth table — is identical for both, and reimplementing it painter-side is
    // how the waterfall shading ended up as two copies that drifted.
    // minWallVoxels is the shortest wall worth dressing, and `patchy` decides
    // whether coverage means "this fraction of the face, in cellular patches" or
    // "dress the whole face". Worldgen wants patches — a zone of cliffs where
    // some are climbable. The painter marks INDIVIDUAL routes, and a route with
    // holes in it is not a route.
    public static void StampClimbSurfaces(WorldState ws, WorldGenData genData,
        Func<int, int, float> coverageAt, Func<int, int, int> waterYAt,
        int minWallVoxels, bool patchy)
    {
        // Which crust each block grows, flattened to an id-indexed table once —
        // the walk below asks per voxel, and OVERLAY_NONE means "this rock grows
        // nothing", which skips it. Resolving per block rather than per zone is
        // what lets one zone's caves differ from its surface (desert sandstone
        // keeps lichen where the limestone everyone else's caves are cut from
        // takes moss) and what keeps a mantle lip matching the wall under it.
        var growthByBlock = new byte[BlockCatalog.MAX_BLOCKS];
        bool anyGrowth = false;
        BlockCatalog catalog = BlockCatalog.Active;
        for (int id = 0; id < growthByBlock.Length; id++)
        {
            BlockSurfaceData growth = catalog.ClimbGrowthFor(id);
            if (growth == null)
            {
                growthByBlock[id] = OVERLAY_NONE;
                continue;
            }
            if (growth.atlasBaseIndex <= 0)
            {
                GD.PushError($"WorldGen: climb growth surface '{growth.surfaceName}' has no atlas layer; add it to voxel_atlas_manifest.tres and rebuild.");
                growthByBlock[id] = OVERLAY_NONE;
                continue;
            }
            growthByBlock[id] = (byte)growth.atlasBaseIndex;
            anyGrowth = true;
        }
        if (!anyGrowth)
        {
            return;
        }
        int minHeight = Mathf.Max(minWallVoxels, 2);
        float yStretch = Mathf.Max(genData.climbVerticalStretch, 0.01f);

        var patchNoise = new FastNoiseLite();
        patchNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular;
        patchNoise.Seed = CLIMB_PATCH_SEED;
        patchNoise.Frequency = genData.climbCellFrequency;
        patchNoise.CellularDistanceFunction = FastNoiseLite.CellularDistanceFunctionEnum.Euclidean;
        patchNoise.CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.CellValue;
        patchNoise.CellularJitter = genData.climbCellJitter;

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        long faceRuns = 0, faceVoxels = 0, stamped = 0;
        var runStart = new int[ClimbFaces.Length];

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                float coverage = coverageAt(wx, wz);
                if (coverage <= 0f)
                {
                    continue;
                }

                // Deepest voxel this column may still mark climbable, from its
                // OWN waterline — so a lake or river bounds its walls the same
                // way the sea does.
                int climbLowestY = waterYAt(wx, wz) - genData.climbUnderwaterVoxels;

                for (int f = 0; f < runStart.Length; f++)
                {
                    runStart[f] = int.MinValue;
                }

                // One extra step past the top so a run ending at the world
                // ceiling is flushed by the same branch as any other.
                for (int wy = worldMinY; wy <= worldMaxY + 1; wy++)
                {
                    bool inRange = wy <= worldMaxY;
                    int v = inRange ? ws.GetBlockWorld(wx, wy, wz) : Blocks.AirId;
                    bool wall = inRange && Blocks.IsSolid(v) && v != Blocks.BarrierId;

                    for (int f = 0; f < ClimbFaces.Length; f++)
                    {
                        (int dx, int dz, EVoxelFace face) = ClimbFaces[f];
                        // Air exposes a face. Water does too, but only down to
                        // climbUnderwaterVoxels below the waterline: the rock a
                        // swimmer can reach is climbable, anything drowned deeper
                        // is not. IsEmpty excludes water ("non-solid but it is
                        // content"), so water has to be admitted explicitly.
                        int neighbour = ws.GetBlockWorld(wx + dx, wy, wz + dz);
                        bool exposed = wall
                            && (Blocks.IsEmpty(neighbour)
                                || (Blocks.IsWater(neighbour) && wy > climbLowestY));
                        if (exposed)
                        {
                            if (runStart[f] == int.MinValue)
                            {
                                runStart[f] = wy;
                            }
                            continue;
                        }
                        if (runStart[f] == int.MinValue)
                        {
                            continue;
                        }

                        int start = runStart[f];
                        runStart[f] = int.MinValue;
                        if (wy - start < minHeight)
                        {
                            continue;
                        }
                        faceRuns++;
                        faceVoxels += wy - start;
                        stamped += StampClimbRun(ws, genData, patchy ? patchNoise : null, coverage,
                            growthByBlock, face, wx, wz, start, wy, yStretch);
                    }
                }
            }
        }

        // The denominator is FACE-voxels: a corner column stands in two runs and
        // is counted once per face, while `stamped` counts each voxel once. So
        // the percentage reads a little under the true share of dressed rock.
        GD.Print($"WorldGen: climbable cliffs — {faceRuns} qualifying faces "
            + $"(>= {minHeight} voxels), {stamped} voxels dressed of {faceVoxels} "
            + $"face-voxels ({100.0 * stamped / Math.Max(faceVoxels, 1):0.0}%)");
    }

    // Dresses one exposed run [startY, endY). Returns how many voxels took the
    // overlay.
    private static long StampClimbRun(WorldState ws, WorldGenData genData, FastNoiseLite patchNoise,
        float coverage, byte[] growthByBlock, EVoxelFace face, int wx, int wz,
        int startY, int endY, float yStretch)
    {
        long stamped = 0;
        for (int wy = startY; wy < endY; wy++)
        {
            // Per voxel, not per run: a run is one face of one column, but a
            // cliff can change block partway up it (a limestone shoulder over
            // sandstone), and the crust has to follow the rock it grows on.
            byte climbOverlay = growthByBlock[
                Mathf.Clamp(ws.GetBlockWorld(wx, wy, wz), 0, growthByBlock.Length - 1)];
            if (climbOverlay == OVERLAY_NONE)
            {
                continue;
            }
            // Leave an authored overlay (a road tread) alone, but let our own
            // pass revisit a voxel — a corner voxel is a face on two sides and
            // has to accumulate both bits.
            int existingOverlay = ws.GetOverlayIdWorld(wx, wy, wz);
            if (existingOverlay != OVERLAY_NONE && existingOverlay != climbOverlay)
            {
                continue;
            }

            // No noise = an authored route: dress every voxel of the face. Even
            // at coverage 1 the cellular test still drops ~a fifth of them, which
            // is right for a hillside and wrong for a line someone drew.
            if (patchNoise != null)
            {
                float value = patchNoise.GetNoise3D(wx, wy * yStretch, wz) * 0.5f + 0.5f;
                if (value >= 0.5f + CLIMB_CELL_SPREAD * (coverage - 0.5f))
                {
                    continue;
                }
            }

            ws.SetOverlayIdWorld(wx, wy, wz, climbOverlay);
            // OR, never assign: the two faces of a corner are found by separate
            // runs, and the second must not erase the first.
            int faces = ws.GetOverlayFacesWorld(wx, wy, wz);
            ws.SetOverlayFacesWorld(wx, wy, wz, faces | (int)face);
            if (existingOverlay != climbOverlay)
            {
                stamped++;
            }
        }
        return stamped;
    }

    private static void StampMossPatches(WorldState ws, WorldGenData genData)
    {
        BlockSurfaceData moss = genData.mossSurface;
        if (moss == null)
        {
            return;
        }
        if (moss.atlasBaseIndex <= 0)
        {
            GD.PushError($"WorldGen: moss surface '{moss.surfaceName}' has no atlas layer; add it to voxel_atlas_manifest.tres and rebuild.");
            return;
        }
        byte mossOverlay = (byte)moss.atlasBaseIndex;

        FastNoiseLite trunkNoise = CreateMossVeinNoise(genData, MOSS_PATCH_SEED, genData.mossPatchFrequency);
        FastNoiseLite capillaryNoise = CreateMossVeinNoise(genData, MOSS_CAPILLARY_SEED,
            genData.mossPatchFrequency * Mathf.Max(genData.mossCapillaryFrequencyScale, 1f));
        // Unwarped: this one says how much moss a REGION carries, so it wants
        // to stay smooth — warping it just adds noise no one can read.
        var patchinessNoise = new FastNoiseLite();
        patchinessNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        patchinessNoise.Seed = MOSS_PATCHINESS_SEED;
        patchinessNoise.Frequency = genData.mossPatchinessFrequency;
        patchinessNoise.FractalOctaves = 2;

        float capillaryWidth = Mathf.Max(genData.mossCapillaryWidth, 0.05f);
        float yStretch = Mathf.Max(genData.mossVerticalStretch, 0.01f);

        long surfaceCandidates = 0, surfaceStamped = 0, caveCandidates = 0, caveStamped = 0;

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
                int columnZone = PickKitZone(wx, wz, genData.ZoneGens, 0);
                ZoneGenData zone = columnZone >= 0 ? genData.ZoneGens[columnZone] : null;
                if (zone == null)
                {
                    continue;
                }
                if (zone.mossSurfaceCoverage <= 0f && zone.mossCaveCoverage <= 0f)
                {
                    continue;
                }

                for (int wy = worldMinY; wy <= worldMaxY; wy++)
                {
                    var v = ws.GetBlockWorld(wx, wy, wz);
                    if (!Blocks.IsSolid(v) || v == Blocks.BarrierId)
                    {
                        continue;
                    }
                    // Leave an authored overlay (a road tread) alone.
                    if (ws.GetOverlayIdWorld(wx, wy, wz) != OVERLAY_NONE)
                    {
                        continue;
                    }
                    if (!IsAirExposed(ws, wx, wy, wz))
                    {
                        continue;
                    }

                    bool isCave = ws.Kits.IsCaveKit(ws.GetTerrainIdWorld(wx, wy, wz));
                    float coverage = isCave ? zone.mossCaveCoverage : zone.mossSurfaceCoverage;
                    if (coverage <= 0f)
                    {
                        continue;
                    }
                    // 3D noise, so a cliff face gets vertical variation instead
                    // of the whole column inheriting one 2D sample. Squashing Y
                    // stretches the strands taller than they are wide, which is
                    // what makes moss run DOWN a wall rather than around it.
                    float sy = wy * yStretch;
                    float trunk = Mathf.Abs(trunkNoise.GetNoise3D(wx, sy, wz)) * MOSS_NOISE_GAIN;
                    float capillary = Mathf.Abs(capillaryNoise.GetNoise3D(wx, sy, wz))
                        * MOSS_NOISE_GAIN / capillaryWidth;
                    float veinDist = Mathf.Min(trunk, capillary);

                    // Mean-preserving swing about 1, so raising patchiness
                    // redistributes coverage instead of adding or removing it.
                    float patch01 = Mathf.Clamp(
                        0.5f + patchinessNoise.GetNoise3D(wx, sy, wz) * MOSS_NOISE_GAIN * 0.5f, 0f, 1f);
                    float localCoverage = coverage * genData.mossStrandWidth
                        * Mathf.Lerp(1f, patch01 * 2f, genData.mossPatchinessAmount);

                    if (isCave) { caveCandidates++; } else { surfaceCandidates++; }
                    if (veinDist < localCoverage)
                    {
                        ws.SetOverlayIdWorld(wx, wy, wz, mossOverlay);
                        if (isCave) { caveStamped++; } else { surfaceStamped++; }
                    }
                }
            }
        }

        GD.Print($"WorldGen: moss surface {surfaceStamped}/{surfaceCandidates}"
            + $" ({100.0 * surfaceStamped / Math.Max(surfaceCandidates, 1):0.0}%),"
            + $" cave {caveStamped}/{caveCandidates}"
            + $" ({100.0 * caveStamped / Math.Max(caveCandidates, 1):0.0}%).");
    }

    // One strand network. The warp is applied by FastNoiseLite inside GetNoise,
    // so callers sample world position and get a crooked field for free. BOTH
    // networks warp off the TRUNK wavelength, not their own — a capillary warped
    // at its own finer scale shakes itself into specks.
    private static FastNoiseLite CreateMossVeinNoise(WorldGenData genData, int seed, float frequency)
    {
        float baseFrequency = Mathf.Max(genData.mossPatchFrequency, 1e-4f);
        var noise = new FastNoiseLite();
        noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        noise.Seed = seed;
        noise.Frequency = frequency;
        noise.FractalOctaves = 2;
        noise.DomainWarpEnabled = genData.mossWarpWavelengths > 0f;
        noise.DomainWarpType = FastNoiseLite.DomainWarpTypeEnum.Simplex;
        noise.DomainWarpAmplitude = genData.mossWarpWavelengths / baseFrequency;
        noise.DomainWarpFrequency = baseFrequency * genData.mossWarpFrequencyScale;
        // One warp application, not FastNoiseLite's default 5-octave progressive
        // one: this pass samples two networks per air-exposed voxel in the world,
        // and the extra octaves buy detail far under a voxel.
        noise.DomainWarpFractalType = FastNoiseLite.DomainWarpFractalTypeEnum.None;
        return noise;
    }

    // Solid voxel with air or water on any of its six sides — the definition of
    // "you can see this face", covering ground tops and cliff faces alike.
    private static bool IsAirExposed(WorldState ws, int wx, int wy, int wz)
    {
        return !IsSolidOpaque(ws, wx + 1, wy, wz)
            || !IsSolidOpaque(ws, wx - 1, wy, wz)
            || !IsSolidOpaque(ws, wx, wy + 1, wz)
            || !IsSolidOpaque(ws, wx, wy - 1, wz)
            || !IsSolidOpaque(ws, wx, wy, wz + 1)
            || !IsSolidOpaque(ws, wx, wy, wz - 1);
    }

    private static bool IsSolidOpaque(WorldState ws, int wx, int wy, int wz)
    {
        var v = ws.GetBlockWorld(wx, wy, wz);
        return Blocks.IsSolid(v) && v != Blocks.BarrierId;
    }

    // Stamp Dirt on "surface voxels" (solid with air directly above)
    // whose local neighborhood slope is in [EdgeMinDiff, EdgeMaxDiff-1].
    // Per-voxel, not per-column: correctly handles cave floors, overhangs, and
    // ledges because the ±EdgeScanWindow clip keeps each voxel's comparison
    // local to its own walkable layer. Currently unused (see the disabled call
    // in Generate); reads its tuning from the active WorldGenData.
    private static void StampEdgeDirt(WorldState ws)
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
                        ws.SetBlockWorld(wx, wy, wz, DIRT_BLOCK, ws.GetShapeWorld(wx, wy, wz));
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
        var self = ws.GetBlockWorld(wx, wy, wz);
        if (!Blocks.IsSolid(self) || self == Blocks.BarrierId)
        {
            return false;
        }
        var above = ws.GetBlockWorld(wx, wy + 1, wz);
        return !Blocks.IsSolid(above) || above == Blocks.BarrierId;
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
            var ground = ws.GetBlockWorld(wx, sy, wz);
            if (ground == Blocks.AirId || ground == Blocks.WaterId)
            {
                return false;
            }
            return ws.GetBlockWorld(wx, sy + 1, wz) == Blocks.AirId;
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
        TerrainKitData chunkCenterKit = ws.Kits.KitAt(ws.GetTerrainIdWorld(chunkCenterWx, chunkCenterSy, chunkCenterWz));
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
            TerrainKitData cellKit = ws.Kits.KitAt(ws.GetTerrainIdWorld(wx, sy, wz));
            WeightedScene.Fill(scenePalette, cellKit?.Trees);
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
                    TerrainKitData kit = ws.Kits.KitAt(ws.GetTerrainIdWorld(wx, sy, wz));
                    if (kit == null)
                    {
                        continue;
                    }
                    float f = forestNoise.GetNoise2D(wx * kit.ForestFrequency, wz * kit.ForestFrequency);
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
                    TerrainKitData cellKit = ws.Kits.KitAt(ws.GetTerrainIdWorld(wx, sy, wz));
                    WeightedScene.Fill(scenePalette, cellKit?.Foliage);
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
                    if (ws.GetBlockWorld(wx, wy, wz) == Blocks.WaterId
                        && ws.GetBlockWorld(wx, wy + 1, wz) != Blocks.WaterId)
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
                    if (ws.GetBlockWorld(wx, surfaceY - (MIN_WATER_DEPTH - 1), wz) != Blocks.WaterId) { continue; }

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
                    if (ws.GetBlockWorld(wx, wy, wz) == Blocks.WaterId
                        && ws.GetBlockWorld(wx, wy + 1, wz) != Blocks.WaterId)
                    {
                        if (ws.GetBlockWorld(wx, wy - (CAVE_WATER_MIN_DEPTH - 1), wz) != Blocks.WaterId)
                        {
                            continue;
                        }
                        bool roofed = false;
                        for (int c = 1; c <= CAVE_CEILING_PROBE; c++)
                        {
                            if (Blocks.IsSolid(ws.GetBlockWorld(wx, wy + c, wz)))
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

                    var below = ws.GetBlockWorld(wx, wy - 1, wz);
                    if (below == Blocks.AirId || below == Blocks.WaterId)
                    {
                        continue;
                    }
                    bool clear = true;
                    for (int c = 0; c < HEAD_CLEARANCE; c++)
                    {
                        if (ws.GetBlockWorld(wx, wy + c, wz) != Blocks.AirId)
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
                        if (ws.GetBlockWorld(wx, wy + c, wz) != Blocks.AirId)
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
