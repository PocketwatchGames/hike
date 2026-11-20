using Godot;

// Teaches what a scroll item teaches — a treasure map's chart, a region, a
// language lesson — without restating it. The scroll's own concept is the
// lesson, so learning it here and reading the scroll later count once.
// [Tool]: in the item closure (ItemEvent → ItemData) — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
[AuthoringKind("Map / scroll")]
public partial class ScrollTeachable : TeachableConcept
{
    [Export] public ScrollData scroll;

    public override string GetDisplayName()
    {
        return scroll?.concept?.GetDisplayName() ?? string.Empty;
    }

    public override bool Teach(Player player)
    {
        return scroll?.concept != null && scroll.concept.Teach(player);
    }

    public override bool IsKnown(Player player)
    {
        return scroll?.concept == null || scroll.concept.IsKnown(player);
    }
}
