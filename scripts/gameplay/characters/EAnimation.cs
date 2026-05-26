// Canonical animation identifiers. The concrete SpriteFrames clip name and
// the slowing-affected flag are looked up per-actor through PlayerData /
// MobData `animations` dictionaries (see AnimationData) — this enum is just
// the typo-proof slot key. ItemEvent.animName and AIOutput.oneShotAnim use
// it directly; UpdateAnimation resolves it to a StringName at Play() time.
//
// Wire values are stable — the animations dictionary on each mob .tres is
// keyed by integer position, so renames are safe but reorders are not.
// `None` is a sentinel at -1 so a missing-field default on
// StatusEffectData.loopAnimOverride doesn't collide with Attack (= 0).
public enum EAnimation
{
    None = -1,
    Attack = 0,
    Dead = 1,
    Die = 2,
    Fall = 3,
    Idle = 4,
    Jump = 5,
    Run = 6,
    Swim = 7,
    SwimIdle = 8,
    Interacting = 9,
    Dizzy = 10,
    Burrowing = 11,
    Burrowed = 12,
    Sneak = 13,
    SneakIdle = 14,
    Dash = 15,
    Sprint = 16,
    SwimSprint = 17,
    Hitstun = 18,
    Skating = 19,
    Attack2 = 20,
    Drinking = 21,
    Eating = 22,
    Reading = 23,
    Casting = 24,
    Using = 25,
}
