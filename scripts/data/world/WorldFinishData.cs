using Godot;


// Tuning for the passes a FINISHED world derives from its own voxels — the moss
// and climb overlays, the fog fill — plus the two scalars the spawn passes read.
// Everything WorldFinish.Finish needs and nothing about generating terrain, so
// both producers can hold one: WorldGen and the map painter's bake run the same
// pass list and must tune it the same way.
//
// These lived as loose fields on WorldGenData, which made a painted world depend
// on the generator's authoring resource for values that have nothing to do with
// generation. Pulling them out is what lets a painted world stand alone.
[GlobalClass]
public partial class WorldFinishData : Resource
{
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

    // Long-wavelength modulation of coverage, so a strand thins out and dies
    // along its length instead of running forever at one width. 0 = uniform,
    // 1 = swings between bare and double coverage.
    [Export] public float mossPatchinessFrequency = 0.012f;

    [Export(PropertyHint.Range, "0,1,0.01")] public float mossPatchinessAmount = 0.6f;

    // Converts a zone's authored coverage into a strand half-width. Coverage
    // stays the per-zone "how mossy is this place" dial; this globally trades
    // wide ribbons for hairlines. Measured: 0.20 turns an authored 0.4 into
    // ~22% of exposed rock. Above ~0.35 the strands merge and it reads as
    // noise rather than as growth.
    [Export(PropertyHint.Range, "0.02,1,0.01")] public float mossStrandWidth = 0.2f;

    [Export(PropertyHint.Range, "0.05,1,0.01")] public float mossCapillaryWidth = 0.4f;

    // The capillary network is the same field at a higher frequency, unioned
    // with the trunks so hairlines branch off them. Width is a FRACTION of the
    // trunk width — at 1.0 the two networks are indistinguishable and the
    // result reads as one dense mesh, which is the blobby look again. Its
    // frequency is subject to the same one-voxel floor as the trunks.
    [Export] public float mossCapillaryFrequencyScale = 1.8f;

    // Vertical squash of the sample position: below 1 stretches the strands
    // taller than they are wide, so moss on a cliff face runs DOWN it like a
    // drip instead of ringing it horizontally. 1 = isotropic.
    [Export(PropertyHint.Range, "0.1,2,0.01")] public float mossVerticalStretch = 0.6f;

    // Domain warp, in units of the strand WAVELENGTH rather than in voxels, so
    // retuning mossPatchFrequency doesn't silently change the character. This
    // is what turns clean contour lines into crooked creeping ones — but only
    // if the warp is as coarse as the strands it moves. Warping at a higher
    // frequency than the network vibrates each strand into noise instead of
    // wandering it, which is why the scale defaults to 1.
    [Export] public float mossWarpWavelengths = 0.35f;

    [Export] public float mossWarpFrequencyScale = 1.0f;

    // The surface painted as the moss overlay. Its atlasBaseIndex is the wire
    // value written into OverlayId, so this must be a surface the atlas
    // manifest actually bakes.
    [Export] public BlockSurfaceData mossSurface;

    // Cell size of the patch network. Lower = fewer, broader colonies; 0.05 puts
    // a cell at roughly 20 voxels, so a tall cliff carries two or three.
    [Export] public float climbCellFrequency = 0.05f;

    // How far a cell's feature point may wander from its lattice slot. 0 is a
    // visible grid; 1 is fully irregular, which is what makes the patches read
    // as growth rather than as tiling.
    [Export(PropertyHint.Range, "0,1,0.01")] public float climbCellJitter = 1.0f;

    // How deep below the waterline a wall face may still be marked climbable, in
    // voxels. 0 stops the affordance at the last dry voxel; 1 lets it reach the
    // rock a swimmer can grab. Anything drowned deeper than this is not somewhere
    // to climb, so worldgen does not mark it.
    [Export(PropertyHint.Range, "0,4,1")] public int climbUnderwaterVoxels = 1;

    // Vertical squash of the sample position, same trick as moss: below 1
    // stretches each cell taller than it is wide, so a colony hangs DOWN the
    // face instead of belting around it.
    [Export(PropertyHint.Range, "0.1,2,0.01")] public float climbVerticalStretch = 0.5f;

    // NOTE: dust in sky-sealed air is not a knob here. Enclosed air is
    // classified into a space class (see SimData.interiorAmbiences) and that
    // class's dustFloor is baked into this same fog field by
    // InteriorDustStamper — so a cave, a cellar and a roofed hut all get their
    // air from one authored place instead of three special cases.

    // Largest height difference between horizontally-adjacent columns still
    // treated as a GRADE (a staircase approximation of a slope, meshed smooth)
    // rather than a real discontinuity (meshed crisp as a wall). StampGradeShapes
    // re-derives the shape channel with it from the finished voxels, which is why
    // it lives here and not on TerrainGenData: BOTH producers need it and neither
    // is asking a question about a terrain approach. A painted document has no
    // approach at all, and borrowing one approach's value was the last thing
    // keeping it tied to a WorldGenData.
    //
    // Raise only if something authors grades steeper than this per column — they
    // would otherwise harden into visible stairs. At 1 a one-voxel step is a 45
    // degree ramp and a two-voxel step is a wall.
    [Export(PropertyHint.Range, "1,8,1")] public int maxGradeStep = 1;

    // Density gradient inside the bucket: density(wy) = (ceiling - wy) *
    // FogDensityPerVoxel, clamped to [0, 255].
    [Export] public float fogDensityPerVoxel = 80f;

    // Per-column "bucket capacity" at humidity = 1, in voxel-depth units.
    [Export] public float fogVolumePerHumidity = 6f;

    // Per-column smoothstep blend radius (in chunks) for the worldgen scalar
    // fades (elevation, density). See WorldGen.GetZoneGenWeights.
    [Export] public float zoneGenBlendRadius = 2.0f;

    // Absolute cap on monster level after the zone band and the descriptor's
    // authored base. Each level scales health/armor/damage by
    // SimData.levelScalePerLevel (~1.5x/level), so keep this small. (Forges have no
    // separate cap — they use their band
    // directly.)
    [Export(PropertyHint.Range, "0,4,1")] public int mobLevelCap = 4;
}
