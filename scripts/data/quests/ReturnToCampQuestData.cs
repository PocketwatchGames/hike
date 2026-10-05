using Godot;

// "Return to Camp" — triggered at nightfall (Sim.OnNightfall), satisfied by the
// party resting (Sim.OnRest).
// No progress display and no per-run state beyond its existence.
[GlobalClass]
public partial class ReturnToCampQuestData : QuestData
{
    public override QuestState CreateRuntime() => new ReturnToCampQuest(this);
}
