// "Learn Vyeshal (X/N)" — track how many components of a language the party has
// learned toward the full set. Read live from the (separately save-persisted)
// Knowledge stores each tick — the permanent party pool unioned with the active
// member's provisional field store — so it carries no state to serialize.
public class LearnLanguageQuest : QuestState
{
    LearnLanguageQuestData Config => Data as LearnLanguageQuestData;

    public LearnLanguageQuest(LearnLanguageQuestData data) : base(data) { }

    int LearnedCount()
    {
        Party party = World.Current?.WorldState?.SimState?.Party;
        LanguageData lang = Config?.language;
        if (party == null || lang == null)
        {
            return 0;
        }
        ELanguageComponents have = ELanguageComponents.None;
        if (party.Knowledge.LearnedLanguages.TryGetValue(lang, out ELanguageComponents banked))
        {
            have |= banked;
        }
        Knowledge active = party.Active?.Knowledge;
        if (active != null && active.LearnedLanguages.TryGetValue(lang, out ELanguageComponents field))
        {
            have |= field;
        }
        have &= ELanguageComponents.All;
        return System.Numerics.BitOperations.PopCount((uint)have);
    }

    static int TotalComponents =>
        System.Numerics.BitOperations.PopCount((uint)ELanguageComponents.All);

    public override void Tick(ulong nowMs)
    {
        if (Config?.language != null && LearnedCount() >= TotalComponents)
        {
            Complete();
        }
    }

    protected override int Current => LearnedCount();
    protected override int Target => TotalComponents;
}
