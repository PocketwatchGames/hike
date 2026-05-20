using Godot;
using Godot.Collections;

// One charge tier within an ItemActionProfile. The charge timeline runs from
// press; each tier holds for its own `chargeTime` before the next tier in
// `chargedActions` (same comboIndex) takes over. On activation (release, or
// autoActivateAtMax when the final tier's window completes), the runner
// enters Active and walks `events` over `activeDurationSeconds`. The
// action's cooldownSeconds is written to the driving item's cooldownExpireMs
// at activation, gating re-firing of that specific item.
[GlobalClass]
public partial class ItemAction : Resource
{
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

	// Length of the Active phase in seconds. May be 0 — t=0 events fire and
	// Active exits the same tick.
	[Export] public float activeDurationSeconds = 0f;

	// Cooldown applied to the driving item after activation. Independent
	// per-item (a weapon's cooldown doesn't block other items).
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
	// this radius and its sweep speed scales with this value so any
	// positional tier (short or long reach) sweeps edge-to-center in a
	// consistent wall time. Authored per-tier — a charged AoE on a long-
	// range bow can still target close to the caster, and a thrown-
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
	// `base * chargedRangeScale`. 1.0 (default) = no within-tier ramp; the
	// tier fires at its event's authored range regardless of hold length.
	// `chargedAccuracyScale` DIVIDES `accuracySpread01` as `chargeT` runs
	// 0 → 1: at chargeT=0 spread = accuracySpread01; at chargeT=1 spread
	// = `accuracySpread01 / chargedAccuracyScale`. 1.0 (default) = no
	// tightening from holding. Asymmetric (divide, not multiply) because
	// accuracy improves toward zero — a multiplicative 0→1 ramp can't
	// reach pinpoint while still allowing a non-zero press value.
	[Export] public float chargedRangeScale = 1f;
	[Export] public float chargedAccuracyScale = 1f;

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
}
