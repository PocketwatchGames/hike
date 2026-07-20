using System;

// "Return to Camp" — added at nightfall (GameClient subscribes Sim.OnNightfall)
// and satisfied by sleeping to sunrise, which fires Sim.OnNewDay. Purely
// event-driven: no progress display and no per-run state beyond its existence.
public class ReturnToCampQuest : QuestState
{
    public ReturnToCampQuest(ReturnToCampQuestData data) : base(data) { }

    public override void OnStart()
    {
        if (Sim.Current != null)
        {
            Sim.Current.OnNewDay += OnNewDay;
        }
    }

    public override void OnEnd()
    {
        if (Sim.Current != null)
        {
            Sim.Current.OnNewDay -= OnNewDay;
        }
    }

    void OnNewDay(int dayNumber) => Complete();
}
