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

	// Seconds of stun applied to the receiver. 0 = no stun. Plumbed through
	// HitInfo for receivers that implement stun; ignored otherwise.
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
}
