using Godot;

// One class of space — what the air, wind and acoustics are like inside it.
// Referenced from SimData.interiorAmbiences; a cell of ChunkState.EnvTag
// stores that list's INDEX, so which entry applies at a point is authored
// per 4³-voxel cell rather than derived from the geometry.
//
// The list is flat and the names are labels, not a schema: "building_dusty_large"
// is one entry, NOT building × dusty × large composed at runtime. Adding a damp
// cellar or a dripping cave is a new .tres appended to the list, not an enum
// member and a switch case. Cross-product naming is a human convention for
// keeping the palette browsable; nothing parses it.
//
// Index 0 is the OUTDOOR baseline despite the type name — one uniform table
// with no null case is worth the slightly-off name, since every consumer then
// blends the same eight fields regardless of where the listener stands.
//
// Values BLEND: a sample takes the eight surrounding cells and accumulates each
// field weighted by trilinear distance, so crossing a threshold crossfades
// instead of snapping. Anything that can't blend (a sound-emitter set, say)
// does not belong here — it needs a dominant-cell rule instead.
[GlobalClass]
public partial class InteriorAmbienceData : Resource
{
    // Palette label, shown in the editor's env-tag brush. Not localized —
    // this is an authoring-side name, never shown to a player.
    [Export] public string displayName = "";

    // Airborne dust held in this class of space, as a floor on the per-voxel
    // fog the raymarcher reads (a MAX, never a reduction — it must not thin
    // authored mist that happens to sit here). Shafts are only visible where
    // there is something for them to light, so an enclosed space with zero
    // dust gets no god rays however many holes are above it.
    //
    // Fills the cell's own AIR voxels; unlike a roof there is no depth knob,
    // because a cell already knows the volume it covers.
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustFloor;

    // How much this space seals out ambient wind. 0 = wind passes freely
    // (outdoors), 1 = dead calm. Applied as a multiplier on the sampled wind
    // factor, so it damps both the audio wind bed and mote drift without
    // touching the authored per-cell WindFactor channel.
    [Export(PropertyHint.Range, "0,1,0.01")] public float windSuppression;

    // Sky overhead, in [0, 1]. 1 = open, 0 = fully enclosed. Feeds
    // AmbienceState.Openness / Caveness, which gate the outdoor ambience
    // layers. Separate from windSuppression because a deep porch is open to
    // the sky's sound while being sheltered from its wind.
    [Export(PropertyHint.Range, "0,1,0.01")] public float openness;

    // --- Reverb ------------------------------------------------------------
    // Pushed to the ReverbSend bus by AmbienceBusDriver after blending. These
    // were per-tag constants in that file; they live here so a new class of
    // space is authored rather than compiled, and so one bundle can be reused
    // by both a painted cell and (later) an authored volume.

    // Wet mix. Outdoors stays nearly dry; a large cavity is wet and boomy.
    [Export(PropertyHint.Range, "0,1,0.01")] public float reverbWet = 0.05f;

    [Export(PropertyHint.Range, "0,1,0.01")] public float reverbRoomSize = 0.4f;

    // Predelay in ms — how long the first reflection takes to come back.
    // Longest in a corridor, where the far wall is a long way down.
    [Export(PropertyHint.Range, "0,250,1")] public float reverbPredelayMs = 60f;

    // High-frequency absorption. Soft-furnished rooms damp MORE; bare wet
    // rock damps less and rings longer.
    [Export(PropertyHint.Range, "0,1,0.01")] public float reverbDamping = 0.5f;

    // Lowpass cutoff in Hz. Enclosed stone reads darker. Local fog pulls this
    // down further on top (see AmbienceBusDriver).
    [Export(PropertyHint.Range, "500,20000,50")] public float lowpassCutoffHz = 20000f;
}
