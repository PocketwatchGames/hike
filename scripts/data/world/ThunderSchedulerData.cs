using Godot;

// Asset + tuning data for the distant rolling-thunder scheduler. Held
// on SimData.thunderScheduler and read by the ThunderScheduler node
// each frame. Sim-wide rather than per-zone because the rolling-thunder
// bed is generic — when a future pass wants per-zone variation
// (jungle thunder vs mountain thunder), promote this to a per-zone
// reference on ZoneAmbienceData.
//
// One-shot scheduling, not a looping stream: the source samples are
// discrete claps (~5-8s each), and "distant rolling thunder" is
// naturally a sequence of irregularly-spaced far rumbles, not a
// continuous bed. Mean interval shrinks as LightningIntensity rises,
// so a stormy phase produces a near-continuous bed of overlapping
// rumbles and a fading-in phase produces sparser, more distant claps.
//
// Near-strike audio (close, sharp cracks tied to a visible lightning
// hazard) is NOT this — that's event-driven and lives on the hazard
// itself. This resource holds only the ambient distant bed.
[GlobalClass]
public partial class ThunderSchedulerData : Resource
{
    // Pool of one-shot streams. The scheduler picks uniformly from
    // this array each fire; same-clap-twice-in-a-row is allowed.
    // Should be the "far" thunder samples (sparse low rumbles) —
    // closer samples will sound wrong fired without an accompanying
    // visible bolt.
    [Export] public AudioStream[] streams = System.Array.Empty<AudioStream>();

    // Mean seconds between strikes, sampled from LightningIntensity.
    // X = lightningIntensity in [0, 1]; Y = mean interval in seconds.
    // Typical shape: ~30s at 0.05 (sparse first claps as a storm
    // approaches), ~3s at 1.0 (near-constant overlap during a full
    // electrical storm). Actual interval is exponentially jittered
    // around this mean so claps don't fire on a metronome.
    [Export] public Curve intervalCurve;

    // STORM GATE: applied to AmbienceState.DestinationLightningIntensity
    // (what lightning will settle to at the end of the current variance
    // crossfade), NOT the in-flight displayed intensity. Below this
    // floor, no claps fire — so a transient lerp through the lightning
    // gate between two non-storm variance values stays silent. Above
    // it, claps fire at a cadence driven by CURRENT intensity (sparse
    // early in an approach, dense at peak). Tune to roughly the
    // displayed intensity a "real storm" reaches in your zones —
    // simLightning = lightningMax × gate × variance, so a storm with
    // lightningMax=1 + open gates + variance~0.6 lands around 0.3.
    [Export(PropertyHint.Range, "0,0.5,0.005")] public float intensityFloor = 0.10f;

    // Hard ceiling on the mean interval — keeps a degenerate curve
    // (or low-end intensity) from waiting minutes between claps.
    [Export(PropertyHint.Range, "1,120,0.5")] public float maxIntervalSeconds = 45f;

    // Hard floor on the mean interval — at peak intensity claps still
    // need to overlap rather than fire on the same frame.
    [Export(PropertyHint.Range, "0.2,10,0.1")] public float minIntervalSeconds = 1.5f;

    // Per-strike volume range in dB. Each fire rolls a random dB from
    // [minVolumeDb, maxVolumeDb] so successive claps don't sound
    // mechanically uniform. The Ambience2D bus's master volume scales
    // the entire bed on top of these per-strike values.
    [Export(PropertyHint.Range, "-40,12,0.5")] public float minVolumeDb = -14f;
    [Export(PropertyHint.Range, "-40,12,0.5")] public float maxVolumeDb = -4f;

    // Per-strike pitch range. ±8% gives enough variation that the
    // 9-sample pool doesn't telegraph its size; outside ±15% starts
    // to sound artificial.
    [Export(PropertyHint.Range, "0.5,2,0.01")] public float minPitch = 0.92f;
    [Export(PropertyHint.Range, "0.5,2,0.01")] public float maxPitch = 1.08f;

    // Audio bus. Ambience2D matches the global ambience layers (no
    // reverb send — distant thunder shouldn't get cave reverb applied
    // when the player is in a cave; the sound is already "far" by the
    // source recording).
    [Export] public string bus = "Ambience2D";

    // Max simultaneous players. Claps can overlap (a new far rumble
    // starts while the tail of the previous one is still decaying),
    // so we need polyphony. 4 covers the realistic worst case at
    // minIntervalSeconds with ~6s-tail samples.
    [Export(PropertyHint.Range, "1,8,1")] public int polyphony = 4;

    // Per-strike audio-lag range (seconds between the visible flash and
    // the audible clap — light arrives effectively instantly, sound
    // takes ~3s per km). Sampled by intensity at fire time: low
    // intensity uses the longer end (storm is distant, strikes are
    // many km away), high intensity uses the shorter end (storm is
    // overhead, strikes are close). A per-strike random jitter inside
    // the resolved range keeps successive claps from feeling
    // metronomic.
    [Export(PropertyHint.Range, "0,8,0.05")] public float audioLagMaxSeconds = 5.0f;
    [Export(PropertyHint.Range, "0,8,0.05")] public float audioLagMinSeconds = 0.4f;

    // Flash amplitude scaled by lightning intensity at fire time.
    // X = lightningIntensity, Y = flash peak in [0, 1]. Default
    // shape (lerp 0.25 → 1.0) gives a visible-but-dim flash even on
    // the first sparse strike of an approaching storm; users who
    // want the storm-rolls-in build-up to be more dramatic can curve
    // it sharper.
    [Export] public Curve flashAmplitudeCurve;

    // Multiplicative per-strike random jitter on the flash amplitude.
    // Each strike rolls a factor in [1 - flashAmpJitter, 1] and applies
    // it to the curve sample, so two consecutive strikes at the same
    // intensity don't pulse to the same brightness.
    [Export(PropertyHint.Range, "0,0.7,0.01")] public float flashAmpJitter = 0.3f;
}
