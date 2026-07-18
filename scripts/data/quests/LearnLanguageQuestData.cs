using Godot;

// "Learn Vyeshal (X/N)" — track how many components of a language the party has
// learned toward the full set. Progress is a Counter, recomputed live from the
// (save-persisted) Knowledge stores, so nothing quest-side needs serializing
// beyond the quest's existence.
[GlobalClass]
public partial class LearnLanguageQuestData : QuestData
{
    // The language whose components must all be learned.
    [Export] public LanguageData language;

    public override QuestState CreateRuntime() => new LearnLanguageQuest(this);
}
