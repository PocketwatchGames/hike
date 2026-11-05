using Godot;

// Adds `quest` to the log — the sheet's `start_quest:<name>` cell. Does nothing
// while the quest is already active, or once it has been completed (its
// completedVariable is set), so a repeatable response cannot restart it.
[GlobalClass]
public partial class StartQuestAction : ConversationAction
{
    [Export] public QuestData quest;

    public override void Execute(ConversationContext ctx)
    {
        SimState sim = ctx.sim?.WorldState?.SimState;
        if (quest == null || sim == null)
        {
            return;
        }
        if (sim.QuestLog.Find(quest) != null)
        {
            return;
        }
        StringName completed = quest.completedVariable;
        if (completed != null && !completed.IsEmpty && sim.ScriptVars.GetBool(completed))
        {
            return;
        }
        sim.QuestLog.Add(quest.CreateRuntime());
    }
}
