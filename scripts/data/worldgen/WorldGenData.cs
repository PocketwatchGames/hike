using Godot;

[GlobalClass]
public partial class WorldGenData : Resource
{
    [Export] public SimData SimData;

    // Per-zone world-gen + theme bundle. Index in this array becomes the
    // ChunkState.ZoneIndex stamped on each generated chunk; the embedded
    // ZoneData becomes WorldState.Zones[i].Data. WorldGen blends the
    // per-position scalars on each entry (ElevationMultiplier, thresholds,
    // densities) across a chunk-kernel for smooth transitions between
    // adjacent zones. WorldGen assigns chunks to zones (currently by
    // quadrant; the long-term design is arbitrary zone polygons / atlas).
    [Export] public ZoneGenData[] Zones = System.Array.Empty<ZoneGenData>();

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

    // Editor brush-palette scenes. These are NOT placed by worldgen — the
    // custom WorldEditor instantiates them when the author paints a door /
    // climbable tree / spike trap (see WorldEditor.cs). They live here because
    // there's no other shared home for the editor's prop palette yet; a
    // dedicated editor-palette resource is the eventual home.
    [Export] public PackedScene DoorScene;
    [Export] public PackedScene SpikeTrapScene;
    // Climbing one lifts the player into the bird's-eye overlook and conceals
    // them from mobs (see ClimbableTree / Player.EnterClimbableTree).
    [Export] public PackedScene ClimbableTreeScene;

    // The hub is a true zone (its own Zones[] entry, theme, and kit) carved as
    // a chunk-radius square around the spawn column, independent of the
    // quadrant split that assigns the other zones — see WorldGen.PickZoneIndex.
    // HubZoneIndex selects which Zones[] entry it is; -1 disables the hub
    // entirely (no carve, every zone anchors its fixtures on a rolled column).
    // The hub zone's ZoneGenData.Fixtures cluster anchors at SpawnColumn (the
    // near-spawn villager / companion / lit campfire / boat) rather than a
    // rolled column.
    [Export] public int HubZoneIndex = -1;
    // Chebyshev radius (in chunks) of the hub carve around the spawn chunk. 0
    // is a single chunk; 1 a 3×3 block. Only consulted when HubZoneIndex >= 0.
    [Export] public int HubRadiusChunks = 1;
    // Voxel XZ column the player spawns on / the hub zone's fixtures anchor on.
    [Export] public Vector2I SpawnColumn = new(0, 0);

    // Hand-authored subscene stamps (cottages, dungeons, landmarks). Each
    // entry is a `.hikescene` file plus a world XZ anchor; WorldGen loads
    // and stamps them after terrain/cave/road generation but before the
    // sunlight bake. Y is picked from average surface elevation over the
    // footprint — see SubsceneStamper.ComputeSurfaceAnchor.
    [Export] public SubscenePlacement[] Subscenes = System.Array.Empty<SubscenePlacement>();

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
}
