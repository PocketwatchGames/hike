using System;

// Bitmask on DamageDataModifier — selects which payload fields the modifier
// touches when its trigger fires. Cleared bits mean "ignore this field on
// the modifier" so the base DamageData value flows through unchanged.
//
// Most bits replace the corresponding field on the live HitInfo. The
// AddStatusEffects bit is the exception: it APPENDS the modifier's array
// to the running statusEffects list rather than overwriting it, since the
// typical authoring intent for crit/stun modifiers is "also apply bleed"
// rather than "drop the base hit's status effects".
//
// Wire values are stable — append new bits, never reassign existing ones,
// so existing .tres files keep loading.
[Flags]
public enum EDamageFields
{
	None = 0,
	HealthDamage = 1 << 0,
	Stun = 1 << 1,
	Hitstun = 1 << 2,
	KnockbackDistance = 1 << 3,
	KnockbackTime = 1 << 4,
	AddStatusEffects = 1 << 5,
}
