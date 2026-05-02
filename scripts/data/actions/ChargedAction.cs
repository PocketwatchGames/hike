using Godot;
using Godot.Collections;

// One charge tier within an ItemActionProfile. The charge timeline runs from
// press; reaching this action's chargeTime makes it the selected tier. On
// activation (release, or autoActivateAtMax), the runner enters Active and
// walks `events` over `activeDurationSeconds`. The action's cooldownSeconds
// is written to the driving item's cooldownExpireMs at activation, gating
// re-firing of that specific item.
[GlobalClass]
public partial class ChargedAction : Resource
{
	[Export] public EActionVerb verb = EActionVerb.None;

	// Hold time required to "reach" this tier. The lowest tier is typically 0.
	// Tiers must be authored in ascending chargeTime order.
	[Export] public float chargeTime = 0f;

	// Length of the Active phase in seconds. May be 0 — t=0 events fire and
	// Active exits the same tick.
	[Export] public float activeDurationSeconds = 0f;

	// Cooldown applied to the driving item after activation. Independent
	// per-item (a weapon's cooldown doesn't block other items).
	[Export] public float cooldownSeconds = 0f;

	// Events fired during Active, on a timeline measured from activateMs.
	[Export] public Array<ItemEvent> events = new();

	// Phase 3 hooks (declared now so the runner doesn't need a re-export).
	// readyEvents fire when this tier becomes the selected tier during
	// charging. abortEvents fire when this tier's Active phase aborts.
	[Export] public Array<ItemEvent> readyEvents = new();
	[Export] public Array<ItemEvent> abortEvents = new();

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

	// Per-tier requirements. Action only selectable if all evaluate true.
	// Phase 3 SelectTier ignored requirements; phase 4 walks them and falls
	// back to a lower tier on failure.
	[Export] public Array<ActionRequirement> requirements = new();

	// Active-phase abort policy. Both flags only consulted while Active —
	// charging always cancels on release/sneak/damage. Defaults match the
	// "committed combat action" pattern (player can't bail, damage staggers).
	[Export] public bool canAbort = false;
	[Export] public bool canInterrupt = true;

	// Per-action continuous-charge curves. Sampled against the action's
	// `chargeT` stashed on PlayerAction at activation. Curve outputs are
	// multipliers/spread fractions in [0, 1]; handlers apply them to the
	// event's base values. Null curve = "no scaling" (1.0 for range, 0.0
	// for accuracy spread). Used by Hitscan for bow accuracy/range; Melee
	// ignores them.
	[Export] public Curve rangeScaleCurve;
	[Export] public Curve accuracyScaleCurve;

	// When > 0, defines the hold window for `chargeT` sampling INDEPENDENTLY
	// of tier chargeTime. A single-tier bow with chargeTime=0 + maxChargeSeconds=1.5
	// fires on any release but scales the curves across [0, 1.5]. When 0 (the
	// default), `chargeT` is normalized against the profile's top-tier chargeTime
	// — works for multi-tier weapons without per-action curves.
	[Export] public float maxChargeSeconds = 0f;

	// Per-tier charge audio/effect lifecycle, managed by ActionRunner. Each is
	// a PackedScene wrapping an EffectOneShot.
	//
	// chargeStartEffect (one-shot): fired when this tier becomes the selected
	//   tier during Charging — at press for tier 0 with chargeTime=0, or at the
	//   moment the charge timer crosses chargeTime for higher tiers. A multi-
	//   tier weapon's "light" tier typically leaves these null and only its
	//   "heavy" tier configures them, so the windup audio fires only when the
	//   player has actually committed to charging.
	// chargeLoopEffect (loop, set EffectOneShot._loop=true): instantiated when
	//   this tier becomes selected and Stop()'d on tier change or Charging exit.
	// chargeCancelEffect (one-shot): fired when Charging aborts (player cancel
	//   or interrupt) while this tier is selected. NOT fired on a successful
	//   release into Active — that path uses releaseEffect.
	// releaseEffect (one-shot): fired when this tier activates (Charging→Active).
	[Export] public PackedScene chargeStartEffect;
	[Export] public PackedScene chargeLoopEffect;
	[Export] public PackedScene chargeCancelEffect;
	[Export] public PackedScene releaseEffect;
}
