using Godot;

// Where a weapon mod's name affix sits relative to the item's base noun when
// WeaponNameGenerator composes the full name. Prefix → before ("Fragile bomb");
// Suffix → after ("bow of Lightning"). The exact word order / connector for each
// slot is a per-language loc template (weapon_name_prefix / weapon_name_suffix),
// so this enum only picks the slot — the template handles grammar.
public enum EAffixPosition
{
	Prefix = 0,
	Suffix = 1,
}

// Weapon-modifier payload — the on-attack effects/modifiers a weapon mod adds, meaningful
// only on a WeaponState (see ItemDescriptor / LootSpawnEntry). The player's modded weapons
// and elite mobs carry these the same way: a mob wields real WeaponData weapons
// (Mob.GetWeapon) and the elite mob-mod composes its signature (e.g. Lightning) onto every
// one, so the payload always fires through a weapon, never as a body status effect.
// New weapon-mod fields go here, not on StatusEffectData; group co-dependent ones into a
// nested sub-resource and independent ones with an [ExportGroup].
// [Tool] so the editor can instantiate it as its real type when its [Tool] parent
// StatusEffectData binds the weaponMod property — otherwise the editor loads it as a
// base Resource and the parent's typed setter throws / leaves the field empty.
[Tool]
[GlobalClass]
public partial class WeaponModData : Resource
{
	// Weapon delivery methods this mod is allowed to attach to. None (default) =
	// no restriction, applies to any weapon — the right default for mods that act
	// on any landed hit (Vampiric, Flaming, Knockback). Set it only for a
	// delivery-specific mod: Charged Pierce → Shot, Fragile → Shot | Thrown. A mod
	// attaches when this is None or shares any bit with the weapon's
	// WeaponData.delivery (see ItemDescriptor.ApplyTo).
	[ExportGroup("Compatibility")]
	[Export] public EWeaponDelivery requiredDelivery = EWeaponDelivery.None;

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

	// On each landed attack, arc lightning between nearby enemies from the impact
	// point. The "Shocking" weapon mod (player) and the elite "Lightning" signature
	// (composed onto a mob's natural weapon) both ride this. Null = none. See
	// ChainLightningData.
	[ExportGroup("Chain Lightning")]
	[Export] public ChainLightningData chainLightning;

	// Looping Fx attached to the weapon model while this mod is on the wielded
	// weapon — a Flaming sword's flame, a glowing enchant aura. Pushed onto the
	// in-hand HeldWeapon scene (parented to its idleFxAnchor) whenever the weapon
	// is drawn; it fades out when the weapon is swapped away. Author it as a loop
	// Fx (_loop = true) so it sustains. Null = no held-weapon fx. Not charge-
	// scoped — the idle visual rides the weapon at rest, independent of tier.
	[ExportGroup("Visual Fx")]
	[Export] public PackedScene idleFx;

	// Looping Fx attached to every projectile this weapon fires while the mod
	// reaches the firing charge tier — a Flaming bow's flaming arrows. Rides the
	// projectile and fades at impact, exactly like the event's own
	// projectileLoopEffect (layered on top of it). Null = no projectile fx.
	[Export] public PackedScene projectileFx;

	// ============================ Naming ============================
	// How this mod names the weapons it's attached to (see WeaponNameGenerator).

	// Surface word/phrase the mod injects into the composed name. Empty falls back
	// to the owning effect's displayName, so a plain adjective mod (Fragile) names
	// the weapon with no extra authoring. Set it explicitly when the name word
	// differs from the effect's UI name — e.g. a suffix "of Lightning" whose
	// effect displayName is just "Lightning".
	[ExportGroup("Naming")]
	[Export] public StringName affix = "";

	// Whether the affix reads before the noun ("Fragile bomb") or after it
	// ("bow of Lightning").
	[Export] public EAffixPosition affixPosition = EAffixPosition.Prefix;
}
