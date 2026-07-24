using Godot;
using Godot.Collections;

// An attunable alchemy spell: attuned to the single spell slot at the alchemy
// campfire screen, then cast on demand. Each cast spends `reagents` from the
// party material pool (stash + backpack) rather than a fixed stack, so its
// "ammo" is however many casts the pooled reagents currently afford
// (Cooking.CountAffordable / Player.GetSpellAmmo). Runs as a SpellState (which
// tracks a summon spell's pet). Which spells the player begins knowing is
// authored on WorldGenData.initialKnowledge (SpellTeachable entries), not here.
[GlobalClass]
public partial class SpellData : ItemData, IUsableItem
{
	// Cast timeline (charge tiers + cast events). Same shape as WeaponData's
	// actionProfile — the action runner doesn't distinguish.
	[Export] public ItemActionProfile actionProfile;
	public ItemActionProfile ActionProfile => actionProfile;

	// Flat, deterministic cast cost. Each entry's `count` is spent per cast;
	// RecipeInput.range is unused here (a spell cost is exact, not a fuzzy
	// cooking match). Reagent identity is matched up the ItemData.parent chain,
	// so a reagent naming a parent species-meat is paid by any descendant.
	[Export] public Array<RecipeInput> reagents = new();

	// Attuned into the single spell slot (the "Equipment" category / EquipSlotKind).
	protected override EItemCategory ComputeCategory() => EItemCategory.Equipment;

	public override ItemState CreateState()
	{
		return new SpellState(this);
	}
}
