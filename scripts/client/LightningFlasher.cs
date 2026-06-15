using System.Collections.Generic;
using Godot;

// Centralized 0..1 lightning flash intensity. ThunderScheduler calls
// TriggerFlash(amplitude) the moment it picks a clap to play; that flash
// fires NOW while the audible clap is queued a couple of seconds later
// (light arrives before sound). Each flash adds a flicker envelope —
// 2-4 quick decaying spikes packed into ~300ms — to capture the
// staccato look of a real strike rather than a single boxy flash.
//
// SkyController samples Current.Intensity once per frame and uses it to
// (a) blank cloud-shadow attenuation on the terrain so the flash reaches
// the ground even where a cloud was darkening it, and (b) boost the
// active body's DirectionalLight3D energy. The combination lets a
// player standing under a cloud shadow see the world brighten on every
// strike, which was the user-requested behavior.
//
// Lives as a sibling of ThunderScheduler / AmbienceController under
// World; persists across the whole run. Dormant (Intensity = 0) when
// no flashes are active, which is the default state.
[GlobalClass]
public partial class LightningFlasher : Node
{
    public static LightningFlasher Current { get; private set; }

    // Current summed flicker intensity in [0, 1]. SkyController reads
    // this every frame; produced as the max of all active envelopes
    // (max, not sum — two simultaneous flashes shouldn't push past 1.0
    // and produce a washed-out clamp).
    public float Intensity { get; private set; }

    // One active flash. Spikes are evaluated parametrically each frame
    // rather than stored as a time-series, so an envelope is just its
    // start time and a small set of spike params.
    private struct Envelope
    {
        public double StartTimeSec;       // _accumTime when TriggerFlash was called
        public float Amplitude;           // peak intensity in [0, 1]
        public float DurationSec;         // total envelope length; intensity = 0 past this
        public float Spike0OffsetSec;
        public float Spike1OffsetSec;
        public float Spike2OffsetSec;
        public float Spike0Amp;
        public float Spike1Amp;
        public float Spike2Amp;
    }

    private readonly List<Envelope> _envelopes = new();
    private double _accumTime;
    private readonly RandomNumberGenerator _rng = new RandomNumberGenerator();

    // Each individual spike decays exponentially with this time constant
    // (seconds). ~70ms gives a perceptibly-sharp flash that doesn't
    // smear into a dim glow — matches the visual impression of a real
    // strike's stepped leader / return stroke pulse.
    private const float SPIKE_DECAY_TAU = 0.07f;

    public override void _Ready()
    {
        Current = this;
        _rng.Randomize();
    }

    public override void _ExitTree()
    {
        if (Current == this) { Current = null; }
    }

    public override void _Process(double delta)
    {
        _accumTime += delta;
        if (_envelopes.Count == 0) { Intensity = 0f; return; }

        float maxI = 0f;
        for (int i = _envelopes.Count - 1; i >= 0; i--)
        {
            Envelope e = _envelopes[i];
            float t = (float)(_accumTime - e.StartTimeSec);
            if (t > e.DurationSec)
            {
                _envelopes.RemoveAt(i);
                continue;
            }
            float val = SampleSpike(t - e.Spike0OffsetSec, e.Spike0Amp)
                      + SampleSpike(t - e.Spike1OffsetSec, e.Spike1Amp)
                      + SampleSpike(t - e.Spike2OffsetSec, e.Spike2Amp);
            val *= e.Amplitude;
            if (val > maxI) { maxI = val; }
        }
        if (maxI > 1f) { maxI = 1f; }
        Intensity = maxI;
    }

    // Exponential decay starting at t=0, zero for negative t. The
    // negative-t cutoff is what spaces the spikes in time: spike 1
    // doesn't contribute until its offset has elapsed.
    private static float SampleSpike(float t, float amp)
    {
        if (t < 0f || amp <= 0f) { return 0f; }
        return amp * Mathf.Exp(-t / SPIKE_DECAY_TAU);
    }

    // amplitude is the FLASH PEAK in [0, 1]. ThunderScheduler scales
    // this by lightning intensity + a per-strike random factor so
    // distant approaching strikes are dim and a full storm's peak is
    // near full white.
    public void TriggerFlash(float amplitude)
    {
        if (amplitude <= 0f) { return; }
        if (amplitude > 1f) { amplitude = 1f; }

        // 1-3 staccato spikes per strike. The first is always strongest;
        // the follow-ups decay in amplitude and shift in time. Real
        // strikes have a return stroke + dart leaders + sometimes a
        // continuing current — this is a stylized approximation, not a
        // physical simulation.
        var e = new Envelope
        {
            StartTimeSec = _accumTime,
            Amplitude = amplitude,
            Spike0OffsetSec = 0f,
            Spike0Amp = 1.0f,
            Spike1OffsetSec = _rng.RandfRange(0.04f, 0.10f),
            Spike1Amp = _rng.RandfRange(0.4f, 0.7f),
            Spike2OffsetSec = _rng.RandfRange(0.14f, 0.24f),
            Spike2Amp = _rng.Randf() < 0.6f ? _rng.RandfRange(0.2f, 0.45f) : 0f,
        };
        // Total envelope length: last spike's start + ~5 tau decay tail
        // (after which contribution is < 0.7% of peak — well below the
        // intensity floor anyone will see).
        float lastSpike = Mathf.Max(e.Spike2OffsetSec, e.Spike1OffsetSec);
        e.DurationSec = lastSpike + 5f * SPIKE_DECAY_TAU;
        _envelopes.Add(e);
    }
}
