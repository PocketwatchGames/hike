// Canonical animation identifiers. The concrete animation clip name and
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
    Fly = 26,
    // Seated on a rideable vehicle (see IRideable / RideableData): BoatIdle is
    // the paddle-rest loop, BoatPaddle the stroke loop. Until dedicated clips
    // are authored, default_player.tres maps these to placeholder clips.
    BoatIdle = 27,
    BoatPaddle = 28,
    // Held weapon-charge poses, picked by charge tier (1 = first/light charge
    // level, 2 = heavy) crossed with locomotion (Idle = standing, Walk = moving
    // below the 75%-of-run-speed split, Run = at/above it). Generic STATE slots
    // shared by all weapons — the per-weapon CLIP is supplied through
    // WeaponData.animSet's override dict. Unarmed leaves these unmapped (you
    // can't charge a weapon with no weapon), so they only resolve through a
    // weapon override. UpdateAnimation selects the slot from runner state.
    Charge1Idle = 29,
    Charge1Walk = 30,
    Charge1Run = 31,
    Charge2Idle = 32,
    Charge2Walk = 33,
    Charge2Run = 34,
    // One-shot guard reaction, fired by PlayOneShot from the hit handler when a
    // blocked hit is soaked by a charging weapon's block-armor pool.
    Block = 35,
    // Shovel dig one-shot (consumable "Use" timeline). Clip: polysplit/anims/
    // digging.fbx, mapped per anim-set in unarmed.tres and baked into
    // human_anims.res by the PlayerAnimManifest rebuild.
    Digging = 36,
}
