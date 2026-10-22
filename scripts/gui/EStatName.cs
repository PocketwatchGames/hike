// UI label keys for the per-stat readouts on the inventory's item info /
// action / context panels. Display strings live in GameClient.statNames —
// this enum is purely a stable handle so call sites don't carry hardcoded
// strings.
public enum EStatName
{
	Damage,
	CritChance,
	ArmorPenetration,
	Blunt,
	Dizzy,
	Knockback,
	Block,
	Parry,
	BloodCost,
	Range,
	TargetRange,
	Dps,
	Radius,
	Duration,
	Ammo,
	BreakChance,
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
	Physical,
	// Status families (EHitTag) — shown as buildup resistances.
	Burning,
	Poisoned,
	Shocked,
	Sunlight,
	Bleeding,
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
	// Equipped gear's weight (GearData.weight).
	Weight,
	// Shield block counter-strike (ShieldData.blockCounter) context title.
	Thorns,
	OilCost,
	DashDistance,
	// Gear durability (GearState.durability / GearData.maxDurability).
	Durability,
	DurabilityWear,
	HungerDrain,
	ThirstDrain,
	FatigueDrain,
	// Hunger a food restores (ConsumableData.satiation).
	Satiation,
	// What a flask or lantern holds now, out of its capacity.
	Contents,
	// How far an attack carries the wielder forward (ItemAction.ComputeLungeDistance).
	Lunge,
	// Extra armor chip a hit deals (DamageData.blunt) — distinct from Blunt, the
	// hit tag a resistance row names.
	ArmorChip,
}
