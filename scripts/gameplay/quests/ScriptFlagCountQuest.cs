// "<title> (X/N)" — counts how many of its authored bool flags are set in the
// (save-persisted) ScriptVariableBank. Read live each tick, so it carries no
// state to serialize; completes once all flags are set.
public class ScriptFlagCountQuest : QuestState
{
    ScriptFlagCountQuestData Config => Data as ScriptFlagCountQuestData;

    public ScriptFlagCountQuest(ScriptFlagCountQuestData data) : base(data) { }

    int SetCount()
    {
        ScriptVariableBank bank = Sim.Current?.WorldState?.SimState?.ScriptVars;
        string[] flags = Config?.flags;
        if (bank == null || flags == null)
        {
            return 0;
        }
        int count = 0;
        foreach (string flag in flags)
        {
            if (bank.GetBool(flag))
            {
                count++;
            }
        }
        return count;
    }

    int TotalFlags => Config?.flags?.Length ?? 0;

    public override void Tick(ulong nowMs)
    {
        if (TotalFlags > 0 && SetCount() >= TotalFlags)
        {
            Complete();
        }
    }

    protected override int Current => SetCount();
    protected override int Target => TotalFlags;
}
