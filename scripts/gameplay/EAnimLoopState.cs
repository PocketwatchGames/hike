// Coarse buckets used to drive a single per-actor "anim-loop" audio/particle
// effect (Player, Mob). Mirrors a subset of the loop animations picked in
// UpdateAnimation so the audio layer doesn't need to know about every
// AnimationNames StringName — only the states that actually have a loop
// effect mapped in the .tscn. None means "no loop active for this state"
// (e.g. fall, dead, interacting, swim, etc.).
public enum EAnimLoopState
{
    None,
    Idle,
    Run,
    SwimIdle,
}
