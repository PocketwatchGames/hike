using System;

// What a hit or a status effect IS — an OR mask on DamageData /
// ContinuousDamageData (a sword swing is Damage|Physical|Melee|Blunt), a single
// family on StatusEffectData, and the key a TagModifier resists or amplifies.
//
// Two groups, and the split is the rule:
//  - HIT tags scale a direct hit at its application sites (damage, armor
//    bypass, armor chip, knockback).
//  - STATUS FAMILY tags scale only the BUILDUP feeding an effect of that
//    family. Once an effect lands it runs at full strength — its DoT is never
//    resisted. A fire-resistant cloak resists Fire, and resists Burning only
//    if it also says so.
//
// Values are written into .tres as ints: APPEND-ONLY, never reassign a bit.
[Flags]
public enum EHitTag
{
	None = 0,

	// The universal "this damages" marker. Rides on every damaging hit, so a
	// modifier against it scales all direct damage.
	Damage = 1 << 0,
	Fire = 1 << 1,
	Blunt = 1 << 2,
	// Status family (buildup only) — kept at its original bit.
	Dizzy = 1 << 3,
	ArmorPenetration = 1 << 4,
	Electrical = 1 << 5,
	Ranged = 1 << 6,
	Melee = 1 << 7,
	Poison = 1 << 8,
	Magical = 1 << 9,
	Knockback = 1 << 10,
	// Ordinary matter-on-matter damage — a blade, a club, an arrow, a blast's
	// concussion. Exists so "physical" is something a hit can SAY rather than
	// something inferred from carrying no other type: absence can't be
	// resisted, required by a receiver, or validated. Orthogonal to
	// Melee/Ranged, which are delivery.
	Physical = 1 << 11,

	// Status families (buildup only).
	Burning = 1 << 12,
	Poisoned = 1 << 13,   // poison AND food poisoning
	Shocked = 1 << 14,
	// Harm from direct sunlight — for monsters that burn in the sun (slimes,
	// vampires). Not Fire: a fire-resistant creature is not sun-proof.
	Sunlight = 1 << 15,
}

public static class HitTags
{
	// Tags that scale a hit's healthDamage. Receivers AND this with hit.tags.
	// ArmorPenetration / Blunt / Knockback have their own application sites.
	public const EHitTag DamageScale =
		EHitTag.Damage
		| EHitTag.Fire
		| EHitTag.Magical
		| EHitTag.Poison
		| EHitTag.Electrical
		| EHitTag.Physical
		| EHitTag.Ranged
		| EHitTag.Melee;

	// What a hit is MADE of, as opposed to delivery (Ranged / Melee), mechanic
	// (Blunt / ArmorPenetration / Knockback) or the universal Damage marker.
	// Every template that sets Damage must set at least one — ResourceCheck
	// enforces it, and Destructible.destroyedBy is authored against it.
	public const EHitTag DamageTypes =
		EHitTag.Physical
		| EHitTag.Fire
		| EHitTag.Electrical
		| EHitTag.Poison
		| EHitTag.Magical;

	// The only tags a StatusEffectData may carry — ResourceCheck enforces it.
	public const EHitTag StatusFamilies =
		EHitTag.Dizzy
		| EHitTag.Burning
		| EHitTag.Poisoned
		| EHitTag.Shocked
		| EHitTag.Sunlight;
}
