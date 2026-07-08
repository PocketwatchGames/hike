// A runtime predicate a ConditionalModifierData gates its StatModifiers on. The
// owning actor maps each value to its live state (see Player.EvaluateTraitCondition);
// an actor with no evaluator (Mob, item-side controllers) reads every condition as
// false, so a conditional trait simply contributes nothing there. Wire values are
// stable — append new conditions, never reassign existing ones.
public enum ETraitCondition
{
	// Current stamina is below ConditionalModifierData.parameter (a fraction of max
	// stamina). Drives Runs Hot's low-stamina damage spike.
	StaminaBelowFraction = 0,

	// At least one party member has fallen (PlayerState.IsDead) and awaits rescue.
	// Drives Empathetic's stamina boost. Ignores `parameter`.
	PartyMemberFallen = 1,
}
