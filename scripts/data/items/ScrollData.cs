using Godot;

// A found scroll — read on the spot when picked up out of the world (a chest
// drop, ground loot) rather than carried. Reading grants a single
// TeachableConcept. Authoring is one line in the .tres: assign `concept` and the
// scroll's display name is auto-derived from it (e.g. "Scroll of <region name>",
// "Scroll of <recipe output>", "Scroll of <language>").
//
// Applies on pickup (IApplyOnPickup) rather than through a consumable Use
// timeline — the concept is self-contained (Teach), so the scroll doesn't need
// to be a ConsumableData. Its knowledge-stone sibling (KnowledgeStone) grants
// the same concepts via its own Complete; this is the loot-shaped counterpart.
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

	// Equipment (not Material) so field pickup requires an interact instead of
	// auto-grabbing on contact — reading is a deliberate action.
	protected override EItemCategory ComputeCategory() => EItemCategory.Equipment;

	public bool ApplyOnPickup(Player player, Vector3 worldPosition)
	{
		if (player == null || concept == null)
		{
			// No concept to grant: still consume the scroll so a misauthored one
			// doesn't become an un-pickable blocker in the world.
			return true;
		}
		// Teach returns true only on a new grant, so the fx gates on first learn.
		if (concept.Teach(player) && learnEffect != null)
		{
			Fx.Create(learnEffect, player, Vector3.Zero);
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
		string conceptName = concept.GetDisplayName();
		if (string.IsNullOrEmpty(conceptName))
		{
			return displayName.ToString();
		}
		// Hardcoded format — same StringName-as-display-text convention used
		// elsewhere (RegionData / ItemData displayName).
		return $"Scroll of {conceptName}";
	}
}
