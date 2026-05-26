using Godot;

// Authored data for a status effect held by a Player or Mob — icon, display
// name, lifecycle, fx, and a list of StatModifier entries the runtime
// composes into the actor's stat values while the effect is active.
//
// Most "this effect changes a stat" tunables now live as StatModifier entries
// on `modifiers` (move speed, damage scale, sense modifiers, temperature
// thresholds, etc.) — composed multiplicatively or additively per stat by
// the receiver. Lifecycle / identity / payload fields stay top-level
// because they don't fit the modifier shape.
[Tool]
[GlobalClass]
public partial class StatusEffectData : Resource
{
	[Export] public Texture2D icon;
	[Export] public StringName displayName;

	// Type tags this effect carries. Used by the resistance / modifier system
	// in two places: (1) buildup contributions feeding this effect scale by
	// the receiver's matching StatModifier entries — kun-kun's Dizzy
	// vulnerability lives there; (2) the per-second damagePerSecond DoT tick
	// scales by the same lookup so a Burning effect's burn tick respects a
	// Fire-resistant target. Default None = no tag-based scaling. Set Dizzy
	// on the dizzy effect, Fire|Damage on Burning, Poison|Damage on Poisoned.
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

	// Stat modifications granted to the actor while this effect is active.
	// Composed multiplicatively (or additively, per StatModifierUtil.
	// IsAdditive) with inherent and equipment modifiers when the actor's
	// composed value for any stat is queried. Authoring examples:
	//   { Damage,         0.0  } — dash i-frames (full damage immunity)
	//   { MoveSpeed,      0.75 } — Cold slows movement
	//   { ColdResist,    -25   } — Wet lowers cold threshold (additive)
	//   { Camouflage,    +5    } — armor / cloak bonus (additive)
	//   { Dizzy,          0.3  } — Dizzy buildup resistance (multiplicative)
	[Export] public Godot.Collections.Array<StatModifier> modifiers;

	// Per-second HP delta. Positive damages (poison), negative heals
	// (regeneration). Applied in 1-second chunks via the state's tick
	// accumulator so a 0.6 dps effect ticks as integer damage at the right
	// average rate rather than fractional damage every physics frame.
	[Export] public float damagePerSecond;

	// Fraction of each damage tick that bypasses armor and lands directly on
	// health, mirroring ContinuousDamageData.pierce. 1 (default) is "armor
	// doesn't soak this" — matches the historical status-DOT behavior where
	// poison ticks always chipped HP regardless of armor. Author less than 1
	// to let armor absorb a slice of the burn (e.g. Burning at 0.75 means
	// 25% of each damagePerSecond chunk chips armor while 75% goes through).
	// Ignored for heals (positive damagePerSecond * -1 sign means the delta
	// is a heal — armor never blocks regeneration).
	[Export(PropertyHint.Range, "0,1,0.01")] public float pierce = 1f;

	// Default seconds the effect lasts once timed. 0 = situational; the
	// gameplay system that armed the effect owns the lifetime and either
	// removes the state directly (e.g. Wet, which lives as long as the
	// player's wetness float stays above the disarm threshold) or arms a
	// removal timer via StatusEffectState.ArmTimer when one is wanted.
	[Export] public float duration;

	// Cap on simultaneous instances of this effect on a single actor. When a
	// new Add would push the count past this, the controller refreshes the
	// oldest existing instance's timer instead of appending a fresh state.
	// Default 99 is effectively "unbounded stacking" for poison-like effects;
	// authored 1 on consumable/situational effects (Well Fed, Hydrated, Wet,
	// Hot, Cold) so re-eating / re-drinking just extends the timer.
	[Export] public int maxStack = 99;

	// Audiovisual cues bound to the effect's lifecycle. `startFx` and `endFx`
	// are one-shot Fx scenes spawned on the actor at apply / remove. `loopFx`
	// is a looping Fx scene (Fx._loop = true) parented to the actor while the
	// effect is active and Stop()'d when it's removed.
	[Export] public PackedScene startFx;
	[Export] public PackedScene endFx;
	[Export] public PackedScene loopFx;

	// --- Buildup (Dark Souls-style pre-apply meter) ---
	// Damage data carries StatusEffectBuildup entries; each contribution
	// accumulates into the receiver's meter for this effect. When the meter
	// crosses 1, the controller applies the effect once and (per
	// clearBuildupOnApply) either zeros the meter or subtracts 1 so the next
	// stack can begin to fill.
	//
	// Seconds after the last buildup contribution before decay starts. Fresh
	// hits keep extending this window — only a quiet period drains the meter.
	[Export] public float buildupRemovalDelay = 0f;
	// Buildup units drained per second once the delay elapses. 0 = no decay
	// (buildup persists until the meter is filled).
	[Export] public float buildupRemovalSpeed = 0f;
	// When true, crossing the threshold zeros the meter instead of subtracting
	// 1. Used by non-stacking states (Dizzy) so a second apply isn't sitting
	// "half-charged" the instant the first one lands.
	[Export] public bool clearBuildupOnApply = false;
	// Trigger fired on the hit whose buildup contribution crossed the
	// threshold. Lets weapons author OnDizzy-style conditional modifiers (extra
	// knockback when this hit lands the dizzy, etc.) without the receiver
	// having to know about specific effects. Default None = no trigger fires.
	[Export] public EDamageTrigger applyTrigger = EDamageTrigger.None;

	// --- Mutual exclusion ---
	// Status effects to remove from the actor at the moment this effect is
	// applied. Used for douse-style relationships — Wet lists Burning so
	// stepping into water clears the burn the same frame the wet stack lands.
	// Removal is deep: matching active instances are EndFx'd and dropped; the
	// matching buildup meter is also zeroed so a partially-charged buildup
	// doesn't immediately re-fire.
	[Export] public Godot.Collections.Array<StatusEffectData> removesOnApply;

	// --- Animation override ---
	// Loop-animation slot to force on the actor while this effect is active.
	// EAnimation.None (default, -1) means the effect doesn't touch animation
	// and the actor's movement-state loop plays normally. Dizzy authors
	// EAnimation.Dizzy so the mob holds the dizzy clip for the duration.
	// First active effect with a non-None override wins (Mob's UpdateAnimation
	// reads StatusEffectController.LoopAnimOverride); priority is implicit
	// in effect-add order, which matches the rest of the controller's
	// "iterate the list once" composition.
	[Export] public EAnimation loopAnimOverride = EAnimation.None;

	// --- Behavior gates ---
	// When true, this effect counts as an "incapacitating" state — the actor
	// can't act (AI suppressed, no yell on hit) and any incoming hit clears
	// every effect with this flag (the generalized wake-from-dizzy rule).
	// Authored on Dizzy; future Frozen / Knocked-Down would also set it.
	[Export] public bool incapacitates = false;

	// Per-effect contribution to the receiver's `Vulnerable` score in [0, 1].
	// Composes across active effects as 1 - product(1 - v_i), i.e. multiple
	// vulnerabilities chain as independent probabilities — the actor's total
	// vulnerable is the chance "at least one effect makes the hit a crit."
	// Kept as a top-level field because the probabilistic-union math doesn't
	// fit the multiplicative / additive StatModifier shape. 0 (default) is
	// neutral; 1 pins vulnerable at 1 regardless of other effects. Dizzy
	// authors 1.0 so a dizzied mob is always crit on triggered hits.
	[Export(PropertyHint.Range, "0,1,0.01")] public float vulnerable = 0f;
}
