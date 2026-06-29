using Godot;

// Player-facing master/music/sfx volume, applied as bus volume_db on the audio
// bus layout. The cvars (and a future settings menu) store linear 0..1; the
// linear→dB conversion lives here. "sfx" covers BOTH the 3D positional bus
// (World3D) and the 2D ambience bus (Ambience2D) — everything that isn't music.
//
// NOTE: the loading / death / bird's-eye duck routines capture and restore the
// ABSOLUTE db of World3D / Ambience2D. Changing volume_sfx mid-duck can be
// clobbered by a later restore — harmless for console use; a settings menu
// should re-apply ApplySfx after a duck ends.
public static class AudioVolume
{
    private const string BUS_MASTER = "Master";
    private const string BUS_MUSIC = "Music";
    private const string BUS_WORLD_3D = "World3D";
    private const string BUS_AMBIENCE_2D = "Ambience2D";

    // Linear at/below this reads as full silence instead of letting
    // LinearToDb run off to -inf.
    private const float SILENCE_EPSILON = 0.0001f;
    private const float SILENCE_DB = -80f;

    public static void ApplyMaster(float linear)
    {
        SetBus(BUS_MASTER, linear);
    }

    public static void ApplyMusic(float linear)
    {
        SetBus(BUS_MUSIC, linear);
    }

    public static void ApplySfx(float linear)
    {
        SetBus(BUS_WORLD_3D, linear);
        SetBus(BUS_AMBIENCE_2D, linear);
    }

    // Push all three from the current cvar values. Call once at startup after
    // the cvar config file runs — cvar callbacks don't fire on construction.
    public static void ApplyAll()
    {
        ApplyMaster(CVars.volumeMaster.Value);
        ApplyMusic(CVars.volumeMusic.Value);
        ApplySfx(CVars.volumeSfx.Value);
    }

    private static void SetBus(string busName, float linear)
    {
        int idx = AudioServer.GetBusIndex(busName);
        if (idx < 0) { return; }
        float db = linear > SILENCE_EPSILON ? Mathf.LinearToDb(linear) : SILENCE_DB;
        AudioServer.SetBusVolumeDb(idx, db);
    }
}
