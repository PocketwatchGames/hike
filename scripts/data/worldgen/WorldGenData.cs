using Godot;
using Godot.Collections;

[GlobalClass]
public partial class WorldGenData : Resource
{
    [Export] public SimData simData;

    // This world's authored scripted content — quests today, scripted events
    // later. Threaded onto WorldState.ScriptData at load (GameClient.Init).
    // Separate from SimData, which is generic cross-session content. Null = no
    // scripted content in this world.
    [Export] public WorldScriptData scriptData;

    // This world's kit palette — the slot table ChunkState.TerrainId indexes.
    // Authored, and APPEND-ONLY: see KitPaletteData. It used to be derived by
    // walking `zones` and collecting each zone's four kit slots, which made the
    // .hike's wire format a side effect of zone placement.
    [Export] public KitPaletteData kitPalette;

    // Per-zone placement list. Each PlacedZone pairs a reusable ZoneGenData
    // template with the ZoneBounds describing where it goes in THIS world; the
    // index in this array becomes the ChunkState.ZoneIndex stamped on each
    // generated chunk (and the slot in WorldState.Zones[]). WorldGen assigns
    // every chunk to the highest-Priority bounds that contains it (see
    // WorldGen.PickZoneIndex) and kernel-blends the per-position scalars across
    // chunk borders for smooth transitions.
    [Export] public PlacedZone[] zones = System.Array.Empty<PlacedZone>();

    // The ZoneGenData templates of `Zones`, index-aligned, cached. The per-zone
    // worldgen passes (elevation/threshold blending, kit borders, prop palettes)
    // consume this — placement lives on the PlacedZone wrapper, the worldgen
    // scalars on the template. Rebuilt whenever the count changes (a reload
    // replaces the array, so identity check on length suffices for gen-time use).
    private ZoneGenData[] _zoneGens;
    public ZoneGenData[] ZoneGens
    {
        get
        {
            if (_zoneGens == null || _zoneGens.Length != (zones?.Length ?? 0))
            {
                int n = zones?.Length ?? 0;
                _zoneGens = new ZoneGenData[n];
                for (int i = 0; i < n; i++)
                {
                    _zoneGens[i] = zones[i]?.zoneGen;
                }
            }
            return _zoneGens;
        }
    }

    // Named-region palette. Index in this array becomes the
    // ChunkState.RegionIndex stamped on each generated chunk; the entry's
    // `.Region` becomes WorldState.Regions[i].Data. Regions are an independent
    // top-level subdivision from zones (a single named region can span
    // multiple biomes, and a single biome can host multiple regions).
    // WorldGen assigns chunks to regions (currently by quadrant, mirroring
    // the zone assignment; the long-term design is arbitrary region
    // polygons authored in the editor). Empty entries (or entries with a null
    // Region) are border chunks — ChunkState.RegionIndex still points at them
    // but the GameClient's sticky-region rules treat null Data as "no named
    // region here". Each RegionGenData also carries the region's one-off
    // Fixtures (signpost, knowledge stone) placed once within its footprint.
    [Export] public RegionGenData[] regions = System.Array.Empty<RegionGenData>();

    // The terrain approach for this world: a TerrainGenData subclass carrying
    // that approach's own knobs. THIS CHOICE SELECTS THE ALGORITHM — WorldGen
    // asks it for the generator that turns noise into the voxel grid, and
    // nothing else in worldgen knows which approach ran.
    //
    // Terrain tuning deliberately does NOT live on this resource. It used to,
    // and a second approach's fields ended up sitting beside the first's with
    // nothing marking which belonged to which; adding a third would have made
    // that worse. Put new terrain knobs on the subclass — see CLAUDE.md in this
    // folder for the recipe and the HeightMap contract.
    [Export] public TerrainGenData terrain;

    // Horizontal world extent (in chunks) and seed are passed as arguments to
    // WorldGen.Generate rather than authored on the data resource — they're
    // per-run knobs (a single WorldGenData template should be able to
    // generate worlds of varying size with different seeds). Per-channel
    // noise seeds (terrain, tunnel, cave, etc.) are derived from the run's
    // worldSeed inside Generate.
    //
    // The VERTICAL extent is not a run knob either: WorldGen fits it to the
    // heightmap it just built (WorldGen.FitVerticalExtent) from the headroom
    // values on TerrainGenData. Terrain shape therefore sets world height.

    // Where the player spawns. X/Z are world voxel coordinates; a zone placed
    // around this point (a BoxBounds/CircleBounds whose center is the spawn
    // chunk) becomes the start area. Set this to match the start zone's bounds
    // center so the player spawns inside its fixtures.
    [Export] public Vector3 playerSpawnPosition = Vector3.Zero;
    // When true, playerSpawnPosition.Y is ignored and the spawn drops to the
    // ground surface at (X, Z) — the usual case. When false the explicit Y is
    // used as-is (e.g. to spawn on top of an authored structure / platform).
    [Export] public bool spawnAtSurface = true;

    // How far from the spawn to look for the party's home campfire, which
    // starts lit (WorldGen.LightSpawnCampfire). Source-agnostic: the fire can
    // be placed by a fixture group or authored into a stamped subscene. 0 = no
    // fire is lit at the start.
    [Export(PropertyHint.Range, "0,64,0.5")] public float spawnCampfireRadius = 12f;

    // Hand-authored subscene stamps (cottages, dungeons, landmarks). Each
    // entry is a `.hikescene` file plus a world XZ anchor; WorldGen loads
    // and stamps them after terrain/cave/road generation but before the
    // sunlight bake. Y is the dominant plateau level over the footprint —
    // see TerrainMath.FootprintPlateauY.
    [Export] public SubscenePlacement[] subscenes = System.Array.Empty<SubscenePlacement>();

    // POI-anchored spawn placements. Each binds authored spawn content to a
    // named point of interest (resolved from ZoneData.PointsOfInterest into
    // WorldState.PointsOfInterest); WorldGen places the content at that
    // position. This is how signposts are placed now (replacing the per-region
    // random-column fixtures) and how bosses / loot / villages will be placed
    // later.
    [Export] public PoiPlacement[] pointsOfInterestPlacements = System.Array.Empty<PoiPlacement>();

    // Roads connecting named points of interest. WorldGen pathfinds and grades
    // a route per connection (see RoadConnection / WorldGen.CarveRoads).
    [Export] public RoadConnection[] roads = System.Array.Empty<RoadConnection>();

    // Smithing forge placed once in each non-spawn zone (see
    // WorldGen.PlaceZoneForges). The spawn zone the player starts in is skipped.
    // Null = no forges in this world.
    [Export] public ForgeSpawnEntry forge;

    // Fountains scattered across the world (see WorldGen.PlaceFountains). Each
    // lands on its own rejection-sampled flat column. A null entry or a count of
    // 0 places none of that variant. Healing = full-heal, mana = lantern refuel;
    // both are FountainSpawnEntry, differing only by the scene they carry.
    [Export] public FountainSpawnEntry healingFountain;
    [Export(PropertyHint.Range, "0,16,1,or_greater")] public int healingFountainCount;
    [Export] public FountainSpawnEntry manaFountain;
    [Export(PropertyHint.Range, "0,16,1,or_greater")] public int manaFountainCount;

    [ExportGroup("Player Party")]
    // The party the run begins with. Each PlayerState is one playable character
    // (identity + appearance + stat sheet + its own starting loadout + traits);
    // the first entry is the initially-controlled member. GameClient.Init clones
    // these templates into the runtime SimState.Party at game start. This
    // replaces the old single CharacterCreationState + the shared per-world
    // loadout (starting gear is now per-character, on PlayerState).
    [Export] public PlayerState[] startingParty = System.Array.Empty<PlayerState>();

    [ExportGroup("Player Loadout")]
    // Things the player already knows about when the run begins. Each
    // entry is a TeachableConcept subclass — ItemTeachable identifies an
    // item by name, RecipeTeachable seeds a recipe into the cookbook,
    // LanguageTeachable grants language components, RegionTeachable
    // reveals a map region, MobTeachable seeds a bestiary entry. Applied
    // via the same Teach() path that scrolls / NPC rewards use, so a
    // "starter pack" of knowledge composes the same way mid-run rewards
    // do. Announcements are suppressed during initial application (see
    // GameClient.SuppressAnnouncements) — the player shouldn't see a
    // stack of banners on the first frame.
    [Export] public Array<TeachableConcept> initialKnowledge = new();

    // ─────────────────────────────────────────────────────────────────────
    // Tuning for the APPROACH-AGNOSTIC passes — scatter, fog, roads, spawns.
    // Anything a single terrain approach reads belongs on its TerrainGenData
    // subclass instead. Stable internal identifiers (seed salts, skip-flag
    // bitmasks, storage caps) stay as consts in WorldGen.cs.
    // ─────────────────────────────────────────────────────────────────────

    [ExportGroup("Scatter Noise")]
    [Export] public float grassNoiseFrequency = 0.1f;
    [Export] public int grassNoiseOctaves = 2;
    // Forest noise base frequency stays 1 (per-kit frequency is applied at
    // sample time by scaling input coords); only the octave count is shared.
    [Export] public int forestNoiseOctaves = 2;

    [ExportGroup("Fog")]
    // Per-column "bucket capacity" at humidity = 1, in voxel-depth units.
    [Export] public float fogVolumePerHumidity = 6f;
    // Density gradient inside the bucket: density(wy) = (ceiling - wy) *
    // FogDensityPerVoxel, clamped to [0, 255].
    [Export] public float fogDensityPerVoxel = 80f;
    // NOTE: dust in sky-sealed air is no longer a worldgen knob. Enclosed air
    // is classified into a space class (see SimData.interiorAmbiences) and that
    // class's dustFloor is baked into this same fog field by
    // InteriorDustStamper — so a cave, a cellar and a roofed hut all get their
    // air from one authored place instead of three special cases.

    [ExportGroup("Zone Blending")]
    // Per-column smoothstep blend radius (in chunks) for the worldgen scalar
    // fades (elevation, density). See WorldGen.GetZoneGenWeights.
    [Export] public float zoneGenBlendRadius = 2.0f;
    // Per-voxel kit-stamp blend radius (in chunks). Must stay >= 1.0 or corner
    // voxels fall back to a chunk-aligned hard seam. See WorldGen.PickKitZone.
    [Export] public float kitBlendRadius = 2.0f;

    [ExportGroup("Moss Scatter")]
    // The surface painted as the moss overlay. Its atlasBaseIndex is the wire
    // value written into OverlayId, so this must be a surface the atlas
    // manifest actually bakes.
    [Export] public BlockSurfaceData mossSurface;
    // Spatial frequency of the TRUNK strand network. Lower = longer, lazier
    // strands wandering across a whole hillside; higher = a tighter mesh.
    //
    // There is a hard floor here that no amount of width tuning escapes: a
    // strand thinner than ONE VOXEL comes out as scattered specks instead of a
    // line. Measured on the preview, isolated-voxel share runs 2.6% at 0.02,
    // 6.6% at 0.035 and 10% at 0.055 for the same width — so make strands
    // sparse by narrowing them, and make them THIN by lowering this, never by
    // raising it.
    [Export] public float mossPatchFrequency = 0.025f;
    // Converts a zone's authored coverage into a strand half-width. Coverage
    // stays the per-zone "how mossy is this place" dial; this globally trades
    // wide ribbons for hairlines. Measured: 0.20 turns an authored 0.4 into
    // ~22% of exposed rock. Above ~0.35 the strands merge and it reads as
    // noise rather than as growth.
    [Export(PropertyHint.Range, "0.02,1,0.01")] public float mossStrandWidth = 0.2f;
    // The capillary network is the same field at a higher frequency, unioned
    // with the trunks so hairlines branch off them. Width is a FRACTION of the
    // trunk width — at 1.0 the two networks are indistinguishable and the
    // result reads as one dense mesh, which is the blobby look again. Its
    // frequency is subject to the same one-voxel floor as the trunks.
    [Export] public float mossCapillaryFrequencyScale = 1.8f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float mossCapillaryWidth = 0.4f;
    // Domain warp, in units of the strand WAVELENGTH rather than in voxels, so
    // retuning mossPatchFrequency doesn't silently change the character. This
    // is what turns clean contour lines into crooked creeping ones — but only
    // if the warp is as coarse as the strands it moves. Warping at a higher
    // frequency than the network vibrates each strand into noise instead of
    // wandering it, which is why the scale defaults to 1.
    [Export] public float mossWarpWavelengths = 0.35f;
    [Export] public float mossWarpFrequencyScale = 1.0f;
    // Vertical squash of the sample position: below 1 stretches the strands
    // taller than they are wide, so moss on a cliff face runs DOWN it like a
    // drip instead of ringing it horizontally. 1 = isotropic.
    [Export(PropertyHint.Range, "0.1,2,0.01")] public float mossVerticalStretch = 0.6f;
    // Long-wavelength modulation of coverage, so a strand thins out and dies
    // along its length instead of running forever at one width. 0 = uniform,
    // 1 = swings between bare and double coverage.
    [Export] public float mossPatchinessFrequency = 0.012f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float mossPatchinessAmount = 0.6f;

    [ExportGroup("Climbable Cliffs")]
    // WHICH surface is painted is not authored here — it comes from the rock, via
    // BlockData.climbGrowthSurface, so a cliff wears what its own block grows and
    // the mantle-lip crust the shader draws on top of it matches automatically.
    // A block growing nothing is skipped. This group only tunes WHERE and HOW
    // MUCH; ZoneGenData.climbCoverage carries the per-zone share.
    //
    // For the overlay to confer CLIMBABILITY as well as a look, the growth
    // surface must have climbable set (BlockCatalog.ValidateOrLog enforces it),
    // or the catalog needs a BlockData whose top surface is this one with
    // climbable set — ClimbProbe resolves the overlay through
    // GetByTopSurfaceLayer, the same bridge road overlays use for ground type.

    // How deep below the waterline a wall face may still be marked climbable, in
    // voxels. 0 stops the affordance at the last dry voxel; 1 lets it reach the
    // rock a swimmer can grab. Anything drowned deeper than this is not somewhere
    // to climb, so worldgen does not mark it.
    [Export(PropertyHint.Range, "0,4,1")] public int climbUnderwaterVoxels = 1;

    // Minimum unbroken height of an exposed wall face, in voxels, before any of
    // it is dressed. Measured PER FACE, so a boulder's tall north side qualifies
    // while its two-voxel east side does not. Below this a wall is a mantle, and
    // dressing it would advertise a climb that the ledge affordance already owns.
    [Export(PropertyHint.Range, "2,32,1")] public int climbMinCliffHeight = 4;
    // Cell size of the patch network. Lower = fewer, broader colonies; 0.05 puts
    // a cell at roughly 20 voxels, so a tall cliff carries two or three.
    [Export] public float climbCellFrequency = 0.05f;
    // How far a cell's feature point may wander from its lattice slot. 0 is a
    // visible grid; 1 is fully irregular, which is what makes the patches read
    // as growth rather than as tiling.
    [Export(PropertyHint.Range, "0,1,0.01")] public float climbCellJitter = 1.0f;
    // Vertical squash of the sample position, same trick as moss: below 1
    // stretches each cell taller than it is wide, so a colony hangs DOWN the
    // face instead of belting around it.
    [Export(PropertyHint.Range, "0.1,2,0.01")] public float climbVerticalStretch = 0.5f;

    [ExportGroup("Dirt Patch Scatter")]
    [Export] public float dirtPatchFrequency = 0.2f;

    // Perlin value a column must exceed to become a dirt patch. The noise is a
    // 2-octave fractal, which in practice tops out well short of 1 — the old
    // 0.9 was unreachable, so the pass produced nothing at all. Lower = more
    // dirt; around 0.35 gives scattered patches, 0.6 gives rare ones.
    [Export(PropertyHint.Range, "0,1,0.001")] public float dirtPatchThreshold = 0.45f;

    [ExportGroup("Submerged Kit")]
    // Chebyshev radius for the water-adjacency search in TagSubmergedKits.
    // Must be >= 2 (see WorldGen.TagSubmergedKits).
    [Export] public int submergedKitRadius = 2;

    [ExportGroup("Props")]
    // XZ jitter (in voxels) applied to scattered tall-grass foliage.
    [Export] public float tallGrassJitter = 0.2f;
    // Cave-pocket spawn gate: required head clearance and how far up to probe
    // for a ceiling before a column counts as an enclosed pocket.
    [Export] public int caveHeadClearance = 2;
    [Export] public int caveCeilingProbe = 6;
    // Voxels of water a flooded cave pocket must hold below its surface before
    // the CaveWaterEntities scan will place a swimmer there — anything shallower
    // is a seep, not a pool a lurker fits in.
    [Export(PropertyHint.Range, "1,8,1")] public int caveWaterMinDepth = 2;

    [ExportGroup("Placement Tuning")]
    // Max rejection-sampling attempts when rolling a random column for a
    // one-off fixture (region landmark / per-zone cluster anchor) before
    // giving up (or falling back to the target column).
    [Export] public int fixturePlacementMaxTries = 256;

    [ExportGroup("Roads")]
    // Max voxel rise per horizontal cell-step a road tolerates before the move
    // counts as climbing a cliff. Also the slope cap the ramp-grading uses: a
    // graded road never rises faster than this per cell, so it stays walkable.
    [Export(PropertyHint.Range, "1,8,1")] public int roadMaxWalkableStep = 1;
    // Pathfinding penalty multiplier applied (scaled by the excess rise) to a
    // move that climbs faster than RoadMaxWalkableStep. High so roads detour
    // around cliffs when a gentler route exists, but still finite so a road can
    // scale one when it must (then the climb gets graded into a ramp).
    [Export] public float roadCliffCostMultiplier = 25f;
    // Cost multiplier (<= 1) for stepping onto a column an earlier road already
    // laid. Below 1 so later roads prefer to merge onto and branch off the
    // existing network rather than run a parallel track beside it.
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float roadReuseCostMultiplier = 0.25f;
    // Per-prop pathfinding cost added for each scatter prop (tree / tall grass)
    // in the R×R window (R = road width) around a step, so roads thread through
    // naturally open ground instead of plowing through dense props. Props the
    // road does cross are removed. 0 disables prop-aware routing.
    [Export] public float roadPropCostMultiplier = 4f;
    // Overlay block used when a RoadConnection leaves its Texture null.
    [Export] public BlockSurfaceData roadDefaultTexture;
    // How far (meters ≈ voxels) a road holds one rolled width before re-rolling
    // a new one in [MinWidth, MaxWidth]. Each stride is a random length in this
    // range, so the tread swells and pinches organically along its length.
    [Export] public float roadStrideMinMeters = 4f;
    [Export] public float roadStrideMaxMeters = 20f;
    // Solid voxels guaranteed under each road tread column after all carving.
    // Tunnels (GenerateChunk) and caves (GenerateCaves) run after the road pass
    // grades the heightmap and can hollow out a road's surface, leaving the road
    // over a void; the road-overlay pass re-solidifies this many voxels down
    // from the tread so a road always bridges caves/tunnels on solid rock. >= 1.
    [Export(PropertyHint.Range, "1,8,1")] public int roadBedDepth = 2;

    // Deepest inland water (voxels below the surface) a road will FORD. Rivers
    // partition the island into drainage basins, so treating all water as
    // impassable silently deletes every route between two of them; a route that
    // crosses at a shallow point is graded and bedded like any other and comes
    // out as a causeway. The SEA is never fordable however shallow — a road
    // wading out into the ocean is always wrong. 0 disables fording.
    [Export(PropertyHint.Range, "0,8,1")] public int roadFordMaxDepth = 2;
    // Pathfinding cost multiplier for a ford step, so a road crosses water only
    // where a crossing genuinely beats going round. Comparable to
    // roadCliffCostMultiplier: both price a thing the road CAN do but shouldn't
    // do casually.
    [Export] public float roadFordCostMultiplier = 20f;

    [ExportGroup("Path Hints")]
    // Tread per path-hint tag for the spurs worldgen carves itself (a placement
    // with ConnectPathHints set). Searched by tag; an entry with an EMPTY tag is
    // the fallback for hints no named profile claims. Nothing here at all means
    // a spur is a 2-wide RoadDefaultTexture track.
    [Export] public PathHintProfile[] pathHintProfiles = System.Array.Empty<PathHintProfile>();
    // Radius (voxels) of the reserved-ground exemption stamped around each path
    // hint, so a route can reach a hint that sits inside its own scene's
    // footprint — which a front door always does. Routing only: the tread still
    // refuses to stamp a reserved column, so a path stops at the wall rather
    // than regrading the room behind it. Too large and a road threads THROUGH a
    // building; keep it at the wall thickness the hint is set back by.
    [Export(PropertyHint.Range, "0,8,1")] public int pathHintPortalRadius = 2;
    // Longest spur (meters ≈ voxels of route) worldgen will carve to link a hint
    // to the network. A hint whose nearest road is further than this is left
    // unconnected with a warning, rather than dragging a track across the map.
    [Export(PropertyHint.Range, "8,512,1")] public float pathHintMaxSpurMeters = 96f;

    [ExportGroup("Zone Leveling")]
    // Feature scale of the two (independent) monster / forge difficulty fields —
    // low-frequency noise partitioning the world into bands within each zone's
    // authored [LevelMin, LevelMax] span. Lower = broader bands. Shared shape;
    // the two fields differ only by seed (see WorldGen.SampleBandedLevel).
    [Export] public float zoneLevelNoiseFrequency = 0.02f;
    // How sharply the level varies across the world: the noise magnitude that
    // maps to a full sweep of a zone's band. The raw Perlin field (2-octave FBm)
    // only spans ~±0.55 (std ~0.18) and clusters near 0, so the noise is divided
    // by this and clamped before the lerp — SMALLER pushes columns toward each
    // zone's band extremes, LARGER keeps most of a zone mid-band.
    // Re-measure spread with tools/mob_level_noise_probe.gd after changing it.
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float zoneLevelNoiseSpread = 0.22f;
    // Voxels to scan straight up from a spawn before giving up on finding a
    // ceiling — a solid voxel within this window marks the spawn underground, so
    // it draws from the zone's UndergroundMobLevel band instead of the surface one.
    [Export] public int mobLevelUndergroundProbe = 24;
    // Absolute cap on monster level after the zone band and the descriptor's
    // authored base. Each level scales health/armor/damage by
    // SimData.levelScalePerLevel (~1.5x/level), so keep this small. (Forges have no
    // separate cap — they use their band
    // directly.)
    [Export(PropertyHint.Range, "0,4,1")] public int mobLevelCap = 4;
}
