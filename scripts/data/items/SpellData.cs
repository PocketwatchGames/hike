using Godot;
using Godot.Collections;

// An attunable alchemy spell: a consumable that owns its reagent cost. Attuned to
// the single consumable slot at the alchemy campfire screen, then cast on demand —
// each cast spends `reagents` from the party material pool (stash + backpack)
// rather than a fixed stack, so its "ammo" is however many casts the pooled
// reagents currently afford (Cooking.CountAffordable / Player.GetSpellAmmo).
//
// Subclasses ConsumableData so the whole cast path — actionProfile, activeSprite,
// CreateState() -> ConsumableState — is inherited unchanged; a spell's runtime
// instance is an ordinary ConsumableState, so effects like SummonPetEffect that
// key off `primaryItem is ConsumableState` keep working.
[GlobalClass]
public partial class SpellData : ConsumableData
{
	// Flat, deterministic cast cost. Each entry's `count` is spent per cast;
	// RecipeInput.range is unused here (a spell cost is exact, not a fuzzy
	// cooking match). Reagent identity is matched up the ItemData.parent chain,
	// so a reagent naming a parent species-meat is paid by any descendant.
	[Export] public Array<RecipeInput> reagents = new();

	// Which spells the player begins knowing is authored on
	// WorldGenData.initialKnowledge (SpellTeachable entries), not here — that is
	// the single source of starting knowledge, learned through the same Teach()
	// path a spell scroll would use.
}
