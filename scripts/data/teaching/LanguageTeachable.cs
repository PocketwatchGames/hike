using Godot;

// Teaches `percent` of a single language through Player.LearnLanguageLesson.
// The lesson is identified by THIS resource (its path — a scroll's embedded
// concept, an authored action file), so reading it twice, or two copies of one
// scroll, teaches once. An inscription teaches its own lesson without one of
// these (InscriptionData.percent).
// [Tool]: in the item closure (ItemEvent → ItemData) — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
[AuthoringKind("Language")]
public partial class LanguageTeachable : TeachableConcept
{
    [Export] public LanguageData language;
    // How much of the language it teaches. The default, the whole tongue,
    // keeps a "learn the language" source authoring-cheap.
    [Export(PropertyHint.Range, "0,100,1")] public float percent = LanguageData.FULL_PERCENT;

    public override string GetDisplayName()
    {
        return language != null ? language.displayName.ToString() : string.Empty;
    }

    public override bool Teach(Player player)
    {
        if (player == null || language == null)
        {
            return false;
        }
        return player.LearnLanguageLesson(language, this, percent);
    }

    public override bool IsKnown(Player player)
    {
        if (player == null || language == null)
        {
            return false;
        }
        return player.GetLessonPercent(language, this) >= percent;
    }
}
