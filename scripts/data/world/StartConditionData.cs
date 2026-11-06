using Godot;
using Godot.Collections;

// The state a New Game's party wakes in: hurt, hungry, unconscious. Applied once to
// every starting member when a New Game spawns (never on a load, never to a
// recruit), after their traits, so a max-health effect here is already counted
// when `health` is taken as a fraction of it.
//
// Health comes back at the first rest like any other wound; an injury that must
// last until treated is an effect in `effects`.
[GlobalClass]
public partial class StartConditionData : Resource
{
	// Fraction of max health the member starts at.
	[Export(PropertyHint.Range, "0.01,1,0.01")] public float health = 1f;

	// Stages of deprivation (see Player.Needs): 0 = fine, 1 = stamina penalty
	// begins, 2 = max health starts falling.
	[Export(PropertyHint.Range, "0,4,0.05,or_greater")] public float hunger;
	[Export(PropertyHint.Range, "0,4,0.05,or_greater")] public float thirst;
	[Export(PropertyHint.Range, "0,4,0.05,or_greater")] public float fatigue;

	// Applied once, unlike PlayerState.traits, which every spawn re-applies.
	[Export] public Array<StatusEffectData> effects = new();
}
