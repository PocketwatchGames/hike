// The current environmental "where am I" snapshot, rebuilt once per frame
// by AmbienceController and read by every audio consumer:
//   * global ambience layers map fields → volume/pitch (rain → wetness,
//     wind layers → windSpeed, etc.)
//   * the reverb-send bus reads envTagWeights + caveness/openness to
//     blend room size / wet-dry / lowpass cutoff
//   * positional emitter palettes use biomeId + envTagWeights + time of
//     day to gate spawning
//
// Plain struct, value-copied — consumers should snapshot it at the top
// of their tick and not hold a reference, since the controller rewrites
// in place each frame.
public struct AmbienceState
{
    // Lingering surface wetness in [0, 1]. Mirrors WorldState.WetnessLevel
    // (which integrates rainAmount + fog + humidity). Drives rain-on-X
    // layer volumes and shimmery puddle reflections.
    public float Wetness;

    // Listener-local wind in [0, 1]. Trilinearly sampled from the wind
    // subgrid — full at open sky, attenuated in caves and indoors. Drives
    // wind-layer volume and the foliage-rustle density layer.
    public float WindSpeed;

    // Rough count of foliage instances (props + detail sprites) within
    // the listener's sample radius, normalized to [0, 1] against a
    // saturation cap. Multiplied by WindSpeed at the layer to produce
    // rustle volume. Set by the density-sampling pass (step 5) — left
    // at 0 until that lands.
    public float FoliageDensity;

    // Fraction of voxels in the listener's sample radius that are water,
    // in [0, 1]. Drives the water layer's volume and (× Wetness or rain)
    // the rain-on-water layer. Set by the density-sampling pass.
    public float WaterDensity;

    // Fraction of water cells adjacent to non-water in the radius — i.e.
    // shoreline length. In [0, 1]. Drives the shoreline lap layer.
    // Set by the density-sampling pass.
    public float ShorelineFactor;

    // Listener "openness" in [0, 1]. 1 = open sky overhead, 0 = enclosed.
    // Authored env-tag Outdoor weight scaled by (1 - Enclosure) so a
    // player standing in an Outdoor cell but ducked under a tree canopy
    // hears the local geometric enclosure attenuate openness even if the
    // 4-voxel-resolution authored tag still reads Outdoor.
    public float Openness;

    // Listener "caveness" in [0, 1]. 1 = sealed natural cavity. Authored
    // Cave + Tunnel + Building weights plus the share of Outdoor weight
    // that the geometric enclosure raycast pulled into "cave-like" — so
    // overhangs and tight forest canopy in nominally-Outdoor cells still
    // get cave-style reverb response.
    public float Caveness;

    // Geometric enclosure at the listener in [0, 1]. 0 = open sky in all
    // directions, 1 = a wall within reach in every direction. Aggregate
    // of a few short raycasts; updated each frame. Stored separately
    // from Openness/Caveness so consumers that want the raw geometric
    // signal can read it without reverse-engineering the env-tag mix.
    public float Enclosure;

    // Local fog density in [0, 1]. From the active visual fog state
    // sampled at the listener. Drives a mild high-frequency damping on
    // the reverb bus and the mute-pull on distant positional emitters.
    public float FogDensity;

    // Index of the dominant zone at the listener (chunk's ZoneIndex
    // at the listener's chunk). -1 if no zone data. Used by positional
    // emitter palettes and by the time-of-day insect bed selector to pick
    // which biome's stream set is active.
    public int BiomeId;

    // Authored env-tag weights at the listener — outputs of the trilinear
    // sample on ChunkState.EnvTag. Drives reverb-bus parameter blending.
    public EnvTagWeights EnvTagWeights;
}
