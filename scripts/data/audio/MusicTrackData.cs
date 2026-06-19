using Godot;

// One musical asset — the stream plus its mix metadata. Authored as a .tres and
// wired into a MusicManager track slot in the autoload scene; never loaded by
// path from code.
//
// `stream` is a full top-level piece (MusicManager is single-layer and crossfades
// between whole tracks). A looping AudioStream plays continuously; a non-looping
// one is manually re-played by the manager so the ambient keeps going.
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
