using Godot;
using Godot.Collections;

// How a buildup meter becomes the applied effect. Selects the StatusEffectController
// branch and gates which authoring fields the editor shows (see _ValidateProperty).
//   ThresholdCross — meter fills, crosses 1.0, applies a discrete instance, then
//                    auto-decays after a quiet period (Dizzy, hit-poison).
//   ContinuousArm  — meter IS the intensity in [0,1]; arms above armThreshold,
//                    releases below disarmThreshold (hysteresis). Externally driven
//                    by signed AddBuildup deltas, no auto-decay (Wet).
public enum EBuildupBehavior
{
	ThresholdCross = 0,
	ContinuousArm = 1,
}

// How a (non-buildup-armed) effect's lifetime ends. Author one value; the
// editor (_ValidateProperty) shows only the matching tunable.
//   Timed      — expires `duration` seconds after apply (0 = no auto-expiry; the
//                arming system or explicit Remove owns lifetime, e.g. Wet).
//   Persistent — never expires on its own; gameplay code calls Remove.
//   TimeOfDay  — expires at the next occurrence of `timeOfDayTarget`
//                (0 = sunrise), so a boon can last "until sunrise" regardless
//                of how long that is (the sleep-to-sunrise crosses it).
//   Sustained  — kept alive by an ongoing external condition that pauses the
//                timer while it holds (body temperature in a hot/cold zone).
//                `duration` is the grace window the effect lingers AFTER the
//                condition clears, not its lifetime — so it reads as persistent
//                on info panels but still shows the real grace countdown once
//                released. Timer mechanics match Timed; the difference is that
//                the seconds field is a post-source grace, not a fixed lifetime.
// Timed is value 0 so existing effects authored before this field (no stored
// durationType) keep their seconds-based behavior.
public enum EDurationType
{
	Timed = 0,
	Persistent = 1,
	TimeOfDay = 2,
	Sustained = 3,
}

// Presentation + lifetime bucket. Author one or more bits; [Flags] so clear ops can
// target a mask. HUD routes by category: only Transient shows in the mob status strip;
// the first Elite effect rides the elite badge.
//   Transient — ordinary timed / buildup combat states (default).
//   Permanent — long-term quirks / afflictions (no HUD strip yet).
//   Elite     — elite signature aura (badge only).
//   Meal      — granted by eating a cooked recipe. At most one per character (eating
//               again clears the previous via RemoveByCategory); the camp screen's
//               meal readout shows the active one. Combine with Transient so it still
//               displays on the normal HUD (e.g. Transient | Meal).
[System.Flags]
public enum EEffectCategory
{
	None = 0,
	Transient = 1 << 0,
	Permanent = 1 << 1,
	Elite = 1 << 2,
	Meal = 1 << 3,
}

// Forge "upgrade" slot ELIGIBILITY. A non-None value marks the effect as a forge
// upgrade and lists which equipment slots it MAY be applied to. [Flags] so one
// upgrade can suit more than one slot — a Flaming edge either weapon (Melee |
// Ranged). A forge (which has a fixed single slot) offers only upgrades eligible
// for its slot, then applies the chosen one to that ONE concrete slot — the applied
// slot is runtime state on the instance (StatusEffectState.appliedUpgradeSlot), NOT
// these flags. Slot exclusivity, weapon matching, and eviction all key off the
// concrete applied slot, so a multi-eligible upgrade never occupies more than the
// single slot it was granted into. The bits mirror the player's equipment. None
// (default) = an ordinary status effect.
[System.Flags]
public enum EUpgradeSlot
{
	None = 0,
	Melee = 1 << 0,
	Ranged = 1 << 1,
	Armor = 1 << 2,
}

// Authored data for a status effect on a Player, Mob, or item. Stat changes live as
// StatModifier entries on `modifiers`; feature payloads live in optional sub-resources
// (`dot`, `attackImpact`, `dashBurst`, `trail`, `weaponMod`; null = absent). Fields are
// split into editor [ExportGroup]s. A "character modifier" payload may sit on an item's
// effect (e.g. a cloak granting a dash burst) — it modifies the wearer.
[Tool]
[GlobalClass]
public partial class StatusEffectData : Resource
{
	[Export] public Texture2D icon;
	[Export] public StringName displayName;
	// Ability tell: the shared accent color a mob with this as an intrinsic
	// species effect (e.g. Venomous) stamps onto its body at spawn, so it reads
	// as "does this" regardless of region. Null = no tell. Only consulted for a
	// mob's intrinsic SpeciesData.statusEffects, not for transient combat states.
	// See AbilityTellData / ModelAnimator.ApplyPalette.
	[Export] public AbilityTellData abilityTell;
	// Inspector flavor text shown under the effect name on detail panels. Keep it short.
	[Export(PropertyHint.MultilineText)] public string description = "";

	// Forge upgrade slot ELIGIBILITY (which equipment slots this upgrade MAY apply to;
	// [Flags], so Melee | Ranged = "offerable by a melee or a ranged forge"). Non-None
	// marks this as a forge upgrade. The concrete slot it's actually applied to is
	// chosen by the forge and stored per-instance on StatusEffectState.appliedUpgradeSlot
	// — that's what drives exclusivity and weapon matching, not these flags. None = an
	// ordinary status effect. See EUpgradeSlot. (Weapon-mod composition ignores this.)
	[Export, CompactFlags] public EUpgradeSlot upgradeSlot = EUpgradeSlot.None;

	// ============================ Lifecycle ============================

	// Presentation + lifetime bucket — author one or more bits. See EEffectCategory.
	[ExportGroup("Lifecycle")]
	[Export, CompactFlags] public EEffectCategory category = EEffectCategory.Transient;

	// Suppress this effect's icon on the mob status strip (MobHUD) while still
	// showing it everywhere else (the player HUD, buildup bar). For a shared
	// effect whose mob application is ambient/circumstantial rather than a combat
	// state worth flagging over the mob — e.g. Wet, which every mob picks up just
	// by swimming or standing in the rain and which would otherwise clutter the
	// HUD. Player-side display is unaffected. Default false = shows normally.
	[Export] public bool hideOnMobHud = false;

	// Resistance/scaling tags. Buildup contributions feeding this effect and the
	// per-second `dot` tick both scale by the receiver's matching StatModifier (e.g.
	// Fire-resistance shrinks a Fire|Damage burn). None = no scaling.
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

	// How this effect's lifetime ends (Timed / Persistent / TimeOfDay). See EDurationType.
	[Export] public EDurationType durationType = EDurationType.Timed;

	// Timed / Sustained: seconds the effect lasts. For Timed, seconds after apply (0 =
	// the arming system owns lifetime, e.g. Wet lives off the wetness meter). For
	// Sustained, the grace window it lingers after the sustaining condition clears.
	[Export] public float duration;

	// TimeOfDay only: normalized time-of-day the effect expires at (0 = sunrise,
	// 0.25 = noon, 0.5 = sunset, 0.75 = midnight). The effect lasts
	// until the next occurrence of this time; the default (sunrise) is the common
	// "until sunrise" boon, which the sleep-to-sunrise then crosses. See
	// WorldState.TimeOfDay01.
	[Export(PropertyHint.Range, "0,1,0.001")] public float timeOfDayTarget = 0.0f;

	// Max simultaneous instances. A further Add refreshes the oldest instance's timer
	// instead of stacking. 1 makes re-applying just extend the timer (consumables, Wet).
	[Export] public int maxStack = 99;

	// For an effect designed to pile many stacks (Webbed — each stack multiplies
	// MoveSpeed so lingering ramps to a near-stop), fire startFx/loopFx only for the
	// FIRST live stack, not once per stack or per timer-refresh. Without this a
	// 6-stack slow spawns 6 start puffs and 6 overlapping loops. The HUD already
	// dedupes by data (one icon + count), so only the fx needs the guard. Default
	// false = every Add spawns its fx as usual.
	[Export] public bool suppressStackFx = false;

	// When true, fire the On Apply payloads then keep NO lingering state (a one-shot
	// blessing). False (default): added normally; modifiers / dot / duration apply over time.
	[Export] public bool instantaneous = false;

	// --- Buildup meter ---
	// DamageData StatusEffectBuildup entries accumulate into a per-effect meter. How the
	// meter applies is set by buildupBehavior (see EBuildupBehavior); the editor hides the
	// tunables that don't apply.
	[ExportSubgroup("Buildup")]
	[Export] public EBuildupBehavior buildupBehavior = EBuildupBehavior.ThresholdCross;
	// ThresholdCross: seconds of quiet before the meter starts decaying (fresh hits extend it).
	[Export] public float buildupRemovalDelay = 0f;
	// ThresholdCross: meter units drained per second after the delay. 0 = no decay.
	[Export] public float buildupRemovalSpeed = 0f;
	// ThresholdCross: zero the meter on a threshold cross instead of subtracting 1
	// (non-stacking states like Dizzy).
	[Export] public bool clearBuildupOnApply = false;
	// ThresholdCross: EDamageTrigger fired on the hit that crosses the threshold, letting
	// weapons react to landing the effect. None = none.
	[Export] public EDamageTrigger applyTrigger = EDamageTrigger.None;
	// ContinuousArm: meter level that arms the effect. HUD bar fills disarmThreshold→1.
	[Export(PropertyHint.Range, "0,1,0.01")] public float armThreshold = 0.5f;
	// ContinuousArm: meter level that releases it. Must be < armThreshold (hysteresis).
	[Export(PropertyHint.Range, "0,1,0.01")] public float disarmThreshold = 0.1f;

	// ============================ Fx ============================
	// startFx / endFx: one-shot Fx spawned on the actor at apply / remove. loopFx: looping
	// Fx parented while active, stopped on remove.
	[ExportGroup("Fx")]
	[Export] public PackedScene startFx;
	[Export] public PackedScene endFx;
	[Export] public PackedScene loopFx;

	// ============================ On Apply ============================
	// One-shot behaviors that fire the moment this effect is applied to an actor.

	// Fraction of the actor's max health restored on apply. 0 (default) = no heal.
	[ExportGroup("On Apply")]
	[Export(PropertyHint.Range, "0,1,0.01")] public float instantHealPercent = 0f;

	// Flat stamina units restored on apply (1 unit = 1 dash), clamped to the
	// actor's MaxStamina. Player-only — mobs have no stamina pool, so it's a
	// no-op there. 0 = none.
	[Export(PropertyHint.Range, "0,5,0.5,or_greater")] public float instantStaminaRestore = 0f;

	// Status effects removed from the actor on apply — douse relationships (Wet removes
	// Burning) and cleanse blessings (Restore). Also zeroes the matching buildup meter.
	// Applied even for `instantaneous` effects, which skip Add (where removal normally runs).
	[Export] public Godot.Collections.Array<StatusEffectData> removesOnApply;

	// ============================ Character Modifiers ============================
	// What the effect does to the character carrying it. May sit on an item's effect (it
	// modifies the wearer, composed across the actor's own + equipped-item effects).

	// Stat changes applied while active, composed with inherent + equipment modifiers.
	// Per-stat additive or multiplicative (StatModifierUtil.IsAdditive) — e.g. MoveSpeed
	// 0.75 (mult, Cold slow), ColdResist -25 (add), Damage 0.0 (mult, dash i-frames).
	[ExportGroup("Character Modifiers")]
	[Export] public Godot.Collections.Array<StatModifier> modifiers;
	// Managed read-mirror of `modifiers` — see MobData.ModifiersFlat. This is
	// the hottest of them all: StatusEffectController.FoldStat/FoldMask walk
	// every active effect on EVERY ComposeStat call, for both mobs and players.
	private StatModifier[] _modifiersFlat;
	public StatModifier[] ModifiersFlat => _modifiersFlat ??= StatModifierUtil.Flatten(modifiers);

	// Like `modifiers`, but each group contributes only while its runtime condition
	// holds (see ConditionalModifierData). Lets a permanent trait grant a situational
	// bonus — Runs Hot's low-stamina damage spike, Empathetic's stamina while a party
	// member is down — without churning a separate effect on/off. Evaluated by the
	// owning actor at compose time; actors with no evaluator (mobs, items) skip these.
	[Export] public Godot.Collections.Array<ConditionalModifierData> conditionalModifiers;

	// Per-second health-over-time (damage / heal / max-health decay). Null = none.
	// See DamageOverTimeData.
	[Export] public DamageOverTimeData dot;

	// On each landed Melee/Hitscan, fire a one-shot AoE burst at the impact point (elite
	// lightning aura). Null = none. See AreaBurstData.
	[Export] public AreaBurstData attackImpact;

	// Like attackImpact but fired on dash, with radial knockback (fairy-corpse scatter).
	// Null = none. See AreaBurstData.
	[Export] public AreaBurstData dashBurst;

	// While dashing, drop a hazard patch at the actor's feet on an interval.
	// Null = none. See MovementTrailData.
	[Export] public MovementTrailData trail;

	// Marks an "incapacitating" state: the actor can't act, and any incoming hit clears
	// every effect with this flag (wake-from-dizzy). Authored on Dizzy.
	[Export] public bool incapacitates = false;

	// Forces a loop animation while active (Dizzy → EAnimation.Dizzy). None = no override.
	// First active effect with an override wins.
	[Export] public EAnimation loopAnimOverride = EAnimation.None;

	// Per-effect crit-vulnerability in [0,1], composed across effects as 1 - prod(1 - v_i)
	// (independent probabilities). Top-level because that math isn't a StatModifier. Dizzy
	// authors 1.0 (always crit when triggered).
	[Export(PropertyHint.Range, "0,1,0.01")] public float vulnerable = 0f;

	// On-damaged reaction — when the wearer takes a hit whose tags overlap
	// `onDamagedTags`, self-apply `onDamagedEffect`. Thin Skinned uses this to arm a
	// short "+5% damage taken" debuff each time physical damage lands. Fired from the
	// actor's hit pipeline (StatusEffectController.TriggerOnDamaged); null effect = no
	// reaction. onDamagedTags == None matches any damaging hit.
	[ExportGroup("On Damaged")]
	[Export] public StatusEffectData onDamagedEffect;
	[Export, CompactFlags] public EStat onDamagedTags = EStat.Melee | EStat.Ranged;

	// Weapon-only payload — null on non-weapon effects. See WeaponModData.
	// The empty [ExportGroup] resets grouping so this stays ungrouped.
	[ExportGroup("")]
	[Export] public WeaponModData weaponMod;

	// Hide the buildup tunables that don't apply to the selected behavior, and hide
	// `duration` for ContinuousArm. Storage is preserved while hidden. Needs [Tool].
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
			nameof(duration) => isContinuous || (durationType != EDurationType.Timed && durationType != EDurationType.Sustained),
			nameof(timeOfDayTarget) => durationType != EDurationType.TimeOfDay,
			nameof(armThreshold) => !isContinuous,
			nameof(disarmThreshold) => !isContinuous,
			_ => false,
		};
		if (!hide) { return; }
		PropertyUsageFlags usage = property["usage"].As<PropertyUsageFlags>() & ~PropertyUsageFlags.Editor;
		property["usage"] = (int)usage;
	}
}
