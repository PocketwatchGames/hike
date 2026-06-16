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
}
