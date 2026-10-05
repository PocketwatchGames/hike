using System;

// "Return to Camp" — added at nightfall (Sim.OnNightfall) and satisfied by the
// party resting (Sim.OnRest) — staying up until dawn doesn't count. Purely
// event-driven: no progress display and no per-run state beyond its existence.
public class ReturnToCampQuest : QuestState
{
    public ReturnToCampQuest(ReturnToCampQuestData data) : base(data) { }

    public override void OnStart()
    {
        if (Sim.Current != null)
        {
            Sim.Current.OnRest += Complete;
        }
    }

    public override void OnEnd()
    {
        if (Sim.Current != null)
        {
            Sim.Current.OnRest -= Complete;
        }
    }
}
