using Godot;
using Godot.Collections;

[GlobalClass]
public partial class WorldGenData : Resource
{
    [Export] public SimData SimData;

    // Per-zone placement list. Each PlacedZone pairs a reusable ZoneGenData
    // template with the ZoneBounds describing where it goes in THIS world; the
    // index in this array becomes the ChunkState.ZoneIndex stamped on each
    // generated chunk (and the slot in WorldState.Zones[]). WorldGen assigns
    // every chunk to the highest-Priority bounds that contains it (see
    // WorldGen.PickZoneIndex) and kernel-blends the per-position scalars across
    // chunk borders for smooth transitions.
    [Export] public PlacedZone[] Zones = System.Array.Empty<PlacedZone>();

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
            if (_zoneGens == null || _zoneGens.Length != (Zones?.Length ?? 0))
            {
                int n = Zones?.Length ?? 0;
                _zoneGens = new ZoneGenData[n];
                for (int i = 0; i < n; i++)
                {
                    _zoneGens[i] = Zones[i]?.ZoneGen;
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
    [Export] public RegionGenData[] Regions = System.Array.Empty<RegionGenData>();

    // World extent (in chunks) and seed are passed as arguments to
    // WorldGen.Generate rather than authored on the data resource — they're
    // per-run knobs (a single WorldGenData template should be able to
    // generate worlds of varying size with different seeds). Per-channel
    // noise seeds (terrain, tunnel, cave, etc.) are derived from the run's
    // worldSeed inside Generate.

    // Terrain is quantized to multiples of PlateauStep so the world reads as
    // tiered plateaus with cliffs between them. Where |path noise| exceeds
    // PathThreshold the height stays smooth (creating ramps/paths between
    // plateau levels). Path columns also use VoxelType.TerrainPath so the
    // shader paints them as dirt instead of grass.
    [Export] public float PlateauStep = 4f;
    // Tunnels are carved as horizontal slabs at the bottom of each plateau
    // step (the lowest TunnelLayerHeight voxels of each step boundary), gated
    // by 3D tunnel noise. This produces tiered tunnel systems whose floors
    // line up with plateau elevations.
    [Export] public int TunnelLayerHeight = 3;
    // Caves are swiss-cheese style holes carved through terrain wherever
    // |caveNoise3D| > CaveThreshold. Floors are smooth (follow the noise
    // surface), ceilings snap up to the next plateau-step boundary so caves
    // remain at least CaveMinHeight tall and can serve as walkable paths.
    // Caves never breach the surface (no craters) and never connect to
    // tunnels via half-height openings — by construction they are >=3 tall.
    [Export] public int CaveMinHeight = 3;
    [Export] public int DirtDepth = 3;

    // Where the player spawns. X/Z are world voxel coordinates; a zone placed
    // around this point (a BoxBounds/CircleBounds whose center is the spawn
    // chunk) becomes the start area. Set this to match the start zone's bounds
    // center so the player spawns inside its fixtures.
    [Export] public Vector3 playerSpawnPosition = Vector3.Zero;
    // When true, playerSpawnPosition.Y is ignored and the spawn drops to the
    // ground surface at (X, Z) — the usual case. When false the explicit Y is
    // used as-is (e.g. to spawn on top of an authored structure / platform).
    [Export] public bool spawnAtSurface = true;

    // Hand-authored subscene stamps (cottages, dungeons, landmarks). Each
    // entry is a `.hikescene` file plus a world XZ anchor; WorldGen loads
    // and stamps them after terrain/cave/road generation but before the
    // sunlight bake. Y is picked from average surface elevation over the
    // footprint — see SubsceneStamper.ComputeSurfaceAnchor.
    [Export] public SubscenePlacement[] Subscenes = System.Array.Empty<SubscenePlacement>();

    // POI-anchored spawn placements. Each binds authored spawn content to a
    // named point of interest (resolved from ZoneData.PointsOfInterest into
    // WorldState.PointsOfInterest); WorldGen places the content at that
    // position. This is how signposts are placed now (replacing the per-region
    // random-column fixtures) and how bosses / loot / villages will be placed
    // later.
    [Export] public PoiPlacement[] PointsOfInterestPlacements = System.Array.Empty<PoiPlacement>();

    // Roads connecting named points of interest. WorldGen pathfinds and grades
    // a route per connection (see RoadConnection / WorldGen.CarveRoads).
    [Export] public RoadConnection[] Roads = System.Array.Empty<RoadConnection>();

    [ExportGroup("Player Loadout")]
    // The starting loadout/knowledge the player spawns with for this world.
    // These describe the run's scenario (a different world template can hand
    // the player different gear), so they live here rather than on the
    // character-creation picks. Player.Initialize seeds them at spawn; the
    // character's name/appearance ride in separately via CharacterCreationState.

    // Items the player tries to spawn already equipped. Each entry is added to
    // the inventory and then we attempt to move it into the matching slot:
    // armor → its armorSlot, weapons → their CanonicalSlot (melee → WeaponLeft,
    // ranged → WeaponRight). If the target slot is already taken (or the item
    // isn't equippable), the item stays in the backpack.
    [Export] public ItemCount[] equippedInventory;

    // Items added to the player's inventory and pushed into consumable hotbar
    // slots in order. Each is created at maxStack.
    [Export] public ConsumableData[] startingConsumables;

    // Items added to the player's backpack at spawn. Each entry's count is
    // split into maxStack-sized stacks. No equip / hotbar placement.
    [Export] public ItemCount[] startingInventory;

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
    // WorldGen tuning. These were `const`s inside WorldGen.cs — the feel /
    // authoring knobs the generator reads each run. Defaults match the former
    // constants exactly, so an un-edited WorldGenData generates the same world
    // as before. Stable internal identifiers (seed/hash salts, skip-flag
    // bitmasks, storage caps, the staircase pattern) stay as consts in
    // WorldGen.cs — they are not authoring knobs.
    // ─────────────────────────────────────────────────────────────────────

    [ExportGroup("Terrain Noise")]
    // Primary terrain height noise. Frequency sets feature scale (lower =
    // broader hills); octaves add fractal detail.
    [Export] public float TerrainNoiseFrequency = 0.02f;
    [Export] public int TerrainNoiseOctaves = 4;
    // Low-frequency macro elevation the per-zone terrain noise rides on top of
    // — broad continental basins / foothills independent of which zone a chunk
    // belongs to.
    [Export] public float ElevationNoiseFrequency = 0.005f;
    [Export] public int ElevationNoiseOctaves = 1;

    [ExportGroup("Cave & Tunnel Noise")]
    [Export] public float TunnelNoiseFrequency = 0.025f;
    [Export] public int TunnelNoiseOctaves = 2;
    // Cave noise frequency is authored per-zone (ZoneGenData.CaveNoiseFrequency);
    // only the fractal octave count is world-wide.
    [Export] public int CaveNoiseOctaves = 2;

    [ExportGroup("Scatter Noise")]
    [Export] public float GrassNoiseFrequency = 0.1f;
    [Export] public int GrassNoiseOctaves = 2;
    // Low-frequency ramp gate whose zero-crossings mark which plateau
    // boundaries get ramped instead of cliffed.
    [Export] public float RampGateNoiseFrequency = 0.015f;
    [Export] public int RampGateNoiseOctaves = 1;
    // Forest noise base frequency stays 1 (per-kit frequency is applied at
    // sample time by scaling input coords); only the octave count is shared.
    [Export] public int ForestNoiseOctaves = 2;

    [ExportGroup("Terrain Shaping")]
    // Horizontal cells per 1 vertical voxel on a ramp skirt. With PlateauStep=4,
    // slope 1 gives a 4-cell ramp rising one full step (steep but narrow).
    [Export] public int RampSlope = 1;
    // |pathNoise| below this marks the core of a ramp zone (thin, sparse
    // meanders). Authored at sub-0.01 magnitudes — the range hint keeps the
    // spinbox from snapping the value.
    [Export(PropertyHint.Range, "0,1,0.0001")] public float RampAnchorBand = 0.015f;
    // Half-amplitude (in plateau steps) added by the macro elevation noise.
    [Export] public float MacroElevationRangePlateaus = 1f;
    // Far east of the world drops to ocean over this many chunks, down to
    // OceanDepthPlateaus below zero at the east edge.
    [Export] public int ShorelineChunks = 2;
    [Export] public float OceanDepthPlateaus = 3f;

    [ExportGroup("Fog")]
    // Per-column "bucket capacity" at humidity = 1, in voxel-depth units.
    [Export] public float FogVolumePerHumidity = 6f;
    // Density gradient inside the bucket: density(wy) = (ceiling - wy) *
    // FogDensityPerVoxel, clamped to [0, 255].
    [Export] public float FogDensityPerVoxel = 80f;

    [ExportGroup("Zone Blending")]
    // Per-column smoothstep blend radius (in chunks) for the worldgen scalar
    // fades (elevation, density). See WorldGen.GetZoneGenWeights.
    [Export] public float ZoneGenBlendRadius = 2.0f;
    // Per-voxel kit-stamp blend radius (in chunks). Must stay >= 1.0 or corner
    // voxels fall back to a chunk-aligned hard seam. See WorldGen.PickKitZone.
    [Export] public float KitBlendRadius = 2.0f;

    [ExportGroup("Overlay Scatter")]
    [Export] public float OverlayDirtFrequency = 0.2f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float OverlayDirtThreshold = 0.9f;
    // Edge-overlay heuristic (the StampEdgeOverlays pass, currently disabled):
    // how far up/down to scan a neighbour column for its surface, and the
    // diff band that counts as a ramp/step rather than a flat or a cliff.
    [Export] public int EdgeScanWindow = 4;
    [Export] public int EdgeMinDiff = 1;
    [Export] public int EdgeMaxDiff = 3;

    [ExportGroup("Submerged Kit")]
    // Chebyshev radius for the water-adjacency search in TagSubmergedKits.
    // Must be >= 2 (see WorldGen.TagSubmergedKits).
    [Export] public int SubmergedKitRadius = 2;

    [ExportGroup("Props")]
    // XZ jitter (in voxels) applied to scattered tall-grass foliage.
    [Export] public float TallGrassJitter = 0.2f;
    // Cave-pocket spawn gate: required head clearance and how far up to probe
    // for a ceiling before a column counts as an enclosed pocket.
    [Export] public int CaveHeadClearance = 2;
    [Export] public int CaveCeilingProbe = 6;

    [ExportGroup("Placement Tuning")]
    // Max rejection-sampling attempts when rolling a random column for a
    // one-off fixture (region landmark / per-zone cluster anchor) before
    // giving up (or falling back to the target column).
    [Export] public int FixturePlacementMaxTries = 256;

    [ExportGroup("Roads")]
    // Max voxel rise per horizontal cell-step a road tolerates before the move
    // counts as climbing a cliff. Also the slope cap the ramp-grading uses: a
    // graded road never rises faster than this per cell, so it stays walkable.
    [Export(PropertyHint.Range, "1,8,1")] public int RoadMaxWalkableStep = 1;
    // Pathfinding penalty multiplier applied (scaled by the excess rise) to a
    // move that climbs faster than RoadMaxWalkableStep. High so roads detour
    // around cliffs when a gentler route exists, but still finite so a road can
    // scale one when it must (then the climb gets graded into a ramp).
    [Export] public float RoadCliffCostMultiplier = 25f;
    // Cost multiplier (<= 1) for stepping onto a column an earlier road already
    // laid. Below 1 so later roads prefer to merge onto and branch off the
    // existing network rather than run a parallel track beside it.
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float RoadReuseCostMultiplier = 0.25f;
    // Per-prop pathfinding cost added for each scatter prop (tree / tall grass)
    // in the R×R window (R = road width) around a step, so roads thread through
    // naturally open ground instead of plowing through dense props. Props the
    // road does cross are removed. 0 disables prop-aware routing.
    [Export] public float RoadPropCostMultiplier = 4f;
    // Overlay block used when a RoadConnection leaves its Texture null.
    [Export] public BlockData RoadDefaultTexture;
    // How far (meters ≈ voxels) a road holds one rolled width before re-rolling
    // a new one in [MinWidth, MaxWidth]. Each stride is a random length in this
    // range, so the tread swells and pinches organically along its length.
    [Export] public float RoadStrideMinMeters = 4f;
    [Export] public float RoadStrideMaxMeters = 20f;
    // Solid voxels guaranteed under each road tread column after all carving.
    // Tunnels (GenerateChunk) and caves (GenerateCaves) run after the road pass
    // grades the heightmap and can hollow out a road's surface, leaving the road
    // over a void; the road-overlay pass re-solidifies this many voxels down
    // from the tread so a road always bridges caves/tunnels on solid rock. >= 1.
    [Export(PropertyHint.Range, "1,8,1")] public int RoadBedDepth = 2;
}
