// UI label keys for the per-stat readouts on the inventory's item info /
// action / context panels. Display strings live in GameClient.statNames —
// this enum is purely a stable handle so call sites don't carry hardcoded
// strings.
public enum EStatName
{
	Damage,
	ArmorPenetration,
	Blunt,
	Dizzy,
	Knockback,
	BloodCost,
	StaminaCost,
	Cooldown,
	Range,
	Reach,
	TargetRange,
	Dps,
	Radius,
	Duration,
	Ammo,
	Charges,
	Heal,
	MoveSpeed,
	MaxStamina,
	ColdResist,
	HeatResist,
	Health,
	Armor,
	Camouflage,
	Vision,
	NightVision,
	Hearing,
	Noise,
	Scent,
	Fire,
	Magical,
	Poison,
	Electrical,
	Ranged,
	Melee,
	OutgoingDamage,
	// Level-derived forge-upgrade scaling: outgoing damage+buildup multiplier
	// (offense slots) and incoming damage reduction (Armor slot).
	DamageScale,
	DamageReduction,
	AnimSpeed,
	FootprintAlpha,
	FootprintDuration,
	// Per-character party sheet attributes (PlayerState), shown on the camp
	// Select-Character stats panel.
	Fortitude,
	Strength,
	Perception,
	Stealth,
	Charisma,
}
