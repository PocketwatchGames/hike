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
    // ChunkState.RegionIndex stamped on each generated chunk; the entry
    // becomes WorldState.Regions[i].Data. Regions are an independent
    // top-level subdivision from zones (a single named region can span
    // multiple biomes, and a single biome can host multiple regions).
    // WorldGen assigns chunks to regions (currently by quadrant, mirroring
    // the zone assignment; the long-term design is arbitrary region
    // polygons authored in the editor). Empty entries are border chunks —
    // ChunkState.RegionIndex still points at them but the GameClient's
    // sticky-region rules treat null Data as "no named region here".
    [Export] public RegionData[] Regions = System.Array.Empty<RegionData>();

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

    // Door / signpost / spike-trap scenes still live world-wide because they
    // aren't placed via the per-zone SpawnListData scan. Doors are placed by
    // the room-shell pass; signposts are one-per-quadrant; spike traps are
    // editor-placed. Torch / campfire / chest / poison-chest scenes used to
    // live here too — they moved into per-zone CaveEntities/SurfaceEntities
    // (TorchSpawnEntry, CampfireSpawnEntry, ChestSpawnEntry).
    [Export] public PackedScene DoorScene;
    [Export] public PackedScene SpikeTrapScene;
    [Export] public PackedScene SignpostScene;

    // One signpost is placed per quadrant on a random grassy column. Index
    // matches PickZoneIndex's quadrant order: 0=NE (X>=0, Z>=0),
    // 1=NW (X<0, Z>=0), 2=SE (X>=0, Z<0), 3=SW (X<0, Z<0). Empty entries
    // (or quadrants with no grassy candidate) skip placement for that
    // quadrant. SignpostScene must be set; otherwise the whole pass is a
    // no-op.
    [Export(PropertyHint.MultilineText)] public string SignpostTextNE = "";
    [Export(PropertyHint.MultilineText)] public string SignpostTextNW = "";
    [Export(PropertyHint.MultilineText)] public string SignpostTextSE = "";
    [Export(PropertyHint.MultilineText)] public string SignpostTextSW = "";
    [Export] public LanguageData SignpostLanguage;

    // Single test-fixture KnowledgeStone placed near the default player spawn
    // by WorldGen — the player walks east to the villager and west to the
    // stone. Temporary scaffolding for the language-learning system; folded
    // into a real placement pass once authored stone spawn rules exist.
    [Export] public PackedScene KnowledgeStoneScene;
    [Export] public LanguageData KnowledgeStoneLanguage;
    [Export(PropertyHint.MultilineText)] public string KnowledgeStoneText = "";
}
