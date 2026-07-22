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

	// Ability tell: the shared accent color this weapon stamps onto its wielder
	// at spawn, so any mob carrying it reads as "does this" regardless of region
	// (a torch goblin and a fire drake both reference the fire tell). Null = this
	// weapon contributes no tell. See AbilityTellData / ModelAnimator.ApplyPalette.
	[Export] public AbilityTellData abilityTell;

	// Single source of truth for handedness: true = this weapon equips to the
	// right-hand (ranged) slot, false = the left-hand (melee) slot. Handedness
	// is exclusive — equip / auto-equip / select-mode swap all read CanonicalSlot
	// so the rule lives in one place. Ranged weapons (bow, bomb) and positional
	// casters (the summoner — the aiming reticle only resolves positional / arced
	// aim for the right slot) set this true; melee weapons leave it false.
	// Independent of whether the weapon bears ammo (maxAmmo) and of the visual
	// wieldHand below.
	[Export] public bool rightHandSlot = false;
	public EInventorySlot CanonicalSlot => rightHandSlot ? EInventorySlot.WeaponRanged : EInventorySlot.WeaponMelee;

	protected override EItemCategory ComputeCategory() => rightHandSlot ? EItemCategory.WeaponRanged : EItemCategory.WeaponMelee;

	// How this weapon delivers its attacks. A capability set — a weapon may
	// carry several bits (a melee weapon with a charged throw). Gates which
	// weapon mods may attach: a mod's WeaponModData.requiredDelivery must be
	// None or share a bit with this. Leave None only for a weapon that should
	// accept no delivery-restricted mods.
	[Export] public EWeaponDelivery delivery = EWeaponDelivery.None;
	// Optional drop spawned at every Hitscan impact point. When wired, each
	// shot leaves a recoverable arrow in the world that returns 1 ammo when
	// removed (player pickup, or auto-reclaimed oldest-first by the central
	// ammoRechargeSeconds timer below). Null = no drop, ammo decrements
	// permanently. Arrows themselves carry no self-expiry timer — set their
	// LootData.removeTimeMs to 0; the weapon's central timer owns recovery.
	[Export] public ArrowLootData arrowLootData;

	// Seconds per unit of the weapon's single central ammo-recharge timer.
	// While ammo < maxAmmo the deadline runs continuously and restarts after
	// each refill, so a fully-spent weapon climbs back to full in
	// `ammoRechargeSeconds * maxAmmo` seconds; firing never resets the
	// in-progress charge, and topping back off by hand clears it. Each elapse
	// recovers one unit: a weapon that drops arrows (arrowLootData set — the
	// bow) auto-reclaims its oldest outstanding arrow; a self-recharging
	// magazine (the bomb) regenerates ammo from nothing. 0 (default) disables
	// the timer entirely — such an arrow weapon would then refill only via
	// hand pickup. See Player.TickAmmoRecharge.
	[Export] public float ammoRechargeSeconds = 0f;

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

	// Per-tick bias fraction in [0, 1] for the YAW pull only. 0 = no yaw assist
	// (the player aims raw); 1 = fully snap yaw to the target each tick. The pull
	// has a smoothstep falloff to 0 at the cone edge so it is strongest near a
	// target's silhouette and fades out before the player rotates past it. Pitch
	// does NOT use this scale — the stick has no manual pitch control, so pitch
	// commits fully to the target's elevation (faded in by the same cone-proximity
	// curve) rather than stopping short of it.
	[Export(PropertyHint.Range, "0,1,0.01")] public float aimAssistStrength = 0.4f;

	// Authored timeline + tier list. A tap-fire weapon has a single tier with
	// chargeTime=0 and autoActivateAtMax=true. A charge-and-release bow can
	// be authored as a single tier with chargeTime > 0 (release before the
	// hold completes fires early) or as multiple tiers chaining via per-tier
	// chargeTime windows.
	[Export] public ItemActionProfile actionProfile;

	// Which hand the wielded model attaches to. HeldItemVisual binds a socket
	// per side against the rig's L_/R_wrist_joint bones and routes SetWeapon to
	// this hand. Default Right matches swords and the bow's draw hand; set Left
	// for an off-hand weapon. Independent of CanonicalSlot (the inventory slot) —
	// this is purely the visual attach point.
	[Export] public EHand wieldHand = EHand.Right;

	// Per-weapon animation overrides consulted by Player.UpdateAnimation while
	// this weapon is the one in hand — its own idle/run stance, charge poses
	// (tier x locomotion), attack one-shots, and block reaction. Null = the
	// player uses the unarmed clips throughout. See WeaponAnimSet.
	[Export] public WeaponAnimSet animSet;

	// Summoner weapon: how many minions this weapon may keep alive at once.
	// WeaponState tracks its summoned minions; summoning past this cap recycles
	// the oldest live minion first. 1 (default) = a single minion at a time;
	// raise it to allow a small pack. Only meaningful for a weapon whose action
	// timeline fires SummonMinion events.
	[Export] public int maxMinions = 1;

	[ExportGroup("AI")]
	// Mob AI engagement tuning — read by BehaviorAttack when a mob wields this
	// weapon (see SpeciesData.weapons). Ignored for player-held weapons (the
	// player drives range / cadence through input + aim). Each tick the mob fires
	// the highest-priority of its weapons whose gates below all pass.

	// Preference when more than one of the mob's weapons can fire this tick —
	// higher wins. A gated special (e.g. a battle cry) sits above the always-
	// available basic attack so it's chosen whenever its conditions hold, and
	// the basic attack carries it the rest of the time. Ties break toward the
	// earlier weapon in the loadout (SpeciesData.weapons).
	[Export] public int priority = 0;

	// Distance at which the mob will fire this weapon (when it can see the target).
	[Export] public float maxAttackRange = 2.5f;
	// Maximum absolute Y differential between mob and target this weapon fires at.
	// Approach / encircle / pathfinding still use 2D distance, so the mob chases
	// up or down to reach the target; this only prevents committing a swing while
	// the target sits on a plateau above / pit below the weapon's vertical reach.
	[Export] public float maxVerticalAttackRange = 4f;
	// Standoff distance the wielding mob holds from the target — it stops closing
	// here so it can attack instead of slamming into the target.
	[Export] public float desiredAttackRange = 1.75f;
	// Fixed cooldown (seconds) after the mob fires this weapon before it may fire
	// it again. Tracked per-weapon so a long-cooldown cry doesn't stall the always-
	// available basic attack between cries. (Distinct from the per-tier
	// ItemAction.cooldownSeconds, which mob attack tiers leave at 0.) Cadence
	// variety is not added here — it's an additional, behavior-level pause AFTER
	// this cooldown elapses; see AttackBehaviorData.attackPauseSeconds.
	[Export] public float cooldownSeconds = 1.5f;
	// Minimum count of same-team mobs (including the wielder) within allyRange
	// required to fire this weapon. 0 = no gate (always available); 2+ keeps a
	// buff / battle cry from firing into an empty field. See BehaviorAttack.
	[Export] public int minAllies = 0;
	[Export] public float allyRange = 8f;
	// AI treats this as a REACTIVE weapon: the mob never approaches to bring it to
	// bear (it fires only when the target is already inside maxAttackRange) and it
	// never drives the mob's hold/standoff distance. For a skirmisher's short-range
	// fallback — a ranged mob's melee — so the mob kites at its ranged weapon's
	// desiredAttackRange and only lashes out with this when the target closes into
	// reach. Default false = an ordinary weapon the mob closes in to use.
	[Export] public bool aiReactiveOnly = false;

	[ExportGroup("Blocking")]
	// Recharging "guard" armor that is active ONLY while the player is sneaking
	// with this weapon in the melee slot. A blocked hit is soaked by this pool
	// BEFORE the player's central armor (see Player.OnHurtBoxHit), so the sneak
	// crouch doubles as a shield. blockArmor is the pool capacity (0 = the
	// weapon has no guard and none of this applies). The recharge stats are
	// independent of the player's central-armor recharge — the guard refills
	// fully over blockArmorRechargeTime seconds (rate derived from blockArmor /
	// that time) once blockArmorRechargeDelay seconds have elapsed since the
	// last hit. Any damage taken while guarding re-arms that delay even when the
	// pool is already empty, so a focused player can't regenerate their guard
	// under fire.
	[Export] public float blockArmor = 0f;
	[Export] public float blockArmorRechargeDelay = 1f;
	// Seconds for the guard to refill from empty to full blockArmor. 0 = never
	// recharges.
	[Export] public float blockArmorRechargeTime = 0.5f;

	// Parry window: milliseconds after a sneak-block begins during which a
	// well-timed block PARRIES — fully negating the blow and counter-striking
	// the attacker — rather than merely soaking it into the guard pool. 0
	// (default) = the weapon can't parry (it only passively blocks). Measured
	// against the sim clock (GameTimeMs), so it slows uniformly under slow-mo.
	[Export] public int parryTimeMs = 0;
	// The largest single hit a parry can fully negate. A blow whose (post-
	// resistance) damage is at or under this is deflected outright — no health,
	// armor, buildup, hitstun, or knockback lands — and the attacker takes the
	// parryDamageProfileKey counter. A bigger blow can't be parried and falls
	// through to the passive block below. Independent of blockArmor, so a weapon
	// that barely blocks (a knife, blockArmor = 0) can still parry hard. A parry
	// is only available while the guard is off its recharge cooldown, and lands
	// re-arm that delay (see Player.SpendParryGuard) so parries can't be spammed
	// — this is the "interacts with block recharge" coupling. 0 = no parry.
	[Export] public float maxParryDamage = 0f;
	// Damage profile (key into damageProfiles) dealt back to the attacker on a
	// successful parry. Empty / unmapped = no counter-strike (the blow is still
	// negated, it just deals no damage back). Only a melee attacker — a Mob
	// dealing the parried blow — is countered.
	[Export] public StringName parryDamageProfileKey = new StringName();

	public override ItemState CreateState()
	{
		return new WeaponState(this);
	}
}
