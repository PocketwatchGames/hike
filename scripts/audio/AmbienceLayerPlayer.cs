using Godot;

// Runtime player for a single AmbienceLayerData. Owns one
// AudioStreamPlayer (non-positional), reads AmbienceState +
// time-of-day + an externally-supplied zone weight every frame, and
// translates them into volume_db / pitch_scale on its player.
//
// Created and ticked by AmbienceController. One instance per (zone,
// layer) — when the player is in a zone with the layer active, its
// zoneWeight rises to 1; when crossing into a zone without that
// layer the weight falls to 0 and the player streams silently before
// being despawned at the end of the cross-fade band.
[GlobalClass]
public partial class AmbienceLayerPlayer : Node
{
    // Layers below this linear amplitude pause their stream. Saves the
    // decode cost of streaming inaudible audio and lets the layer
    // re-attack cleanly when its source field rises again.
    private const float SILENCE_THRESHOLD = 0.002f;

    // Smoothing time-constant for volume changes. A windSpeed jump from
    // 0.2 to 0.8 should NOT produce an audible step — slew the linear
    // amplitude with this exponential time constant. Pitch is more
    // forgiving and skips smoothing.
    private const float VOLUME_SLEW_TAU = 0.25f;

    // Floor below which volume_db is clamped to -80 (effective mute).
    // Below ~-60 dB is inaudible anyway; clamping keeps the player from
    // computing log of zero.
    private const float DB_FLOOR = -80f;

    private AmbienceLayerData _data;
    private AudioStreamPlayer _player;
    private float _smoothedAmp;
    private float _zoneWeight;

    public AmbienceLayerData Data => _data;

    public void Configure(AmbienceLayerData data)
    {
        _data = data;
        _player = new AudioStreamPlayer();
        _player.Stream = data.stream;
        _player.Bus = !string.IsNullOrEmpty(data.bus) ? data.bus : "Ambience2D";
        _player.VolumeDb = DB_FLOOR;
        _player.Autoplay = false;
        AddChild(_player);

        // Start the stream paused at -inf dB; the first Tick will raise
        // it. Starting the stream ensures buffering is warm so the first
        // audible moment doesn't pop.
        if (data.stream != null)
        {
            _player.Play();
            _player.StreamPaused = true;
        }
    }

    // Called by AmbienceController each frame. weight is this layer's
    // contribution from zone blending in [0, 1]; timeOfDay01 is the
    // current world clock for the time-of-day curve.
    public void Tick(in AmbienceState state, float weight, float timeOfDay01, float deltaTime)
    {
        _zoneWeight = weight;

        if (_data == null || _player == null) { return; }

        float fieldValue = SampleField(state, _data.sourceField);
        float curveAmp = _data.volumeCurve != null
            ? _data.volumeCurve.Sample(fieldValue)
            : fieldValue;
        float todAmp = _data.timeOfDayVolume != null
            ? _data.timeOfDayVolume.Sample(timeOfDay01)
            : 1f;

        // Optional secondary gate — multiplies onto the primary curve.
        // Lets a single layer express two-input dependencies like
        // "foliage rustle = wind × foliage density".
        float gateAmp = 1f;
        if (_data.gateCurve != null)
        {
            float gateValue = SampleField(state, _data.gateField);
            gateAmp = _data.gateCurve.Sample(gateValue);
        }

        float targetAmp = curveAmp * gateAmp * todAmp * _data.volumeScale * weight;
        if (targetAmp < 0f) { targetAmp = 0f; }

        // Exponential slew on linear amplitude. dt/tau bounded into [0,1]
        // so a long frame still converges instead of overshooting.
        float alpha = deltaTime / VOLUME_SLEW_TAU;
        if (alpha > 1f) { alpha = 1f; }
        _smoothedAmp += (targetAmp - _smoothedAmp) * alpha;

        if (_smoothedAmp < SILENCE_THRESHOLD)
        {
            _player.VolumeDb = DB_FLOOR;
            if (!_player.StreamPaused) { _player.StreamPaused = true; }
            return;
        }

        if (_player.StreamPaused) { _player.StreamPaused = false; }
        _player.VolumeDb = Mathf.LinearToDb(_smoothedAmp);

        if (_data.pitchCurve != null)
        {
            float p = _data.pitchCurve.Sample(fieldValue);
            if (p < 0.05f) { p = 0.05f; }
            _player.PitchScale = p;
        }
    }

    private static float SampleField(in AmbienceState state, AmbienceField field)
    {
        switch (field)
        {
            case AmbienceField.Wetness: return state.Wetness;
            case AmbienceField.WindSpeed: return state.WindSpeed;
            case AmbienceField.FoliageDensity: return state.FoliageDensity;
            case AmbienceField.WaterDensity: return state.WaterDensity;
            case AmbienceField.ShorelineFactor: return state.ShorelineFactor;
            case AmbienceField.Openness: return state.Openness;
            case AmbienceField.Caveness: return state.Caveness;
            case AmbienceField.FogDensity: return state.FogDensity;
            case AmbienceField.Constant: return 1f;
            default: return 0f;
        }
    }
}
