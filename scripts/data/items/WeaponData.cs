using Godot;
using Godot.Collections;

[GlobalClass]
public partial class WeaponData : ItemData
{
	// Non-zero = this is an ammo-bearing weapon. Drives the equip-slot pick
	// (ammo weapons go to WeaponRight), the HUD ammo counter, and the
	// press-time ammo gate. Per-tier `ItemAction.useAmmo` is the source of
	// truth for whether a given firing tier consumes ammo / drops arrows;
	// `maxAmmo > 0` is just the weapon-level rollup callers use without
	// having to walk the profile.
	[Export] public int maxAmmo = 0;

	// Single canonical equip slot for this weapon. Ranged (ammo-bearing)
	// weapons live in WeaponRight; melee weapons in WeaponLeft. Handedness is
	// exclusive — a melee weapon cannot be equipped in the ranged slot or
	// vice versa. Equip / auto-equip / select-mode swap all read this so the
	// rule lives in one place.
	public EInventorySlot CanonicalSlot => maxAmmo > 0 ? EInventorySlot.WeaponRight : EInventorySlot.WeaponLeft;
	// Optional drop spawned at every Hitscan impact point. When wired, each
	// shot leaves a recoverable arrow in the world that returns 1 ammo when
	// removed (player pickup or LootData.removeTimeMs timeout). Null = no
	// drop, ammo decrements permanently.
	[Export] public ArrowLootData arrowLootData;

	// Named damage profiles fired by this weapon's events. Convention: the
	// "primary" key is the default fallback for any event that doesn't
	// override `damageProfileKey`; secondary keys (e.g. "rain_of_arrows")
	// hold per-effect variants the same weapon authors against. Lives in a
	// dict (not a sub-resource on the event) because Godot 4.6's [Tool]
	// inspector trips on typed DamageData sub-resource pickers — string keys
	// bind cleanly. Mob attacks don't read this; they'd need a parallel dict
	// on their own data shape when implemented.
	[Export] public Dictionary<StringName, DamageData> damageProfiles = new();

	// Per-second damage profiles used by SpawnAreaEffect zones (smooth burn,
	// magical aura). Sibling of damageProfiles so the HUD can surface them
	// side-by-side and the inspector can pick types cleanly. AreaIntervalSpec
	// entries on an ItemEvent still resolve against `damageProfiles`; this
	// dict is for the per-frame continuous portion only.
	[Export] public Dictionary<StringName, ContinuousDamageData> continuousProfiles = new();

	// Convenience lookup. Returns null if the key isn't authored; callers
	// either treat that as "no damage" (events early-out) or surface as an
	// authoring error.
	public DamageData GetDamage(StringName key)
	{
		if (damageProfiles == null)
		{
			return null;
		}
		return damageProfiles.TryGetValue(key, out DamageData d) ? d : null;
	}

	public ContinuousDamageData GetContinuousDamage(StringName key)
	{
		if (continuousProfiles == null)
		{
			return null;
		}
		return continuousProfiles.TryGetValue(key, out ContinuousDamageData d) ? d : null;
	}

	// Half-angle (in degrees) of the vertical auto-aim cone for ranged weapons.
	// While aiming, Player finds the best mob within this cone and adopts its
	// elevation as the firing pitch (clamped to the same range). 0 = pitch
	// assist disabled — fire flat along the yaw. Melee weapons leave this 0.
	[Export] public float pitchRangeDegrees = 0f;

	// Half-angle (in degrees) of the horizontal auto-aim cone. While aiming,
	// the player's yaw is gently pulled toward the best mob inside this cone
	// after the stick-driven rotation lands — the cone falls off smoothly so
	// the player can still rotate freely outside it (full 360° preserved).
	// 0 = yaw assist disabled.
	[Export] public float yawAssistDegrees = 0f;

	// Per-tick bias fraction in [0, 1] for both yaw pull and pitch smoothing.
	// 0 = no assist (the player aims raw); 1 = fully snap to the target each
	// tick. Yaw additionally has a smoothstep falloff to 0 at the cone edge
	// so the assist is strongest near a target's silhouette and fades out
	// before the player rotates past it. Pitch has no falloff because the
	// pitch cone is purely an acquisition gate.
	[Export(PropertyHint.Range, "0,1,0.01")] public float aimAssistStrength = 0.4f;

	// Authored timeline + tier list. A tap-fire weapon has a single tier with
	// chargeTime=0 and autoActivateAtMax=true. A charge-and-release bow can
	// be authored as a single tier with chargeTime > 0 (release before the
	// hold completes fires early) or as multiple tiers chaining via per-tier
	// chargeTime windows.
	[Export] public ItemActionProfile actionProfile;

	[Export] public override int maxLevel { get; set; } = 5;

	[ExportGroup("Blocking")]
	// Recharging "guard" armor that is active ONLY while the player is holding
	// this weapon's charge. A blocked hit is soaked by this pool BEFORE the
	// player's central armor (see Player.OnHurtBoxHit), so a held charge
	// doubles as a shield. blockArmor is the pool capacity (0 = the weapon has
	// no guard and none of this applies). The recharge stats are independent
	// of the player's central-armor recharge — while charging, the guard
	// refills at blockArmorRechargeSpeed (units/sec) once
	// blockArmorRechargeDelay seconds have elapsed since the last hit taken
	// mid-charge. Damage absorbed and the delay re-arm only happen while
	// charging (even at an empty pool, so a focused player can't regenerate
	// their guard under fire); outside the charge the pool sits frozen.
	[Export] public float blockArmor = 0f;
	[Export] public float blockArmorRechargeDelay = 1f;
	[Export] public float blockArmorRechargeSpeed = 20f;

	public override ItemState CreateState()
	{
		return new WeaponState(this);
	}
}
