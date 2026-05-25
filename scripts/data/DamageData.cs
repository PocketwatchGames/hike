using Godot;

// Authored template for a hit. Senders construct a runtime HitInfo from
// this resource (plus their own source / hit direction) before calling
// HurtBox.Hit. Same template can be referenced from a weapon, an event,
// a trap, or a damage zone.
//
// Conditional variants (crit vs base, stun-amplified knockback, etc.) are
// expressed as entries in `modifiers` — see DamageDataModifier. Receivers
// fold matching modifiers onto the live HitInfo via HitInfo.ApplyTrigger
// when the corresponding condition is detected.
[GlobalClass]
public partial class DamageData : Resource
{
	[Export] public float healthDamage = 0f;

	// Chance (0..1) that the entire hit bypasses the receiver's armor pool
	// and lands directly on health. 0 = always absorbed by armor (the legacy
	// behavior); 1 = always bypasses. Rolled once when the HitInfo is built
	// (HitInfo.pierceRoll) so the prediction in HurtBox.QueryHitType and the
	// real apply in HurtBox.Hit always agree on whether this swing pierced.
	[Export(PropertyHint.Range, "0,1,0.01")] public float pierce = 0f;

	// Multiplier on the healthDamage chip dealt to the receiver's armor pool —
	// final armor chip is `healthDamage * (1 + blunt)`, clamped to remaining
	// armor. 0 = baseline (chip == healthDamage); 1 = doubles the chip. Has
	// no effect on the damage that bleeds through on a pierced hit. Stun's
	// armor chip is independent of this field.
	[Export] public float blunt = 0f;

	// Stun build-up added to the receiver's stun meter on hit. The receiver
	// crosses into the stunned state once the accumulated meter reaches its
	// threshold — this is the per-hit contribution, not a duration. 0 = no
	// build-up. Plumbed through HitInfo for receivers that implement stun;
	// ignored otherwise.
	[Export] public float stun = 0f;

	// Seconds of hitstun applied to the receiver — short reaction lockout
	// that triggers the hitstun anim. 0 = no hitstun. Independent of stun:
	// stun is a heavy state crossed via a meter; hitstun fires on every hit
	// that authors one and is the per-hit flinch.
	[Export] public float hitstun = 0f;

	// Magnitude of the horizontal knockback impulse, in m/s of velocity
	// change. Combined at apply time with HitInfo.hitDirection (set by the
	// sender) to form the actual impulse vector — receivers do
	// hitDirection.Normalized() * knockbackDistance and strip Y. 0 = no
	// knockback.
	[Export] public float knockbackDistance = 0f;

	// Seconds the receiver remains in the knockback state. Receivers may
	// use this to suppress input / hold the hitstun anim past the raw
	// impulse. 0 = apply impulse but no lockout window.
	[Export] public float knockbackTime = 0f;

	// Status effects to append to the receiver on hit (poison, slow, burn,
	// etc.). Each entry is added independently — receivers append a fresh
	// StatusEffectState per entry, mirroring AddStatusEffect's behavior.
	[Export] public Godot.Collections.Array<StatusEffectData> statusEffects;

	// Conditional partial-override layers. Each modifier carries a trigger
	// (OnCrit, OnStun, …) and a flag mask selecting which fields it touches;
	// the receiver folds matching modifiers onto the live HitInfo at apply
	// time. Replaces the old `critDamageData` / `knockbackDistanceOnStun`
	// fields with a single extensible list.
	[Export] public Godot.Collections.Array<DamageDataModifier> modifiers;

	// Marks this hit template as a per-frame damage tick (DamageZone with a
	// fast tickInterval, etc.). Receivers route DoT hits into a per-second
	// HUD accumulator so a burn or poison cloud emits one rolled-up floating
	// number per second instead of one per physics frame. No effect on the
	// underlying damage application — only HUD rollup.
	[Export] public bool dot = false;
}
