using System;

// Unified stat-modifier key. Used by:
//  - StatModifier.stat (single bit) on inherent / equipment / status-effect
//    modifier lists
//  - DamageData.tags / ContinuousDamageData.tags (multi-bit OR mask) declaring
//    the hit's nature
//  - StatusEffectData.tags (multi-bit OR mask) declaring the effect's nature
//    for buildup / DoT scaling
//
// Each value is a single bit so hit-side fields can OR multiple tags into one
// mask (a sword swing tagged Damage|Melee|Blunt, a fireball tagged
// Damage|Fire|Magical|Ranged) while modifier entries set exactly one bit.
// Composition is per-stat — the receiver knows whether each stat is
// multiplicative (most) or additive (Camouflage, MaxStamina, ColdResist,
// HeatResist) — and at which gameplay site each one applies (damage scale,
// armor bypass chance, knockback magnitude, movement speed, sense
// multipliers, etc.).
//
// Wire values are stable — append new bits, never reassign existing ones —
// so existing .tres files keep loading.
[Flags]
public enum EStat
{
	None = 0,

	// Damage-type tags. Hit-side OR mask on DamageData / ContinuousDamageData /
	// StatusEffectData. Modifier entries against these scale damage / bypass /
	// armor chip / knockback / buildup at their dedicated sites.
	Damage = 1 << 0,
	Fire = 1 << 1,
	Blunt = 1 << 2,
	Dizzy = 1 << 3,
	Pierce = 1 << 4,
	Electrical = 1 << 5,
	Ranged = 1 << 6,
	Melee = 1 << 7,
	Poison = 1 << 8,
	Magical = 1 << 9,
	Knockback = 1 << 10,

	// Character stat modifiers. Composed by the actor on demand — receivers
	// call into the stat-modifier system to get a final value for movement,
	// sense, etc. Tag-mask use is meaningless for these (they identify a
	// single stat, not a category of hit) — they only ever appear as single-
	// bit modifier entries.
	OutgoingDamage = 1 << 11,    // multiplicative — attacker-side damage scale
	MoveSpeed = 1 << 12,         // multiplicative
	AnimSpeed = 1 << 13,         // multiplicative
	Vision = 1 << 14,            // multiplicative
	Hearing = 1 << 15,           // multiplicative
	Noise = 1 << 16,             // multiplicative
	Scent = 1 << 17,             // multiplicative
	FootprintAlpha = 1 << 18,    // multiplicative
	FootprintDuration = 1 << 19, // multiplicative
	Camouflage = 1 << 20,        // additive (sense offset)
	MaxStamina = 1 << 21,        // additive (flat stamina bonus)
	ColdResist = 1 << 22,        // additive (temperature threshold shift)
	HeatResist = 1 << 23,        // additive (temperature threshold shift)
}
