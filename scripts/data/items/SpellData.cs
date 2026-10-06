using Godot;
using Godot.Collections;

// An alchemy spell, carried in the backpack and cast from the hotbar. Its stack
// is its charges: each cast spends one (the timeline's DecrementStack, or
// SummonPetEffect for a pet). Which spells the player begins knowing is
// authored on WorldStartData.initialKnowledge (SpellTeachable entries), not here.
[GlobalClass]
public partial class SpellData : ItemData, IUsableItem
{
	// Cast timeline (charge tiers + cast events). Same shape as WeaponData's
	// actionProfile — the action runner doesn't distinguish.
	[Export] public ItemActionProfile actionProfile;
	public ItemActionProfile ActionProfile => actionProfile;

	// What one charge will cost to craft. Exact, not a fuzzy cooking match
	// (RecipeInput.range is unused); identity is matched up the ItemData.parent
	// chain, so a reagent naming a parent species-meat is paid by any descendant.
	// Nothing reads it yet — spell crafting is still to come.
	[Export] public Array<RecipeInput> reagents = new();

	protected override EItemCategory ComputeCategory() => EItemCategory.Usable;
}
