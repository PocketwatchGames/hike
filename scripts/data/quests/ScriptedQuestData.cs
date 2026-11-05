using Godot;

// An objective that tracks nothing itself: it stays in the log until something
// completes it from outside — a conversation's `complete_quest:` cell or a world
// script. For a goal the game has no way to observe ("Find the Festering Man").
[GlobalClass]
public partial class ScriptedQuestData : QuestData
{
    public override QuestState CreateRuntime() => new ScriptedQuest(this);
}
