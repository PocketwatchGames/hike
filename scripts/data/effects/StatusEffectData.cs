using Godot;
using Godot.Collections;

// How an effect's buildup meter relates to its applied state. Selects which
// branch StatusEffectController takes when a contribution lands and gates
// which authoring fields the editor surfaces (see _ValidateProperty).
//
//   ThresholdCross  — classic Dark-Souls-style: the meter fills, crosses 1.0,
//                     applies a discrete instance, then auto-decays after a
//                     quiet period. Used for damage-buildup effects (Dizzy,
//                     Poison-from-hits, future Frozen-from-cold-hits).
//
//   ContinuousArm   — the meter IS the effect intensity in [0, 1]. Arms an
//                     instance when the meter rises above armThreshold,
//                     releases when it falls below disarmThreshold (hysteresis
//                     prevents flapping). The armed instance's duration timer
//                     stays paused — the meter, not a countdown, controls
//                     lifecycle. External code drives the meter via signed
//                     AddBuildup deltas (no auto-decay). Used for Wet, where
//                     rain / water / drying are the source signals.
public enum EBuildupBehavior
{
	ThresholdCross = 0,
	ContinuousArm = 1,
}

// How an effect is presented and how long it's meant to live. A single effect
// authors exactly one category, but the type is [Flags] so removal/clear ops
// can target a mask of categories (e.g. a cure consumable clears
// Transient | Permanent while leaving Elite signatures alone). The HUD routes
// by category: only Transient renders in the mob's fading status strip; the
// first Elite-category effect rides the elite health-bar badge instead.
//
//   Transient  — ordinary timed / buildup-driven combat states (poison, wet,
//                dizzy, food buffs). The default so existing effects keep
//                showing in the status strip without re-authoring.
//   Permanent  — long-term character quirks / afflictions the actor carries
//                indefinitely (player traits, lasting injuries). Not yet
//                surfaced in any HUD strip; reserved for the future panel.
//   Elite      — an elite mob's signature aura. Shown only in the elite badge,
//                never the strip.
[System.Flags]
public enum EEffectCategory
{
	None = 0,
	Transient = 1 << 0,
	Permanent = 1 << 1,
	Elite = 1 << 2,
}

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
	// Inspector multiline flavor text. Shown on per-target detail panels
	// (ItemInfoPanel's status section, future actor inspectors) under the
	// effect's name. Plain string — keep it short ("Wet. Slows drying.
	// Lowers cold tolerance.") so it fits next to the progress bar.
	[Export(PropertyHint.MultilineText)] public string description = "";

	// Presentation + lifecycle bucket. See EEffectCategory. Author exactly one
	// bit; default Transient keeps existing effects in the mob status strip.
	[Export, CompactFlags] public EEffectCategory category = EEffectCategory.Transient;

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

	// --- On-attack-impact burst ---
	// When `attackImpactDamage` is set, every Melee / Hitscan attack the
	// carrying actor lands fires a one-shot AoE damage burst centered on the
	// hit's impact position — the elite lightning aura's "every strike
	// crackles" payload. The burst is self-contained on the effect (not the
	// actor's damage profiles), so any mob or the player deals it just by
	// holding this status. Author `attackImpactDamage` with Electrical|Damage
	// tags + healthDamage (knockback / hitstun optional). `attackImpactRadius`
	// is the sphere radius in meters; `attackImpactFx` is the one-shot
	// visual + sound spawned at the impact point (author it to read at the
	// radius so the AoE reach is legible). Null damage AND null fx = the
	// effect contributes no impact burst. Friendly fire is off — the burst
	// skips hurtboxes on the carrier's own team, like a normal swing.
	[Export] public DamageData attackImpactDamage;
	[Export(PropertyHint.Range, "0.5,10,0.5,or_greater")] public float attackImpactRadius = 2f;
	[Export] public PackedScene attackImpactFx;

	// --- On-dash burst ---
	// Direct analog of the attack-impact burst above, fired every time the
	// carrying actor dashes (Player.ApplyMotion → StatusEffectController.
	// TriggerDashBurst) instead of on each landed attack. The burst is a
	// one-shot AoE centered on the dashing actor that pushes nearby targets
	// directly away (radial knockback) and feeds whatever the payload's
	// StatusEffectBuildup entries author — the fairy-corpse buff uses this to
	// make a buffed dash scatter and dizzy the surrounding crowd. Self-
	// contained on the effect (not the actor's damage profiles) like the
	// attack-impact burst, so any actor deals it just by holding this status.
	// Author `dashBurstDamage` with the knockback / Dizzy-buildup payload (it
	// may carry zero healthDamage — knockback and buildup still apply);
	// `dashBurstRadius` is the sphere radius in meters; `dashBurstFx` is the
	// one-shot visual + sound spawned at the actor. Null damage AND null fx =
	// the effect contributes no dash burst. Friendly fire is off — the burst
	// skips the carrier's own hurtbox and (for mob carriers) their team.
	[Export] public DamageData dashBurstDamage;
	[Export(PropertyHint.Range, "0.5,10,0.5,or_greater")] public float dashBurstRadius = 3f;
	[Export] public PackedScene dashBurstFx;

	// --- Movement trail ---
	// When `trailZoneScene` is set, the carrying actor drops a copy of that
	// scene at its feet on a fixed interval while it's dashing or sprinting
	// (Player.cs → StatusEffectController.TickMovementTrail). Author it as a
	// self-expiring hazard — a `GasCloud` rooting a `DamageZone` + looping Fx,
	// like `flame_trail.tscn` — so each dropped patch owns its own lifetime,
	// damage ticking, and visuals; the controller just spawns and forgets. The
	// fairy-corpse buff drops burning fairy-fire this way, leaving a damaging
	// wake behind a sprint. `trailDropInterval` is the spacing in seconds
	// between drops (smaller = denser, more overlap, more live patches). Null
	// scene = the effect leaves no trail.
	[Export] public PackedScene trailZoneScene;
	[Export(PropertyHint.Range, "0.05,2,0.01,or_greater")] public float trailDropInterval = 0.2f;

	// --- Buildup meter ---
	// Damage data carries StatusEffectBuildup entries; each contribution
	// accumulates into the receiver's meter for this effect. How the meter
	// translates into the applied state is selected by `buildupBehavior`.
	// See EBuildupBehavior for the two branches; the editor hides whichever
	// tunables don't apply to the selected behavior.
	[Export] public EBuildupBehavior buildupBehavior = EBuildupBehavior.ThresholdCross;

	// --- ThresholdCross tunables ---
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

	// --- ContinuousArm tunables ---
	// Meter value at or above which an instance is armed (the effect starts).
	// Hysteresis with disarmThreshold prevents flapping when the meter brushes
	// the boundary on a low-intensity signal (drizzle). The HUD progress bar
	// fills from disarmThreshold (empty) to 1.0 (full) — set armThreshold low
	// (or to disarmThreshold) to make the icon appear as soon as any meaningful
	// signal lands.
	[Export(PropertyHint.Range, "0,1,0.01")] public float armThreshold = 0.5f;
	// Meter value at or below which the armed instance is released. Must be
	// strictly less than armThreshold for the hysteresis to do anything; a
	// gap of ~5–10% of the [0, 1] range is usually enough.
	[Export(PropertyHint.Range, "0,1,0.01")] public float disarmThreshold = 0.1f;

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

	// Hide buildup tunables whose owning behavior isn't selected, and hide
	// `duration` for ContinuousArm (the meter, not a timer, controls
	// lifecycle). Storage is preserved while hidden, so flipping the behavior
	// back doesn't lose previously-authored values. `[Tool]` is required for
	// this to fire in the editor.
	public override void _ValidateProperty(Dictionary property)
	{
		string name = property["name"].AsString();
		bool isContinuous = buildupBehavior == EBuildupBehavior.ContinuousArm;
		bool hide = name switch
		{
			nameof(buildupRemovalDelay) => isContinuous,
			nameof(buildupRemovalSpeed) => isContinuous,
			nameof(clearBuildupOnApply) => isContinuous,
			nameof(applyTrigger) => isContinuous,
			nameof(duration) => isContinuous,
			nameof(armThreshold) => !isContinuous,
			nameof(disarmThreshold) => !isContinuous,
			_ => false,
		};
		if (!hide) { return; }
		PropertyUsageFlags usage = property["usage"].As<PropertyUsageFlags>() & ~PropertyUsageFlags.Editor;
		property["usage"] = (int)usage;
	}
}
