using Godot;

// "Return to Camp" — triggered at nightfall (GameClient subscribes
// World.OnNightfall), satisfied by sleeping to sunrise at camp (World.OnNewDay).
// No progress display and no per-run state beyond its existence.
[GlobalClass]
public partial class ReturnToCampQuestData : QuestData
{
    public override QuestState CreateRuntime() => new ReturnToCampQuest(this);
}
