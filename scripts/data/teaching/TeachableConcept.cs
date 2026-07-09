using Godot;

// A single thing a teaching source can grant to a player — a slice of a
// language, a recipe, a map-region location, and (later) skills. Polymorphic
// so any teaching source (KnowledgeStone, scroll, NPC dialogue) carries a
// flat list of these and doesn't need to branch on what it teaches; subclasses
// implement `Teach` against the appropriate world-state collection and
// `GetDisplayName` for UI names that pull from the concept (scroll naming,
// future "you learned X" toasts).
//
// Non-abstract base (with `GD.PushError` fallbacks) so `[GlobalClass]`
// surfaces it in the inspector's resource-picker dropdown — Godot's editor
// won't list abstract types as creatable resources. Subclasses must also be
// `[GlobalClass]` to appear in the picker.
[GlobalClass]
public partial class TeachableConcept : Resource
{
    // Player-facing name of the thing this concept represents. Drives the
    // scroll name ("Scroll of <name>") and any future "Learned: <name>"
    // toast. Returns empty when the underlying data ref is null so callers
    // can fall back gracefully.
    public virtual string GetDisplayName()
    {
        GD.PushError($"TeachableConcept.GetDisplayName not overridden by {GetType().Name}");
        return string.Empty;
    }

    // Apply this concept's grant to `player`. Returns true only when the call
    // produced a new addition (newly-learned component, newly-discovered
    // recipe, newly-revealed region) — false on a re-teach. Callers gate
    // first-learn fx on the OR of returns across a concept array.
    public virtual bool Teach(Player player)
    {
        GD.PushError($"TeachableConcept.Teach not overridden by {GetType().Name}");
        return false;
    }

    // Read-only counterpart of Teach: true when this concept is ALREADY held in
    // full — checked against the same combined knowledge Teach dedups on (party
    // pool ∪ the active member's provisional field store). Lets a teaching
    // source (knowledge stone) dim its map marker once the party has nothing
    // left to learn from it. Base returns false (never-known) so an un-overridden
    // concept safely reads as still-teachable rather than silently dimming.
    public virtual bool IsKnown(Player player)
    {
        return false;
    }
}
