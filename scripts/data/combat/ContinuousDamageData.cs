using Godot;

// Authored template for a damage zone's smooth, per-frame portion. Sits as a
// sibling to DamageData inside the owning entity (WeaponData /
// MobData / FireTrapData …) under `continuousProfiles`. Distinct from
// DamageData because most DamageData fields (modifiers, statusEffects,
// hitstun, knockback) don't compose continuously — a crit roll per physics
// frame is a flood, hitstun would lock the receiver every frame, and status
// stacks belong on the discrete interval path.
//
// Apply rule: per-frame damage = healthDamage * delta. The receiver routes
// through HurtBox.Hit with `dot = true`, which feeds DotHudAccumulator so
// the HUD shows one rolled-up floating number per second instead of one per
// physics frame.
//
// Armor-penetration semantics differ from DamageData.armorPenetration: here it's the
// FRACTION of the per-frame damage that bypasses armor (HitInfo.armorBypassFraction),
// not a chance to bypass entirely. 0 = always absorbed by armor; 1 = always
// straight to health; 0.3 = 30% bleeds through, 70% chips armor.
[Tool]
[GlobalClass]
public partial class ContinuousDamageData : Resource
{
	// Type tags carried by this continuous hit (Fire, Poison, Magical, …).
	// Same role as DamageData.tags — receivers fold their StatModifier
	// entries against this mask to scale per-frame health damage. Default
	// None = untyped, full damage. A fire pillar would be Damage|Fire, a
	// poison gas cloud Damage|Poison, etc.
	private EStat _tags;
	[Export, CompactFlags] public EStat tags
	{
		get => _tags;
		set
		{
			if (_tags == value) { return; }
			_tags = value;
			EmitChanged();
		}
	}

	// Damage per second applied to anything inside the zone. Scaled by
	// physics delta when the zone fires its per-frame tick.
	[Export] public float healthDamage = 0f;

	// Non-null marks this as environmental-hazard damage: `healthDamage` is IGNORED
	// and the zone's dps resolves from the profile's damage band as a fraction of
	// the receiver's max health per second (HitInfo.ApplyHazardScaling). The same
	// profile bands the proc rate and DoT of anything in `buildups`. Null =
	// ordinary flat dps.
	[Export] public HazardProfileData hazardProfile;

	// Fraction of per-frame damage that bypasses armor and lands on health.
	// Differs from DamageData.armorPenetration (chance-based) — continuous damage
	// spreads the bypass across time instead of rolling per hit. Default 1
	// (skip armor entirely) because the typical continuous source is an
	// environmental DoT (fire, acid, poison gas) that shouldn't be stopped
	// by a worn armor plate; author less to let an armor type soak some of
	// the burn.
	[Export(PropertyHint.Range, "0,1,0.01")] public float armorPenetration = 1f;

	// Anti-armor multiplier on the absorbed portion of the per-frame chip.
	// Final armor chip is `absorbable * (1 + blunt)`. Symmetric with
	// DamageData.blunt; harmless when 0.
	[Export] public float blunt = 0f;

	// Buildup contributions per second. Each entry's `amount` is scaled by
	// the physics delta and accumulated into the receiver's meter for
	// `effect` — fire zones feed Burning buildup this way so a body that
	// lingers in the column eventually crosses the threshold even though no
	// discrete hit ever lands. Symmetric with DamageData.buildups but
	// authored as a rate (units/sec) rather than a per-hit chunk.
	[Export] public Godot.Collections.Array<StatusEffectBuildup> buildups;
}
