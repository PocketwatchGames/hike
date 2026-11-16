// "Learn Vyeshal (X/100)" — the percent of a language the party has learned
// (Player.ComputeLanguageLearned). Read live from the save-persisted Knowledge
// stores each tick, so it carries no state to serialize.
public class LearnLanguageQuest : QuestState
{
    const int FULL = 100;

    LearnLanguageQuestData Config => Data as LearnLanguageQuestData;

    public LearnLanguageQuest(LearnLanguageQuestData data) : base(data) { }

    int LearnedPercent()
    {
        LanguageData lang = Config?.language;
        Player player = GameClient.Current?.Player;
        if (lang == null || player == null)
        {
            return 0;
        }
        return Godot.Mathf.FloorToInt(player.ComputeLanguageLearned(lang) * FULL);
    }

    public override void Tick(ulong nowMs)
    {
        if (Config?.language != null && LearnedPercent() >= FULL)
        {
            Complete();
        }
    }

    protected override int Current => LearnedPercent();
    protected override int Target => FULL;
}
