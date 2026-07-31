using Godot;

// Per-frame consumer of AmbienceState — pushes Reverb + LowPass parameters
// to the ReverbSend bus so positional audio takes on the listener's room
// character without each emitter doing anything special. Single bus,
// parameter-tweened, rather than swapping presets.
//
// All emitters under the World3D bus get processed; emitters under
// Ambience2D bypass this entirely (those layers are envelope-only and
// shouldn't sit in a cave reverb).
//
// Every parameter comes straight off the blended InteriorAmbience on
// AmbienceState — the authored space classes around the listener, already
// trilinearly mixed. Nothing is tuned here; a room's character is authored on
// its InteriorAmbienceData .tres, so a new class of space needs no code.
public static class AmbienceBusDriver
{
    private const string BUS_REVERB_SEND = "ReverbSend";
    private const int EFFECT_REVERB_INDEX = 0;
    private const int EFFECT_LOWPASS_INDEX = 1;

    // Fog pulls cutoff down by up to this much at FogDensity = 1.
    private const float FOG_CUTOFF_PULL_HZ = 4000f;

    // Floor on the lowpass, so a maximally dark space plus heavy fog can't
    // filter positional audio down to mud.
    private const float MIN_CUTOFF_HZ = 1000f;

    // outdoorFallback absorbs the weight shortfall when sampled corners were
    // unloaded. Missing data has to resolve to SOMETHING, and dry/open is the
    // safe direction — the alternative is inheriting whichever corner loaded.
    public static void Apply(in AmbienceState state, InteriorAmbienceData outdoorFallback)
    {
        int busIdx = AudioServer.GetBusIndex(BUS_REVERB_SEND);
        if (busIdx < 0) { return; }

        var reverb = AudioServer.GetBusEffect(busIdx, EFFECT_REVERB_INDEX) as AudioEffectReverb;
        var lowpass = AudioServer.GetBusEffect(busIdx, EFFECT_LOWPASS_INDEX) as AudioEffectLowPassFilter;
        if (reverb == null || lowpass == null) { return; }

        // Spend any weight shortfall (unloaded corners) on the outdoor
        // fallback, so the parameters below are always a full unit of
        // weight and missing data biases dry rather than toward whichever
        // corner happened to be resident.
        InteriorAmbience ambience = state.Interior;
        if (ambience.TotalWeight < 1f)
        {
            ambience.Accumulate(outdoorFallback, 1f - ambience.TotalWeight);
        }
        // No palette authored at all (or nothing resident). Leave the bus on
        // whatever the scene set rather than pushing a zeroed mix — every
        // parameter here reads "dry, tiny, dark" at zero, so normalizing a
        // weightless sample would mud all positional audio worldwide.
        if (ambience.TotalWeight <= 0.001f)
        {
            return;
        }
        float norm = 1f / ambience.TotalWeight;

        float cutoff = ambience.LowpassCutoffHz * norm - FOG_CUTOFF_PULL_HZ * state.FogDensity;
        if (cutoff < MIN_CUTOFF_HZ) { cutoff = MIN_CUTOFF_HZ; }

        reverb.Wet = ambience.ReverbWet * norm;
        reverb.RoomSize = ambience.ReverbRoomSize * norm;
        reverb.PredelayMsec = ambience.ReverbPredelayMs * norm;
        reverb.Damping = ambience.ReverbDamping * norm;
        lowpass.CutoffHz = cutoff;
    }
}
