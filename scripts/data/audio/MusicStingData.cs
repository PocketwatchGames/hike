using Godot;

// Momentary musical punctuations — fired once on an event, played one-shot
// over whatever bed is currently running (not a state change). Unlike beds,
// a sting has no priority or biome gate: the triggering event names it and
// MusicManager plays it on a dedicated one-shot player.
public enum EMusicSting
{
    Sunrise,
    Sunset,
    Night,
    RegionDiscovered,
    // Combat ended by killing the last threat (a "victory" flourish) — distinct
    // from combat simply fading out when the player runs away.
    CombatVictory,
    PlayerDeath,
}

// Maps one EMusicSting to the track that voices it. `track.stream` is normally
// a short non-looping asset. Authored as .tres and wired into
// MusicManager.stings.
[GlobalClass]
public partial class MusicStingData : Resource
{
    [Export] public EMusicSting sting = EMusicSting.Sunrise;
    [Export] public MusicTrackData track;
}
