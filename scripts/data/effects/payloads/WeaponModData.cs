using Godot;

// Weapon-modifier payload — meaningful only when the owning effect is composed onto a
// weapon (see ItemDescriptor / LootSpawnEntry), not an actor. New weapon-mod fields go
// here, not on StatusEffectData; group co-dependent ones into a nested sub-resource and
// independent ones with an [ExportGroup].
[GlobalClass]
public partial class WeaponModData : Resource
{
	// Projectiles carrying an impactEvent shatter on first contact instead of bouncing
	// out their fuse. The "Fragile" mod.
	[ExportGroup("Projectile")]
	[Export] public bool projectilesDetonateOnContact = false;

	// Creatures a shot passes through (damaging each) before stopping, composed as a max
	// against the event's base and other mods. The "Charged Pierce" mod. 0 = no change.
	[Export] public int projectilePierceCount = 0;

	// Fraction of the health damage each landed attack deals that is returned to the
	// attacker as healing (lifesteal). The "Vampiric" mod. 0 = no lifesteal.
	[ExportGroup("Lifesteal")]
	[Export(PropertyHint.Range, "0,1,0.01")] public float vampiric = 0f;

	// Status effects every landed attack appends to the struck target, on top of
	// whatever the weapon's own DamageData authors. The "Flaming" mod (→ Burning)
	// lives here. Folded into the outgoing hit by ResolveHit / the projectile path
	// the same way DamageData.statusEffects is.
	[ExportGroup("On-Hit Effects")]
	[Export] public Godot.Collections.Array<StatusEffectData> onHitStatusEffects;

	// Extra knockback every landed attack imparts, on top of the weapon's own
	// DamageData. The "Knockback" mod. Added (summed across reaching mods) to the
	// outgoing hit's knockbackDistance; the hit direction is the attacker's facing
	// (set by ResolveHit / the projectile's flight), so the shove goes the right way.
	[ExportGroup("Knockback")]
	[Export(PropertyHint.Range, "0,20,0.5,or_greater")] public float knockbackBonus = 0f;

	// Extra stagger/lockout seconds added alongside knockbackBonus. 0 = shove the
	// target without lengthening its knockback lockout window.
	[Export(PropertyHint.Range, "0,2,0.05,or_greater")] public float knockbackTimeBonus = 0f;
}
