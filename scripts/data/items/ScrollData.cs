using Godot;

// Consumable that grants a single TeachableConcept on use. Authoring is one
// line in the .tres: assign `concept` and the scroll's display name is
// auto-derived from the concept (e.g. "Scroll of <region name>", "Scroll of
// <recipe output>", "Scroll of <language>"). The action profile authors a
// LearnConcept event in its release tick alongside DecrementStack —
// ItemEventHandlers.DoLearnConcept resolves the concept reference back from
// the consuming item.
//
// Read-side: WorldSimState.GetItemDisplayName routes through here so the
// inventory row, info panel, and cook-discovery announcement all stay in
// sync with the (post-identification) concept-derived name.
[GlobalClass]
public partial class ScrollData : ConsumableData
{
    [Export] public TeachableConcept concept;

    // Computed name used by WorldSimState.GetItemDisplayName after the scroll
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
        // elsewhere (RegionData / ItemData displayName comments call out the
        // future Loc wiring). When Loc grows enough keys to be worth threading
        // through here, swap this for Loc.Format(Loc.Keys.scroll_of, conceptName).
        return $"Scroll of {conceptName}";
    }
}
