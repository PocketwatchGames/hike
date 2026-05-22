// Canonical animation identifiers. The concrete SpriteFrames clip name and
// the slowing-affected flag are looked up per-actor through PlayerData /
// MobData `animations` dictionaries (see AnimationData) — this enum is just
// the typo-proof slot key. ItemEvent.animName and AIOutput.oneShotAnim use
// it directly; UpdateAnimation resolves it to a StringName at Play() time.
public enum EAnimation
{
    Attack,
    Dead,
    Die,
    Fall,
    Idle,
    Jump,
    Run,
    Swim,
    SwimIdle,
    Interacting,
    Stunned,
    Burrowing,
    Burrowed,
    Sneak,
    SneakIdle,
    Dash,
    Sprint,
    SwimSprint,
    Hitstun,
    Skating,
}
