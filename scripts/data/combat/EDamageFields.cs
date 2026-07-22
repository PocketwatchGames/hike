using System;

// Bitmask on DamageDataModifier — selects which payload fields the modifier
// touches when its trigger fires. Cleared bits mean "ignore this field on
// the modifier" so the base DamageData value flows through unchanged.
//
// Most bits replace the corresponding field on the live HitInfo. The
// AddBuildups bit is the exception: it APPENDS the modifier's buildup
// contributions to the running buildups list rather than overwriting it, since
// the typical authoring intent for crit/dizzy modifiers is "also apply bleed"
// rather than "drop the base hit's effects".
//
// Wire values are stable — append new bits, never reassign existing ones,
// so existing .tres files keep loading.
[Flags]
public enum EDamageFields
{
	None = 0,
	HealthDamage = 1 << 0,
	// Bit 1 reserved — was Stun (now expressed via DamageData.buildups).
	// Kept reserved per the wire-stability convention so legacy .tres files
	// with bit 1 set deserialize safely (the bit is now ignored).
	Hitstun = 1 << 2,
	KnockbackDistance = 1 << 3,
	KnockbackTime = 1 << 4,
	// Bit 5 reserved — was AddStatusEffects (direct on-hit status effects),
	// now unified into AddBuildups (a StatusEffectBuildup with applyImmediately).
	// Kept reserved so legacy .tres with bit 5 set deserialize safely.
	ArmorPenetration = 1 << 6,
	Blunt = 1 << 7,
	// APPENDS the modifier's buildup contributions to the running buildups list
	// (a backstab that dumps a large dizzy-buildup chunk on top of the base
	// per-hit buildup, or an immediate-apply effect on crit).
	AddBuildups = 1 << 8,
	// MULTIPLIES the live hit's healthDamage instead of replacing it — and since
	// armor chip and aggro derive from healthDamage, every kind of damage the
	// hit deals scales with it. Folds after a HealthDamage replacement when a
	// modifier authors both.
	DamageMultiplier = 1 << 9,
}
