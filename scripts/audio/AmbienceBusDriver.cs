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
// Mappings come from the env-tag weights and Caveness/Openness on
// AmbienceState. Envtag weights sum to ≤1; the underflow (when corners
// are unloaded) gets treated as Outdoor so missing-data zones stay dry
// rather than going swimmy.
public static class AmbienceBusDriver
{
    private const string BUS_REVERB_SEND = "ReverbSend";
    private const int EFFECT_REVERB_INDEX = 0;
    private const int EFFECT_LOWPASS_INDEX = 1;

    // Reverb wet mix per tag. Outdoor stays nearly dry; Building is a
    // medium room; Cave is wet and boomy; Tunnel is between Cave and
    // Building (close walls, but reflective). Driven by the dominant
    // tag's wet level scaled by its weight, summed.
    private const float WET_OUTDOOR = 0.05f;
    private const float WET_BUILDING = 0.30f;
    private const float WET_CAVE = 0.55f;
    private const float WET_TUNNEL = 0.40f;

    private const float ROOM_OUTDOOR = 0.40f;
    private const float ROOM_BUILDING = 0.55f;
    private const float ROOM_CAVE = 0.85f;
    private const float ROOM_TUNNEL = 0.65f;

    // Predelay in ms. Tunnel has the longest because the first reflection
    // arrives later down a long hallway; Cave is moderate; Building short.
    private const float PREDELAY_OUTDOOR = 60f;
    private const float PREDELAY_BUILDING = 70f;
    private const float PREDELAY_CAVE = 110f;
    private const float PREDELAY_TUNNEL = 150f;

    // Damping. Caves are wet/dark so they damp less (long hangtime);
    // buildings are furniture-soft so they damp more.
    private const float DAMPING_OUTDOOR = 0.50f;
    private const float DAMPING_BUILDING = 0.70f;
    private const float DAMPING_CAVE = 0.30f;
    private const float DAMPING_TUNNEL = 0.45f;

    // Lowpass cutoff per tag. Caves and tunnels read darker; outdoor is
    // open. Fog folds in on top via FogDensity.
    private const float CUTOFF_OUTDOOR = 20000f;
    private const float CUTOFF_BUILDING = 12000f;
    private const float CUTOFF_CAVE = 5000f;
    private const float CUTOFF_TUNNEL = 6000f;

    // Fog pulls cutoff down by up to this much at FogDensity = 1.
    private const float FOG_CUTOFF_PULL_HZ = 4000f;

    public static void Apply(in AmbienceState state)
    {
        int busIdx = AudioServer.GetBusIndex(BUS_REVERB_SEND);
        if (busIdx < 0) { return; }

        var reverb = AudioServer.GetBusEffect(busIdx, EFFECT_REVERB_INDEX) as AudioEffectReverb;
        var lowpass = AudioServer.GetBusEffect(busIdx, EFFECT_LOWPASS_INDEX) as AudioEffectLowPassFilter;
        if (reverb == null || lowpass == null) { return; }

        EnvTagWeights tw = state.EnvTagWeights;
        float outdoor = tw.Outdoor;
        float building = tw.Building;
        float cave = tw.Cave;
        float tunnel = tw.Tunnel;
        float total = outdoor + building + cave + tunnel;

        // Treat any underflow (unloaded corners) as Outdoor so missing
        // data biases dry, not toward whatever happened to be in the
        // last corner the sampler reached.
        if (total < 1f)
        {
            outdoor += 1f - total;
        }

        float wet = WET_OUTDOOR * outdoor + WET_BUILDING * building + WET_CAVE * cave + WET_TUNNEL * tunnel;
        float room = ROOM_OUTDOOR * outdoor + ROOM_BUILDING * building + ROOM_CAVE * cave + ROOM_TUNNEL * tunnel;
        float predelay = PREDELAY_OUTDOOR * outdoor + PREDELAY_BUILDING * building + PREDELAY_CAVE * cave + PREDELAY_TUNNEL * tunnel;
        float damping = DAMPING_OUTDOOR * outdoor + DAMPING_BUILDING * building + DAMPING_CAVE * cave + DAMPING_TUNNEL * tunnel;
        float cutoff = CUTOFF_OUTDOOR * outdoor + CUTOFF_BUILDING * building + CUTOFF_CAVE * cave + CUTOFF_TUNNEL * tunnel;

        cutoff -= FOG_CUTOFF_PULL_HZ * state.FogDensity;
        if (cutoff < 1000f) { cutoff = 1000f; }

        reverb.Wet = wet;
        reverb.RoomSize = room;
        reverb.PredelayMsec = predelay;
        reverb.Damping = damping;
        lowpass.CutoffHz = cutoff;
    }
}
