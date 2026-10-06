using Godot;

// A found scroll, read the moment it is picked up (IApplyOnPickup) rather than
// carried. Reading grants a single TeachableConcept. Authoring is one line in
// the .tres: assign `concept` and the scroll's display name is derived from it
// (TeachableConcept.ScrollTitle: "Scroll of <region name>", "Scroll of
// <language>", or a treasure map's own name). Its knowledge-stone sibling
// (KnowledgeStone) grants the same concepts via its own Complete.
//
// Read-side: SimState.GetItemDisplayName routes through here so the info panel
// and cook-discovery announcement stay in sync with the (post-identification)
// concept-derived name.
[GlobalClass]
public partial class ScrollData : ItemData, IApplyOnPickup
{
	[Export] public TeachableConcept concept;

	// Optional one-shot fx spawned on the player the first time this scroll
	// newly grants its concept (a re-read of an already-known scroll is silent).
	[Export] public PackedScene learnEffect;

	// Not a Material, so a field pickup takes an interact — reading is deliberate.
	protected override EItemCategory ComputeCategory() => EItemCategory.Usable;

	public bool ApplyOnPickup(Player player)
	{
		if (player == null || concept == null)
		{
			// No concept to grant: still spend the scroll so a misauthored one
			// doesn't become an un-pickable blocker in the world.
			return true;
		}
		// Teach returns true only on a new grant, so the fx gates on first learn.
		if (concept.Teach(player))
		{
			if (learnEffect != null)
			{
				Fx.Create(learnEffect, player, Vector3.Zero);
			}
			concept.OnLearnedFromScroll(player);
		}
		return true;
	}

	// Computed name used by SimState.GetItemDisplayName after the scroll
	// is identified. Falls back to the authored displayName field when the
	// concept ref is null or has no resolvable name, so an in-progress edit
	// doesn't render a blank inventory row.
	public string GetEffectiveDisplayName()
	{
		if (concept == null)
		{
			return displayName.ToString();
		}
		string title = concept.ScrollTitle();
		return string.IsNullOrEmpty(title) ? displayName.ToString() : title;
	}
}
