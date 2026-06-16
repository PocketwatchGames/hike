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
