using Godot;

[GlobalClass]
public partial class WorldGenData : Resource
{
    [Export] public SimData SimData;

    // Per-region world-gen + theme bundle. Index in this array becomes the
    // ChunkState.RegionIndex stamped on each generated chunk; the embedded
    // RegionData becomes WorldState.Regions[i].Data. WorldGen blends the
    // per-position scalars on each entry (ElevationMultiplier, thresholds,
    // densities) across a chunk-kernel for smooth transitions between
    // adjacent regions. WorldGen assigns chunks to regions (currently by
    // quadrant; the long-term design is arbitrary region polygons / atlas).
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

    // Per cave-floor spawn roll. Caves are dim, so a low rate still produces
    // visibly lit pockets without flooding every tunnel with torches.
    [Export] public float CaveTorchChance = 0.0025f;
    [Export] public PackedScene DoorScene;
    [Export] public PackedScene TorchScene;
    [Export] public PackedScene CampfireScene;
    [Export] public PackedScene SpikeTrapScene;
}
