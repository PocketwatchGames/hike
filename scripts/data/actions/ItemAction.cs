using Godot;
using Godot.Collections;

// One charge tier within an ItemActionProfile. The charge timeline runs from
// press; each tier holds for its own `chargeTime` before the next tier in
// `chargedActions` (same comboIndex) takes over. On activation (release, or
// autoActivateAtMax when the final tier's window completes), the runner
// enters Active and walks `events` over `activeDurationSeconds`. At
// activation the full attack cycle (activeDurationSeconds + cooldownSeconds)
// is written to the driving item's cooldownExpireMs, gating re-firing of that
// specific item until the swing AND its recovery tail have both elapsed.
[GlobalClass]
public partial class ItemAction : Resource
{
	// UI label shown in the inventory info panel and (later) the charge-tier
	// HUD. Per tier so weapons can name their tiers in flavor terms
	// (Snap Shot / Rain of Arrows / Heavy Bash) rather than relying on a
	// shared enum. Empty falls back to a generic "Action N" label.
	[Export] public StringName displayName = "";

	// Duration the player must continue holding while THIS tier is selected
	// before the next tier (same comboIndex, next in chargedActions) takes
	// over. `chargeT` ramps 0 → 1 across this window — so a Light tier with
	// chargeTime = 0.7 hands off to Heavy at 0.7s and lets `chargedRangeScale`
	// / `chargedAccuracyScale` scale across that span. A single-tier weapon
	// can put chargeTime on its only tier to get a press-to-full ramp before
	// firing (autoActivateAtMax). 0 (the default) = no within-tier ramp:
	// the tier fires at its event's base stats, and (if there's a next tier)
	// the next tier becomes selectable immediately.
	[Export] public float chargeTime = 0f;

	// Per-tier "hold to completion": when true, releasing the input while THIS
	// tier is the selected charging tier aborts instead of committing — the tier
	// only fires by auto-activating at full charge. The profile-wide
	// ItemActionProfile.requireFullCharge forces this on every tier; this field
	// lets a single tier opt in while others in the same profile still commit on
	// release. The lantern uses it so its low toggle tier fires on a quick tap
	// while the high heal tier demands a full hold (an early release cancels the
	// cast without toggling the light).
	[Export] public bool requireFullCharge = false;

	// Length of the Active phase in seconds. May be 0 — t=0 events fire and
	// Active exits the same tick.
	[Export] public float activeDurationSeconds = 0f;

	// Recovery tail after the swing: the time from the END of the Active phase
	// until the item can fire again. The full attack cycle is therefore
	// activeDurationSeconds + cooldownSeconds, and that sum is what EnterActive
	// writes to the driving item's cooldownExpireMs (activation-anchored), so
	// the HUD cooldown bar spans the whole cycle. 0 = no gate beyond the swing
	// itself. Independent per-item (a weapon's cooldown doesn't block other
	// items).
	[Export] public float cooldownSeconds = 0f;

	// Resource costs — direct fields rather than ActionRequirement subclasses
	// because the set is closed (stamina, blood, ammo) and each maps to a
	// single number. SelectTierIndex reads them inline and gates tier
	// promotion when the actor can't afford; EnterActive spends them
	// unconditionally on the activated tier. The separate `requirements`
	// array below is reserved for non-resource gates (weapon level, language
	// known, etc.) — keep item-based / inventory-based gates out of
	// ItemAction entirely (InteractiveAction is the right home for those).
	[Export] public float staminaCost = 0f;
	[Export] public float bloodCost = 0f;

	// Lantern fuel spent (in seconds of burn budget) when this tier
	// activates, drawn from the driving item's fuel tank. Unlike stamina/blood,
	// the gate is "has ANY fuel left" (> 0), not "can afford the full cost" — a
	// near-empty lantern still casts and the spend clamps the tank at 0 (see
	// LanternState.SpendFuel). 0 (default) = no fuel cost. Only meaningful when the
	// driving item (context.primaryItem) is a fuel-bearing consumable (a lantern).
	[Export] public float fuelCost = 0f;

	// True if this tier consumes ammo from the driving WeaponState — gates
	// the press at zero ammo (PlayerWeapon / AimingReticle / WeaponHud read
	// this through WeaponData.UsesAmmo) and gates arrow-loot generation in
	// DoHitscan / DoProjectile (so a melee-bash tier on a bow doesn't drop
	// arrows even though the weapon authors arrowLootData). The actual ammo
	// decrement still rides on the per-event EItemEventType.UseAmmo flag so
	// authors can pick which timeline moment burns the ammo.
	[Export] public bool useAmmo = false;

	// Events fired during Active, on a timeline measured from activateMs.
	[Export] public Array<ItemEvent> events = new();

	// readyEvents fire when this tier becomes the selected tier during
	// charging (the "you've reached Heavy" cue).
	[Export] public Array<ItemEvent> readyEvents = new();

	// Combo position within the action profile. At press time, the runner picks
	// a candidate index based on the driving weapon's chain state: if the
	// previous activation's combo window hasn't lapsed, the runner targets
	// `previousComboIndex + 1`; otherwise it targets 0. Tier selection then
	// filters chargedActions to those matching the target. If no action matches
	// `previousComboIndex + 1`, the runner falls back to 0 (chain restart).
	// `comboWindowMs` is how long after THIS action ends the chain stays open
	// for the next press; 0 means the chain terminates here.
	[Export] public int comboIndex = 0;
	[Export] public ulong comboWindowMs = 0;

	// Repeat-swing combo (the lightweight alternative to authoring one tier per
	// combo step). When non-empty, a single press activates this tier and the
	// driving WeaponState.repeatIndex walks these entries — one per press — while
	// the combo window (comboExpireMs, extended each swing by comboWindowMs) stays
	// open, wrapping to 0 after the final entry. The array length IS the combo
	// length and the index IS the swing number; each entry layers per-swing tweaks
	// (damage, cooldown) over this base tier, so a default entry just replays the
	// base swing. Empty (default) = a normal single-shot tier with no repeat chain.
	// Activating any tier whose list is empty (a charged finisher) resets the
	// weapon's repeat cursor to 0, as does getting hit.
	[Export] public Array<ActionRepeatOverride> repeatActionOverrides = new();

	// Per-tier non-resource gates (weapon level, language known, etc.).
	// Resource costs (stamina, blood, ammo) live on dedicated fields above
	// — keep this array for world / character state checks only; inventory-
	// dependent gates belong on InteractiveAction. Action only selectable
	// when all evaluate true; failed entries drop selection to the next
	// lower tier.
	[Export] public Array<ActionRequirement> requirements = new();

	// Active-phase abort policy. Both flags only consulted while Active —
	// charging always cancels on release/sneak/damage. Defaults match the
	// "committed combat action" pattern (player can't bail, damage staggers).
	[Export] public bool canAbort = false;
	[Export] public bool canInterrupt = true;

	// How player aim input drives this tier. Directional → stick / mouse
	// drives ActorForward and the aim point is forward × range (matches the
	// pre-existing bow / hitscan path). Positional → stick / mouse pushes a
	// world-space aim point across the ground per frame, scaled by
	// `positionalRange` below; the cursor stays where it was when the active
	// tier flips mode mid-charge, so the reticle's ground circle is
	// continuous. A single profile can mix modes between tiers (e.g. snap
	// bow shot → charged rain-of-arrows; melee axe → charged thrown
	// explosive).
	[Export] public EAimType aimType = EAimType.Directional;

	// Positional aim only: maximum horizontal distance of the aim cursor
	// from the player, in world meters. The cursor is clamped to a disk of
	// this radius; its sweep speed is a constant world rate (the reticle's
	// _gamepadPositionalCursorSpeed), so a short-reach tier sweeps its disk
	// faster edge-to-edge than a long one. Authored per-tier — a charged AoE
	// on a long-range bow can still target close to the caster, and a thrown-
	// explosive on a melee weapon defines its own reach independently of
	// the weapon's melee range. Ignored for Directional tiers (their reach
	// comes from the event's hitscan / projectile / melee distance).
	[Export] public float positionalRange = 10f;

	// Positional aim only: world-space radius of the targeting ring drawn
	// at the cursor — represents the tier's footprint on the ground (AoE
	// radius for rain-of-arrows, blast radius for a thrown explosive,
	// etc.). The reticle's ring outer radius lerps to this value while
	// Positional aim is active, so designers can tune the telegraph
	// independently of the gameplay AoE if needed. The AoE event handler
	// that spawns `areaEffectScene` should read this same value and pass
	// it into the spawned instance so the visual and the actual damage
	// zone share one authored number. Ignored for Directional tiers
	// (the ring there is the small lock-on dot / mob silhouette halo).
	[Export] public float positionalAreaRadius = 1.5f;

	// Press-time spread fraction in [0, 1] for ranged events on this tier.
	// 0 = pinpoint, 1 = full MAX_SPREAD_HALF_ANGLE cone. Melee ignores it.
	// Combined with `chargedAccuracyScale` below to model "hold to steady".
	[Export] public float accuracySpread01 = 0f;
	// Within-tier charge response on Hitscan / Projectile events. Each event
	// authors its own base firing stats (hitScanRange, projectileSpeed,
	// projectileLifetimeSeconds); these scalars only modulate the FRACTION
	// of base/press that ends up being applied at fire time.
	//
	// `chargedRangeScale` MULTIPLIES the event's base range as `chargeT`
	// runs 0 → 1: at chargeT=0 the range is base; at chargeT=1 it's
	// `base * chargedRangeScale`. For arced (thrown) projectiles the scaled
	// range is `projectileMaxRange` — the aim disk and the throw's max reach
	// (and max launch speed, reach / lifetime) grow as the hold charges. 1.0
	// (default) = no within-tier ramp; the tier fires at its event's authored
	// range regardless of hold length.
	// `chargedAccuracyScale` DIVIDES `accuracySpread01` as `chargeT` runs
	// 0 → 1: at chargeT=0 spread = accuracySpread01; at chargeT=1 spread
	// = `accuracySpread01 / chargedAccuracyScale`. 1.0 (default) = no
	// tightening from holding. Asymmetric (divide, not multiply) because
	// accuracy improves toward zero — a multiplicative 0→1 ramp can't
	// reach pinpoint while still allowing a non-zero press value.
	[Export] public float chargedRangeScale = 1f;
	[Export] public float chargedAccuracyScale = 1f;

	// Movement constraints the actor retains while this is the selected tier,
	// split by phase and mutually exclusive (only one phase is live at a time).
	// `maxSpeedCharging` caps the player's move speed at a named gait for the
	// ENTIRE Charging phase, press through release (Stationary = a drinking
	// consumable roots at 0; Sneak = the summoner's channel crawl; Sprint =
	// effectively unrestricted). `chargedSpeedMax` is the same kind of cap but
	// engages only once THIS tier is fully charged — its own chargeTime window
	// complete, the hold sitting at max — so the bow draws at full speed and
	// drops to a Sneak crawl only at full draw. A chargeTime = 0 tier counts as
	// fully charged from the moment it's selected. When both are authored the
	// lower gait wins while fully charged. Both are ceilings applied as a min
	// against the computed speed, never a speed-up.
	// `speedMultiplierActive` is a MULTIPLIER (1 = full speed, 0 = fully rooted)
	// applied during Active (the swing / strike / dart) — mob attacks set it to
	// 0 to own the body through windup, strike, and recovery. NOTE: the "is
	// movement locked" boolean (ActionRunner.LocksMovement, consumed by mob
	// path-skip, footstep suppression, and the charge-anim override) is true
	// while charging only when the EFFECTIVE cap (including an engaged
	// chargedSpeedMax) is Stationary, and while Active when
	// speedMultiplierActive <= 0.
	[Export] public EChargeSpeedCap maxSpeedCharging = EChargeSpeedCap.Sprint;
	[Export] public EChargeSpeedCap chargedSpeedMax = EChargeSpeedCap.Sprint;
	[Export(PropertyHint.Range, "0,1,0.01")] public float speedMultiplierActive = 1f;

	// Turn-speed multipliers the actor retains while this is the selected tier,
	// split by phase like the speed pair above (1 = free turning, 0 = facing
	// fully locked). A committed melee swing sets `turnSpeedMultiplierActive` = 0
	// so the attacker can't course-correct its aim mid-swing — it commits to the
	// facing it had when the swing began, and any dart/lunge fires along that
	// locked heading. The "is facing locked" boolean (ActionRunner.LocksFacing)
	// is derived as multiplier <= 0, so a partial value just slows the turn.
	[Export(PropertyHint.Range, "0,1,0.01")] public float turnSpeedMultiplierCharging = 1f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float turnSpeedMultiplierActive = 1f;

	// Grace window (seconds into the Active phase) during which facing stays free
	// before `turnSpeedMultiplierActive` engages — lets a mob finish turning onto
	// its target in the opening of a committed swing, then lock for the strike.
	// 0 (default) locks immediately. Only meaningful when turnSpeedMultiplierActive
	// is below 1; ignored otherwise.
	[Export(PropertyHint.Range, "0,1,0.01")] public float turnLockDelaySeconds = 0f;

	// Per-tier charge audio/effect lifecycle, managed by ActionRunner. Each is
	// a PackedScene wrapping an Fx.
	//
	// chargeStartEffect (one-shot): fired when this tier becomes the selected
	//   tier during Charging — at press for tier 0 with chargeTime=0, or at the
	//   moment the charge timer crosses chargeTime for higher tiers. A multi-
	//   tier weapon's "light" tier typically leaves these null and only its
	//   "heavy" tier configures them, so the windup audio fires only when the
	//   player has actually committed to charging.
	// chargeLoopEffect (loop, set Fx._loop=true): instantiated when
	//   this tier becomes selected and Stop()'d on tier change or Charging exit.
	// chargeCancelEffect (one-shot): fired when Charging aborts (player cancel
	//   or interrupt) while this tier is selected. NOT fired on a successful
	//   release into Active — that path uses releaseEffect.
	// releaseEffect (one-shot): fired when this tier activates (Charging→Active).
	[Export] public PackedScene chargeStartEffect;
	[Export] public PackedScene chargeLoopEffect;
	[Export] public PackedScene chargeCancelEffect;
	[Export] public PackedScene releaseEffect;

	// Channeled-charge zone (summoner weapon). When set, ActionRunner spawns
	// this scene at the aim point the moment this tier becomes selected during
	// Charging, repositions it to the positional aim cursor every tick, and
	// frees it when Charging ends (abort OR activation). The scene root is a
	// GasCloud carrying a DamageZone + looping particle effect — its continuous
	// damage is authored in the scene; the runner only stamps the actor's team
	// (via GasCloud.InitializeChannel) so the channel hits enemies, not the
	// caster. The zone's radius uses `positionalAreaRadius` above. Null on a
	// tier means it has no channeled zone (the normal case). Pairs with
	// EAimType.Positional and a long `chargeTime` (the hold IS the channel).
	[Export] public PackedScene channelZoneScene;

	// Channeled-charge blood cost, in blood (= HP for the player) per second,
	// drained continuously while this tier is being charged. Distinct from
	// `bloodCost` above, which is a one-time spend at activation. If the actor
	// can't afford the next drain tick, ActionRunner aborts the charge. 0 (the
	// default) = free to hold. Only meaningful alongside a long `chargeTime`.
	[Export] public float channelBloodCostPerSecond = 0f;

	// Per-tier impact one-shots layered on top of the per-event
	// impactHealth/Armor/Lethal effects whenever the receiver flags the
	// matching trigger condition (OnCrit when the mob was dizzy or
	// untriggered, OnBackstab when the player also met PlayerData.backstabAngle
	// from behind). Authored per-tier so a weapon's flavor (sword zing vs club
	// thud) carries through to its crit/backstab payoff; null on a tier leaves
	// the base impact fx unaugmented.
	[Export] public PackedScene impactCritEffect;
	[Export] public PackedScene impactBackstabEffect;

	// Range multiplier at the given chargeT. Lerps from 1 (chargeT=0, fire
	// at the event's base range) to `chargedRangeScale` (chargeT=1).
	// Handlers multiply the event's authored range by this value.
	public static float SampleRangeScale(ItemAction tier, float chargeT)
	{
		if (tier == null) { return 1f; }
		return Mathf.Lerp(1f, tier.chargedRangeScale, Mathf.Clamp(chargeT, 0f, 1f));
	}

	// Resolved spread fraction at the given chargeT. Press value
	// `accuracySpread01` is divided by the lerp(1, chargedAccuracyScale,
	// chargeT) — so chargeT=0 returns the press value flat and chargeT=1
	// returns press / chargedAccuracyScale. A divisor of 1 (default)
	// leaves the press value unchanged across the whole hold.
	public static float SampleAccuracySpread(ItemAction tier, float chargeT)
	{
		if (tier == null) { return 0f; }
		float divisor = Mathf.Lerp(1f, tier.chargedAccuracyScale, Mathf.Clamp(chargeT, 0f, 1f));
		if (divisor <= 0f) { return tier.accuracySpread01; }
		return tier.accuracySpread01 / divisor;
	}

	// Number of swings in this tier's repeat combo (0 = not a repeat tier).
	public int RepeatCount => repeatActionOverrides?.Count ?? 0;

	// Per-swing override for the given repeat index, or null when the tier has no
	// repeat combo or the index is out of range (both resolve to "use the base
	// swing unchanged").
	public ActionRepeatOverride GetRepeat(int index)
	{
		if (repeatActionOverrides == null || index < 0 || index >= repeatActionOverrides.Count)
		{
			return null;
		}
		return repeatActionOverrides[index];
	}
}
