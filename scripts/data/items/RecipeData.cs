using Godot;
using Godot.Collections;

// A cooking recipe IS the thing it produces: a named day-long buff with a
// reagent cost — there is no output item. Eating it at the cookpot (picking it
// from the list, or a successful experimental cook) consumes `inputs` from the
// party material stash and applies `statusEffects` to the chosen character
// immediately, replacing whatever meal they last ate (the effects are marked
// EEffectCategory.Meal). Author them TimeOfDay/sunrise so the buff lasts the day,
// unless a shorter authored duration is the point (e.g. food poisoning).
//
// Standard and high-quality variants of the same dish are authored as two
// separate RecipeData files: the high-quality variant uses range=0 on each
// ingredient (must hit count exactly); the standard variant uses range>0 on
// some ingredients. Cooking.TryMatch picks the highest-priority match (ties
// broken by lowest total range) so exact ingredient counts unlock the
// high-quality dish, and looser counts fall through to the standard.
[GlobalClass]
public partial class RecipeData : Resource
{
	[Export] public ECampfireType campfireType;
	[Export] public StringName displayName;
	[Export] public string description;
	[Export] public Texture2D icon;
	// The buffs granted to a member leaving camp while this recipe is in the pot.
	[Export] public Array<StatusEffectData> statusEffects = new();
	[Export] public Array<RecipeInput> inputs = new();
	// Higher priority wins when multiple recipes match the same inputs.
	// Use to force a high-quality variant over a standard one, or to author
	// an explicit fallback recipe at a low priority. Ties resolve to the
	// recipe with the smallest sum of input ranges (most specific).
	[Export] public int priority = 0;
}
