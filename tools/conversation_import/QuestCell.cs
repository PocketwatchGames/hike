using System;

// A quest started or completed from a sheet's action cell:
// `start_quest:<name>` / `complete_quest:<name>`, where <name> is the basename of
// a QuestData .tres under worlds/shared/quests/ or this world's own
// worlds/<world>/quests/. The importer emits the StartQuestAction /
// CompleteQuestAction itself: the quest is the content, the action is plumbing.
class QuestRef
{
	public ResRef Quest;
	// "StartQuestAction" or "CompleteQuestAction".
	public string ActionClass;
}

static class QuestCell
{
	// Resolves a parsed `start_quest:` / `complete_quest:` (ActionCommand).
	public static QuestRef Resolve(ActionCommand command, ResourceIndex index, string world, SheetRow row, Report report)
	{
		ResRef quest = index.Quest(world, command.Name);
		if (quest == null)
		{
			report.Error(row, $"unknown quest '{command.Name}' - no .tres by that basename under worlds/shared/quests/ or worlds/{world}/quests/");
			return null;
		}
		bool start = command.Kind == EActionCommand.StartQuest;
		return new QuestRef { Quest = quest, ActionClass = start ? "StartQuestAction" : "CompleteQuestAction" };
	}
}
