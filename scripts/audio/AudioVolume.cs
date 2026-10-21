using Godot;
using System.Collections.Generic;

// Sfx buses a duck can pull down. "sfx" covers BOTH the 3D positional bus
// (World3D) and the 2D ambience bus (Ambience2D) — everything that isn't music.
[System.Flags]
public enum ESfxBus
{
    None = 0,
    World3D = 1 << 0,
    Ambience2D = 1 << 1,
    All = World3D | Ambience2D,
}

// The ONE writer of bus volume_db. A bus plays at the player's setting (the
// volume_* cvars, linear 0..1) plus every active duck on it. Nothing else may
// call AudioServer.SetBusVolumeDb for these buses, and nothing reads a bus back
// as a baseline: a fade that snapshots the live level captures whatever duck
// was stuck on it, and the next restore makes it permanent.
//
// A duck is runtime-only and never touches a cvar. Its owner clears it in
// _ExitTree, so every way out — including a failed load freeing the loading
// screen — restores the level.
public static class AudioVolume
{
    private const string BUS_MASTER = "Master";
    private const string BUS_MUSIC = "Music";
    private const string BUS_WORLD_3D = "World3D";
    private const string BUS_AMBIENCE_2D = "Ambience2D";

    // Linear at/below this reads as full silence instead of letting
    // LinearToDb run off to -inf. Also the floor a duck can pull a bus to.
    private const float SILENCE_EPSILON = 0.0001f;
    private const float SILENCE_DB = -80f;

    private static readonly Dictionary<string, float> _settingDb = new();
    private static readonly Dictionary<object, (ESfxBus buses, float db)> _ducks = new();

    public static void ApplyMaster(float linear)
    {
        SetSetting(BUS_MASTER, ESfxBus.None, linear);
    }

    public static void ApplyMusic(float linear)
    {
        SetSetting(BUS_MUSIC, ESfxBus.None, linear);
    }

    public static void ApplySfx(float linear)
    {
        SetSetting(BUS_WORLD_3D, ESfxBus.World3D, linear);
        SetSetting(BUS_AMBIENCE_2D, ESfxBus.Ambience2D, linear);
    }

    // Push all three from the current cvar values. Call once at startup after
    // the cvar config file runs — cvar callbacks don't fire on construction.
    public static void ApplyAll()
    {
        ApplyMaster(CVars.volumeMaster.Value);
        ApplyMusic(CVars.volumeMusic.Value);
        ApplySfx(CVars.volumeSfx.Value);
    }

    // Sets (or replaces) `owner`'s duck. `db` is an offset, <= 0; 0 is no duck.
    // One duck per owner — a later call replaces the earlier one's buses too.
    public static void SetDuck(object owner, ESfxBus buses, float db)
    {
        ESfxBus touched = buses;
        if (_ducks.TryGetValue(owner, out (ESfxBus buses, float db) prev))
        {
            touched |= prev.buses;
        }
        _ducks[owner] = (buses, Mathf.Min(0f, db));
        ApplySfxBuses(touched);
    }

    public static void ClearDuck(object owner)
    {
        if (_ducks.Remove(owner, out (ESfxBus buses, float db) prev))
        {
            ApplySfxBuses(prev.buses);
        }
    }

    // `self` is the bus's own duck flag; None for a bus nothing ducks.
    private static void SetSetting(string busName, ESfxBus self, float linear)
    {
        _settingDb[busName] = linear > SILENCE_EPSILON ? Mathf.LinearToDb(linear) : SILENCE_DB;
        ApplyBus(busName, self);
    }

    private static void ApplySfxBuses(ESfxBus buses)
    {
        if ((buses & ESfxBus.World3D) != 0)
        {
            ApplyBus(BUS_WORLD_3D, ESfxBus.World3D);
        }
        if ((buses & ESfxBus.Ambience2D) != 0)
        {
            ApplyBus(BUS_AMBIENCE_2D, ESfxBus.Ambience2D);
        }
    }

    private static void ApplyBus(string busName, ESfxBus self)
    {
        int idx = AudioServer.GetBusIndex(busName);
        if (idx < 0)
        {
            return;
        }
        // A bus nobody has set yet plays at the layout's authored level.
        if (!_settingDb.TryGetValue(busName, out float db))
        {
            return;
        }
        foreach ((ESfxBus buses, float duckDb) in _ducks.Values)
        {
            if ((buses & self) != 0)
            {
                db += duckDb;
            }
        }
#pragma warning disable RS0030 // the one sanctioned writer
        AudioServer.SetBusVolumeDb(idx, Mathf.Max(SILENCE_DB, db));
#pragma warning restore RS0030
    }
}
