using Godot;

// The discrete musical BEDS the game can be in (looping background pieces).
// MusicManager decides which are ACTIVE each frame and the cue table maps the
// active set to a stream + clip. Momentary one-shot punctuations (sunrise,
// region discovered, …) are EMusicSting, handled separately. Add a value here
// as the game grows (Victory, MiniBoss, …) and author a matching MusicCueData.
public enum EMusicState
{
    Silent,
    Menu,
    Loading,
    Explore,
    Combat,
    Death,
    // Player is resting at a campfire (camp screen open). Set/cleared by
    // CampScreen via MusicManager.SetCamping; plays the calm camp bed.
    Camp,
}

// One row of the music cue table: "while the game is in <state> (optionally
// only within biome <biomeId>), this cue offers <track>'s <clipName> at
// <priority>." More than one cue can be active at once; MusicManager plays the
// highest-priority active cue. When the winner keeps the same track but a
// different clip (e.g. Explore -> Combat within one AudioStreamInteractive),
// the manager calls SwitchToClipByName for a seamless, authored transition;
// when the track itself changes it crossfades two players.
[GlobalClass]
public partial class MusicCueData : Resource
{
    [Export] public EMusicState state = EMusicState.Explore;
    [Export] public MusicTrackData track;

    // Clip to switch to within `track` when it's an AudioStreamInteractive.
    // Empty = leave the stream on its initial/current clip (fine for a plain
    // looping stream, or when this cue shares a track and only changes biome).
    [Export] public string clipName = "";

    // Optional biome gate. -1 = any biome. When >= 0 this cue only applies
    // while the listener's biome (AmbienceState.BiomeId, = the chunk's
    // ZoneIndex) matches — lets Explore music vary per zone.
    [Export] public int biomeId = -1;

    // Tie-break when several cues are simultaneously active. Higher wins.
    // Convention: Death > Combat > Loading > Explore > Menu > Silent.
    [Export] public int priority = 0;
}
