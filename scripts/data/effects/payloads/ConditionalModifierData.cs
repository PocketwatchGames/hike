using Godot;

// A group of StatModifiers on a StatusEffectData that contribute only while a
// runtime `condition` holds — the StatusEffectController folds them in exactly
// like the effect's own `modifiers`, but skips the group whenever the owning
// actor's condition evaluator (Player.EvaluateTraitCondition) reports the
// condition inactive. This lets a permanent trait grant a SITUATIONAL bonus
// (Runs Hot's 2× damage under 25% stamina, Empathetic's stamina while a party
// member is down) without adding/removing a whole effect as the condition flips:
// the check happens at stat-compose time, so the modifier appears and vanishes
// for free. Composition op (multiply / add) is per-stat as usual (StatModifierUtil).
//
// [Tool] so the [Tool] parent StatusEffectData can bind it as its real type in the
// editor — otherwise it loads as a base Resource and the typed field reads empty
// (see the Key Conventions note in CLAUDE.md).
[Tool]
[GlobalClass]
public partial class ConditionalModifierData : Resource
{
	// The predicate these modifiers are gated on. See ETraitCondition.
	[Export] public ETraitCondition condition = ETraitCondition.StaminaBelowFraction;

	// Free parameter interpreted per-condition: for StaminaBelowFraction it's the
	// stamina fraction [0,1] the current value must fall below (0.25 = "under 25%").
	// Conditions that take no argument (PartyMemberFallen) ignore it.
	[Export(PropertyHint.Range, "0,1,0.01")] public float parameter = 0.25f;

	// Composed (multiplicatively or additively, per each entry's stat) into the
	// actor's stat only while `condition` holds.
	[Export] public Godot.Collections.Array<StatModifier> modifiers;
	// Managed read-mirror of `modifiers` — see MobData.ModifiersFlat.
	private StatModifier[] _modifiersFlat;
	public StatModifier[] ModifiersFlat => _modifiersFlat ??= StatModifierUtil.Flatten(modifiers);
}
