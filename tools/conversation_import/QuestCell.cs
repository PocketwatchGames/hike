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
	const string StartPrefix = "start_quest:";
	const string CompletePrefix = "complete_quest:";

	public static bool IsQuestToken(string token)
	{
		return token.StartsWith(StartPrefix, StringComparison.OrdinalIgnoreCase)
			|| token.StartsWith(CompletePrefix, StringComparison.OrdinalIgnoreCase);
	}

	public static QuestRef Parse(string token, ResourceIndex index, string world, SheetRow row, Report report)
	{
		bool start = token.StartsWith(StartPrefix, StringComparison.OrdinalIgnoreCase);
		string prefix = start ? StartPrefix : CompletePrefix;
		string name = token.Substring(prefix.Length).Trim();
		if (name.Length == 0 || name.IndexOfAny(new[] { ' ', '\t' }) >= 0)
		{
			report.Error(row, $"'{token}' must name exactly one quest - say '{prefix}find_festering_man'");
			return null;
		}
		ResRef quest = index.Quest(world, name);
		if (quest == null)
		{
			report.Error(row, $"unknown quest '{name}' - no .tres by that basename under worlds/shared/quests/ or worlds/{world}/quests/");
			return null;
		}
		return new QuestRef { Quest = quest, ActionClass = start ? "StartQuestAction" : "CompleteQuestAction" };
	}
}
