using Godot;

// One musical asset — the stream plus its mix metadata. Authored as a .tres
// and referenced by MusicCueData / MusicStingData; never loaded by path from
// code.
//
// For beds, `stream` is normally an AudioStreamInteractive (Godot 4.3+): it
// holds named clips and an authored transition table (beat/bar/marker aligned,
// with fades), and MusicManager switches between its clips by name as the
// resolved cue changes — the engine does the seamless transition. A plain
// looping AudioStream also works (the manager just plays it; clip switches are
// no-ops). For stings, a short one-shot stream is fine.
[GlobalClass]
public partial class MusicTrackData : Resource
{
    [Export] public AudioStream stream;

    // Per-track trim so the cue table can balance levels without re-rendering
    // audio. Applied on top of MusicManager.masterVolumeDb. 0 dB plays the
    // asset at its authored level.
    [Export(PropertyHint.Range, "-40,6,0.5")] public float volumeDb = 0f;

    // Author-facing label for the music console / debug readout. Not shown to
    // the player, so it isn't localized.
    [Export] public string displayName = "";
}
