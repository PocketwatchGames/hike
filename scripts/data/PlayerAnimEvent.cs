using Godot;

// One authored event baked onto a clip when PlayerAnimManifest rebuilds — a
// Call Method Track key inserted into human_anims.res. The manifest (a text
// .tres, version-controlled and diffable) is the source of truth so events
// SURVIVE re-importing the source FBX: the rebuild re-applies them every time,
// instead of the events living only in the binary .res where a rebuild / FBX
// re-import would silently wipe them.
//
// Generalises the footstep cue (Method = "EmitFootstep") to any per-frame
// animation event (attack-contact frame, sound, vfx) — author the method name
// + time once on the clip row and every rebuild re-bakes it.
[Tool]
[GlobalClass]
public partial class PlayerAnimEvent : Resource
{
    // Position in the clip as a fraction of its length, 0..1. Stored NORMALIZED
    // (not absolute seconds) so it stays put when the source FBX is re-imported
    // with a slightly different length and is unaffected by the clip's Speed
    // bake — the rebuild multiplies it by the final clip length. Range hint set
    // fine enough to dodge the sub-0.01 default-step snapping trap.
    [Export(PropertyHint.Range, "0,1,0.0001")]
    public float NormalizedTime;

    // Method invoked on the rebuild's MethodTrackTarget node (the rig's
    // ModelAnimator) at NormalizedTime — e.g. "EmitFootstep". Empty = skipped.
    [Export] public StringName Method = "";

    // Optional arguments forwarded in the method-track key. Empty for parameter-
    // less cues like EmitFootstep.
    [Export] public Godot.Collections.Array Args = new();
}
