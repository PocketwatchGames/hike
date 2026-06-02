using Godot;

// Per-actor authoring binding from an EAnimation slot to a concrete SpriteFrames
// clip name plus the rules for how the animator should play it. PlayerData and
// MobData each hold a Dictionary<EAnimation, AnimationData> so each actor type
// can rename its anims and opt into status-driven retiming on a per-anim basis
// (only movement loops whose underlying action is also slowed by statusMoveMul
// should be retimed; one-shots like attack / hitstun / die play at authored
// speed regardless of status).
[GlobalClass]
public partial class AnimationData : Resource
{
    // SpriteFrames clip name this slot resolves to (e.g. "run", "swim_idle").
    // Must match the animation name authored in the actor's SpriteFrames
    // resource — the animator does a HasAnimation lookup and silently skips
    // unknown names. Empty / default means the slot is unbound.
    [Export] public StringName name;

    // When true, the animator's effectSpeedMultiplier is set to the actor's
    // statusAnimMul each tick this anim is the current loop. Reserved for
    // anims whose underlying ACTION speed is also scaled by statusMoveMul
    // (run / sprint / swim / etc.) — otherwise a slowed actor would visibly
    // play their attack / hitstun / die at half speed too.
    [Export] public bool affectedBySpeedMultiplier;

    // When true, the actor's in-hand weapon model is concealed while this clip
    // is the one playing. Authored on poses that take over the hands — drinking
    // a potion, reading a scroll, casting — so the wielded weapon gives way to
    // the consumable model (or to empty hands) for the duration of the pose,
    // then pops back when the clip ends. Read by Player via HeldItemVisual; the
    // sprite-only mob path ignores it (mobs have no held visual yet).
    [Export] public bool hidesHeldItem;
}
