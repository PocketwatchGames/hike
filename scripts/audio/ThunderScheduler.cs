using System.Collections.Generic;
using Godot;

// Distant rolling-thunder scheduler. Runs as a child of Sim (spawned
// alongside AmbienceController). Every frame it samples
// AmbienceController.Current.State.LightningIntensity, looks up the
// mean inter-strike interval from ThunderSchedulerData.intervalCurve,
// and fires strikes at exponentially-jittered intervals around that
// mean.
//
// A "strike" is two events with a lag between them: the flash fires
// NOW (via LightningFlasher.Current.TriggerFlash) while the audible
// clap is queued for AudioLag seconds later — light effectively
// instantaneous, sound ~3s/km. The lag interpolates from a longer
// value at low intensity (storm is distant) to a shorter one at peak
// (storm is overhead), so the audio-visual delay narrows as the storm
// rolls in. This is what gives the "first you see the horizon flash,
// later you hear the rumble" feel rather than a mechanical fire-on-beat.
//
// Why not an AmbienceLayerData? Layers play a looping stream; the
// thunder source samples are discrete one-shot claps. Distant rolling
// thunder is naturally a sequence of irregularly-spaced rumbles, not
// a continuous bed, so the layer abstraction is the wrong shape.
//
// Asset references live on SimData.thunderScheduler (a
// ThunderSchedulerData resource). No-op when SimData has no thunder
// data wired up, so worlds without lightning silently skip this whole
// path.
[GlobalClass]
public partial class ThunderScheduler : Node
{
    public static ThunderScheduler Current { get; private set; }

    private ThunderSchedulerData _data;
    private AudioStreamPlayer[] _players;
    private int _nextPlayer;
    private double _timeUntilNext;
    private readonly RandomNumberGenerator _rng = new RandomNumberGenerator();

    // Audio claps waiting to fire. Populated when a strike is scheduled
    // (flash fires immediately, clap goes here for AudioLag seconds);
    // _Process drains it as timers expire. A small List is fine — even
    // a peak storm with ~2s mean interval and ~5s lag has at most ~3
    // pending claps at any moment.
    private struct PendingClap
    {
        public double TimeUntilFire;
        public AudioStream Stream;
        public float VolumeDb;
        public float PitchScale;
    }
    private readonly List<PendingClap> _pending = new();

    public override void _Ready()
    {
        Current = this;
        _rng.Randomize();

        WorldState ws = Sim.Current?.WorldState;
        _data = ws?.SimData?.thunderScheduler;
        if (_data == null || _data.streams == null || _data.streams.Length == 0)
        {
            // No thunder authored — leave the node in the tree but
            // effectively dormant. Lets a world add thunder data later
            // without re-spawning the node.
            return;
        }

        int n = Mathf.Max(1, _data.polyphony);
        _players = new AudioStreamPlayer[n];
        for (int i = 0; i < n; i++)
        {
            var p = new AudioStreamPlayer();
            p.Bus = !string.IsNullOrEmpty(_data.bus) ? _data.bus : "Ambience2D";
            p.Name = $"ThunderVoice{i}";
            AddChild(p);
            _players[i] = p;
        }
        // Initial wait so a freshly loaded world doesn't fire a clap
        // on frame 0. Mid-curve mean gives a sensible default.
        _timeUntilNext = SampleInterval(0.5f);
    }

    public override void _ExitTree()
    {
        if (Current == this) { Current = null; }
    }

    public override void _Process(double delta)
    {
        if (_data == null || _players == null) { return; }

        DrainPendingClaps(delta);

        // Storm GATE uses DESTINATION intensity — what we're lerping
        // TOWARD, not the in-flight crossfade value. A non-storm
        // variance lerp that incidentally passes through the lightning
        // gate has a low destination value (cur variance is in the
        // calm half), so thunder stays silent. Only when the
        // destination is an actual storm do we fire — that lets thunder
        // roll in across the approaching crossfade, then stop the
        // moment the storm's exit handover commits.
        AmbienceState s = AmbienceController.Current?.State ?? default;
        float destIntensity = s.DestinationLightningIntensity;
        float intensity = s.LightningIntensity;
        if (destIntensity <= _data.intensityFloor)
        {
            // Park the timer at a reasonable wait so we don't fire on
            // the same frame intensity crosses the floor — gives a
            // beat of silence between "no lightning" and the first clap.
            _timeUntilNext = SampleInterval(_data.intensityFloor);
            return;
        }

        _timeUntilNext -= delta;
        if (_timeUntilNext > 0.0) { return; }

        // CADENCE uses CURRENT intensity — sparse claps early in the
        // approach, building toward the destination peak. A storm
        // that's still 30 minutes from peak should sound like one
        // 30 minutes out, not like it's already overhead.
        TriggerStrike(intensity);
        _timeUntilNext = SampleInterval(intensity);
    }

    private void DrainPendingClaps(double delta)
    {
        // Walk backwards so RemoveAt during iteration stays correct.
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            PendingClap c = _pending[i];
            c.TimeUntilFire -= delta;
            if (c.TimeUntilFire <= 0.0)
            {
                PlayClap(c.Stream, c.VolumeDb, c.PitchScale);
                _pending.RemoveAt(i);
            }
            else
            {
                _pending[i] = c;
            }
        }
    }

    // Fire the visible flash NOW; queue the audible clap to play after
    // AudioLag seconds. Lag and flash amplitude both modulate with the
    // current lightning intensity, so the storm rolls in audio-visually:
    // sparse dim distant flashes with long thunder delay early, frequent
    // bright close flashes with short delay at peak.
    private void TriggerStrike(float intensity)
    {
        AudioStream stream = _data.streams[_rng.RandiRange(0, _data.streams.Length - 1)];
        if (stream == null) { return; }

        float volumeDb = _rng.RandfRange(_data.minVolumeDb, _data.maxVolumeDb);
        float pitchScale = _rng.RandfRange(_data.minPitch, _data.maxPitch);

        // Audio lag: intensity 0 → audioLagMaxSeconds (far), intensity
        // 1 → audioLagMinSeconds (close). Lerp directly, then add a
        // small random jitter inside the resolved range so consecutive
        // strikes don't telegraph the same delay.
        float lagBase = Mathf.Lerp(_data.audioLagMaxSeconds, _data.audioLagMinSeconds,
            Mathf.Clamp(intensity, 0f, 1f));
        // Jitter spreads ±20% around the base, clamped to [min, max].
        float jitter = _rng.RandfRange(0.8f, 1.2f);
        float lag = Mathf.Clamp(lagBase * jitter, _data.audioLagMinSeconds, _data.audioLagMaxSeconds);

        // Trigger the flash now. Amplitude curve is optional — default
        // to a linear 0.25 → 1.0 ramp if the author left it null so a
        // misconfigured data resource still produces visible flashes.
        float flashAmp = _data.flashAmplitudeCurve != null
            ? _data.flashAmplitudeCurve.Sample(Mathf.Clamp(intensity, 0f, 1f))
            : Mathf.Lerp(0.25f, 1f, Mathf.Clamp(intensity, 0f, 1f));
        if (_data.flashAmpJitter > 0f)
        {
            flashAmp *= _rng.RandfRange(1f - _data.flashAmpJitter, 1f);
        }
        LightningFlasher.Current?.TriggerFlash(flashAmp);

        // Queue the audible clap.
        _pending.Add(new PendingClap
        {
            TimeUntilFire = lag,
            Stream = stream,
            VolumeDb = volumeDb,
            PitchScale = pitchScale,
        });
    }

    private void PlayClap(AudioStream stream, float volumeDb, float pitchScale)
    {
        if (stream == null) { return; }
        AudioStreamPlayer p = _players[_nextPlayer];
        _nextPlayer = (_nextPlayer + 1) % _players.Length;
        p.Stream = stream;
        p.VolumeDb = volumeDb;
        p.PitchScale = pitchScale;
        p.Play();
    }

    // Exponentially-jittered interval around the mean. Exp(1) has mean
    // 1, so multiplying the mean by -ln(U) gives an exponentially
    // distributed wait time — same distribution as the gap between
    // Poisson events, which is the right shape for "claps are
    // independent events at rate 1/mean". Clamped to [min, 4*mean] so
    // a freak tiny U doesn't produce a 60-second wait at peak storm.
    private double SampleInterval(float intensity)
    {
        if (_data == null) { return 5.0; }
        float mean = _data.intervalCurve != null
            ? _data.intervalCurve.Sample(Mathf.Clamp(intensity, 0f, 1f))
            : Mathf.Lerp(_data.maxIntervalSeconds, _data.minIntervalSeconds, intensity);
        if (mean < _data.minIntervalSeconds) { mean = _data.minIntervalSeconds; }
        if (mean > _data.maxIntervalSeconds) { mean = _data.maxIntervalSeconds; }
        float u = Mathf.Max(_rng.Randf(), 1e-4f);
        float wait = mean * -Mathf.Log(u);
        float waitMax = mean * 4f;
        if (wait > waitMax) { wait = waitMax; }
        if (wait < _data.minIntervalSeconds) { wait = _data.minIntervalSeconds; }
        return wait;
    }
}
