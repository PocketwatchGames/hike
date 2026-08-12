using Godot;

// Tuning for the PLATEAU terrain approach (see PlateauTerrainGen).
//
// The original generator: terrain height is noise quantized to multiples of
// PlateauStep, so the world reads as tiered plateaus with cliffs between them,
// and ramps are painted back in afterwards where a low-frequency gate allows.
// Tunnels are carved as horizontal slabs at each plateau band, and caves as
// swiss-cheese holes whose ceilings snap to the next band.
//
// Every field here was previously a loose field on WorldGenData, where it sat
// alongside a second approach's knobs with nothing marking which belonged to
// which. Defaults match those values exactly, so an existing world re-authored
// onto this resource generates the same terrain.
[GlobalClass]
public partial class PlateauTerrainData : TerrainGenData
{
    [ExportGroup("Plateau Shaping")]
    // Terrain is quantized to multiples of this, in voxels. It is also the
    // vertical lattice that tunnel bands and cave ceilings snap to, which is
    // what keeps enclosed spaces at heights the camera cutaway can read.
    [Export] public float plateauStep = 4f;

    // Horizontal cells per 1 vertical voxel on a ramp skirt. With plateauStep 4,
    // slope 1 gives a 4-cell ramp rising one full step (steep but narrow).
    [Export] public int rampSlope = 1;

    // |rampGateNoise| below this marks the core of a ramp zone (thin, sparse
    // meanders). Authored at sub-0.01 magnitudes — the range hint keeps the
    // spinbox from snapping the value.
    [Export(PropertyHint.Range, "0,1,0.0001")] public float rampAnchorBand = 0.015f;

    // Half-amplitude (in plateau steps) added by the macro elevation noise.
    [Export] public float macroElevationRangePlateaus = 1f;

    // Far east of the world drops to ocean over this many chunks, down to
    // oceanDepthPlateaus below zero at the east edge.
    [Export] public int shorelineChunks = 2;
    [Export] public float oceanDepthPlateaus = 3f;

    [ExportGroup("Terrain Noise")]
    // Primary terrain height noise. Frequency sets feature scale (lower =
    // broader hills); octaves add fractal detail.
    [Export] public float terrainNoiseFrequency = 0.02f;
    [Export] public int terrainNoiseOctaves = 4;

    // Low-frequency macro elevation the per-zone terrain noise rides on top of
    // — broad continental basins / foothills independent of which zone a chunk
    // belongs to.
    [Export] public float elevationNoiseFrequency = 0.005f;
    [Export] public int elevationNoiseOctaves = 1;

    // Low-frequency ramp gate whose zero-crossings mark which plateau
    // boundaries get ramped instead of cliffed.
    [Export] public float rampGateNoiseFrequency = 0.015f;
    [Export] public int rampGateNoiseOctaves = 1;

    [ExportGroup("Caves & Tunnels")]
    // Tunnels are carved as horizontal slabs at the top of each plateau step
    // (the highest tunnelLayerHeight voxels of each band), gated by 3D tunnel
    // noise. This produces tiered tunnel systems whose ceilings line up with
    // plateau elevations and whose openings show up in cliff faces.
    [Export] public int tunnelLayerHeight = 3;
    [Export] public float tunnelNoiseFrequency = 0.025f;
    [Export] public int tunnelNoiseOctaves = 2;

    // Caves are swiss-cheese holes carved wherever |caveNoise3D| exceeds the
    // per-zone CaveThreshold. Floors follow the noise surface; ceilings snap up
    // to the next plateau-step boundary so a cave stays at least this tall and
    // can serve as a walkable path.
    [Export] public int caveMinHeight = 3;

    // Cave noise FREQUENCY is authored per-zone (ZoneGenData.caveNoiseFrequency)
    // because one shared field spans the world; only the octave count is global.
    [Export] public int caveNoiseOctaves = 2;

    public override ITerrainGenerator CreateGenerator(WorldGenData genData, int worldSeed)
    {
        return new PlateauTerrainGen(this, genData, worldSeed);
    }
}
