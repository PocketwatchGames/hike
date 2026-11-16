using Godot;

// Teaches one lesson of a single language through Player.LearnLanguageLesson —
// `percent` of it, under the number `lesson` (see LanguageData).
// [Tool]: in the item closure (ItemEvent → ItemData) — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class LanguageTeachable : TeachableConcept
{
    [Export] public LanguageData language;
    // Which lesson this is. Two sources with the same number are the same
    // lesson, so only the first one read teaches anything.
    [Export] public int lesson;
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
        return player.LearnLanguageLesson(language, lesson, percent);
    }

    public override bool IsKnown(Player player)
    {
        if (player == null || language == null)
        {
            return false;
        }
        return player.GetLessonPercent(language, lesson) >= percent;
    }
}
