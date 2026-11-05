using Godot;

// Completes `quest` if it is in the log — the sheet's `complete_quest:<name>`
// cell. A quest not in the log is left alone (never started, or already done).
[GlobalClass]
public partial class CompleteQuestAction : ConversationAction
{
    [Export] public QuestData quest;

    public override void Execute(ConversationContext ctx)
    {
        if (quest == null)
        {
            return;
        }
        ctx.sim?.WorldState?.SimState?.QuestLog.Complete(quest);
    }
}
