// A character stat — one value an actor composes on demand from every
// modifier source (inherent data, class, equipped armor, active status
// effects) via ComposeStat. A plain enum, not flags: a stat is never part of a
// mask. What a hit or an effect IS lives on EHitTag instead.
//
// Composition op is intrinsic to the stat (StatModifierUtil.IsAdditive).
//
// Values are written into .tres as ints, so they are explicit and APPEND-ONLY:
// renumbering one silently re-points every authored StatModifier.
public enum EStat
{
	None = 0,
	OutgoingDamage = 1,       // multiplicative — attacker-side damage scale
	MoveSpeed = 2,            // multiplicative
	AnimSpeed = 3,            // multiplicative
	Vision = 4,               // multiplicative
	Hearing = 5,              // multiplicative
	Noise = 6,                // multiplicative
	Scent = 7,                // multiplicative
	FootprintAlpha = 8,       // multiplicative
	FootprintDuration = 9,    // multiplicative
	Camouflage = 10,          // additive (sense offset)
	MaxStamina = 11,          // additive (flat stamina bonus)
	ColdResist = 12,          // additive (temperature threshold shift)
	HeatResist = 13,          // additive (temperature threshold shift)
	WetnessDryRate = 14,      // multiplicative (drying speed; <1 slows, >1 accelerates)
	NightVision = 15,         // multiplicative (perception darkness relief; value-1 = fraction of darkness penalty removed, e.g. 1.85 = 85%)
	MaxHealth = 16,           // additive (flat health bonus)
	MaxArmor = 17,            // additive (flat armor bonus)
	FortitudeResistance = 18, // multiplicative — scale on EVERY combat buildup (<1 = resistant); PlayerState.fortitude folds in here too
}
