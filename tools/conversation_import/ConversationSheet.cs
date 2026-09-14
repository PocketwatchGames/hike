using System;
using System.Collections.Generic;
using System.IO;

// One row of an authored conversation sheet, columns resolved by header name.
class SheetRow
{
	public string File;
	public int Line;
	public string Character = "";
	public string Player = "";
	public string Key = "";
	public string Goto = "";
	public string Language = "";
	public string Condition = "";
	public string Action = "";
	public string Text = "";

	public bool IsPlayer => Player.Trim().Length > 0;
	public string Where => $"{Path.GetFileName(File)}:{Line}";
}

// One NPC turn. `LineKeys` are the loc keys generated for this branch's
// paragraphs, in order.
class SheetBranch
{
	public string Name;
	public string Language;
	public List<string> LineKeys = new List<string>();
	public List<string> Actions = new List<string>();
	public string ExitGroup = "";
	public bool IsPrimaryGroupEntry;
	// First row of the branch, and the rows that claimed its one-per-branch
	// fields - kept so a second claim can name where the first one was.
	public SheetRow Row;
	public SheetRow GotoRow;
	public SheetRow LanguageRow;
}

class SheetResponse
{
	public string LocKey = "";
	public string Language;
	public string Condition;
	public string Destination = "";
	public List<string> Actions = new List<string>();
	public SheetRow Row;
}

class SheetGroup
{
	public string Name;
	public List<SheetResponse> Responses = new List<SheetResponse>();
	public SheetRow Row;
}

class SheetEntry
{
	public string Condition;
	public string Branch;
	public List<string> Actions = new List<string>();
	public SheetRow Row;
}

// Everything one character's rows add up to: the ConversationData to write and
// the loc rows its text becomes.
class SheetCharacter
{
	public string Name;
	public string World;
	public List<SheetBranch> Branches = new List<SheetBranch>();
	public List<SheetGroup> Groups = new List<SheetGroup>();
	public List<SheetEntry> Entries = new List<SheetEntry>();
	public List<KeyValuePair<string, string>> Strings = new List<KeyValuePair<string, string>>();
	public SheetRow FirstRow;
}

static class ConversationSheet
{
	// Reserved value in the `conversation key` column: the row declares a
	// ConversationEntry (a conditional way into the conversation) rather than a
	// branch of its own.
	public const string EntryKey = "entry";

	static readonly string[] RequiredColumns =
	{
		"character", "player", "conversation key", "goto", "language", "condition", "action", "text",
	};

	// Reads one sheet export into rows, applying the fill-down rule on the
	// character column (a name is typed once, every row under it belongs to that
	// character until the next name).
	public static List<SheetRow> ReadRows(string path, Report report)
	{
		var rows = new List<SheetRow>();
		List<string> lines = ReadAllLinesShared(path);
		if (lines.Count == 0)
		{
			report.Error(path, 1, "sheet is empty - the first row must be the header");
			return rows;
		}

		// A spreadsheet's save dialog switches the delimiter with one click, and then
		// every column reads as missing - name the real cause instead.
		if (lines[0].IndexOf('\t') < 0)
		{
			string format = "not tab-separated";
			if (lines[0].IndexOf(',') >= 0)
			{
				format = "comma-separated";
			}
			else if (lines[0].IndexOf(';') >= 0)
			{
				format = "semicolon-separated";
			}
			report.Error(path, 1, $"the sheet was saved {format} - re-save it tab-separated (LibreOffice: File > Save As > Text CSV, tick 'Edit filter settings', Field delimiter {{Tab}})");
			return rows;
		}

		var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		string[] header = lines[0].Split('\t');
		for (int i = 0; i < header.Length; i++)
		{
			string name = header[i].Trim();
			if (name.Length > 0 && !columns.ContainsKey(name))
			{
				columns[name] = i;
			}
		}
		foreach (string required in RequiredColumns)
		{
			if (!columns.ContainsKey(required))
			{
				report.Error(path, 1, $"header is missing the '{required}' column");
				return rows;
			}
		}

		string character = "";
		for (int i = 1; i < lines.Count; i++)
		{
			string[] cells = lines[i].Split('\t');
			var row = new SheetRow
			{
				File = path,
				Line = i + 1,
				Character = Cell(cells, columns, "character"),
				Player = Cell(cells, columns, "player"),
				Key = Cell(cells, columns, "conversation key"),
				Goto = Cell(cells, columns, "goto"),
				Language = Cell(cells, columns, "language"),
				Condition = Cell(cells, columns, "condition"),
				Action = Cell(cells, columns, "action"),
				Text = Cell(cells, columns, "text"),
			};
			if (row.Character.Length == 0 && row.Player.Length == 0 && row.Key.Length == 0
				&& row.Goto.Length == 0 && row.Language.Length == 0 && row.Condition.Length == 0
				&& row.Action.Length == 0 && row.Text.Length == 0)
			{
				continue;
			}
			if (row.Character.Length > 0)
			{
				// The sheet writes the prefix ("intro_mayor_"); the trailing
				// separator is the author's punctuation, not part of the name.
				character = row.Character.TrimEnd('_');
			}
			row.Character = character;
			if (character.Length == 0)
			{
				report.Error(row, "no character named yet - the first row of a sheet must name one");
				continue;
			}
			rows.Add(row);
		}
		return rows;
	}

	// Groups rows into characters and resolves the graph: branches, response
	// groups, entries, and the loc keys the text lands under.
	public static List<SheetCharacter> Build(List<SheetRow> rows, string world, Report report)
	{
		var characters = new List<SheetCharacter>();
		var byName = new Dictionary<string, SheetCharacter>(StringComparer.Ordinal);
		SheetBranch openBranch = null;
		string openCharacter = null;

		foreach (SheetRow row in rows)
		{
			if (!byName.TryGetValue(row.Character, out SheetCharacter character))
			{
				character = new SheetCharacter { Name = row.Character, World = world, FirstRow = row };
				byName[row.Character] = character;
				characters.Add(character);
			}
			if (openCharacter != row.Character)
			{
				openBranch = null;
				openCharacter = row.Character;
			}

			if (row.IsPlayer)
			{
				openBranch = null;
				AddResponse(character, row, report);
				continue;
			}
			if (row.Key.Equals(EntryKey, StringComparison.OrdinalIgnoreCase))
			{
				openBranch = null;
				AddEntry(character, row, report);
				continue;
			}
			if (row.Key.Length == 0)
			{
				// Continuation: another paragraph of the branch above.
				if (openBranch == null)
				{
					report.Error(row, "blank conversation key with no branch above it to continue");
					continue;
				}
				if (row.Condition.Length > 0)
				{
					report.Error(row, "a branch has no condition slot - put the condition on an 'entry' row that goes to this branch, or on the player response that leads here");
					continue;
				}
				ApplyBranchFields(openBranch, row, report);
				AddLine(character, openBranch, row, report);
				continue;
			}
			openBranch = AddBranch(character, row, report);
		}

		foreach (SheetCharacter character in characters)
		{
			Resolve(character, report);
		}
		return characters;
	}

	static SheetBranch AddBranch(SheetCharacter character, SheetRow row, Report report)
	{
		foreach (SheetBranch existing in character.Branches)
		{
			if (existing.Name == row.Key)
			{
				report.Error(row, $"duplicate branch '{row.Key}' - it is already declared at {existing.Row.Where}");
				return existing;
			}
		}
		if (row.Condition.Length > 0)
		{
			report.Error(row, "a branch has no condition slot - put the condition on an 'entry' row that goes to this branch, or on the player response that leads here");
		}
		var branch = new SheetBranch { Name = row.Key, Row = row };
		character.Branches.Add(branch);
		ApplyBranchFields(branch, row, report);
		AddLine(character, branch, row, report);
		return branch;
	}

	// goto, language and action describe the whole branch, not the paragraph
	// they sit beside, and may be written on ANY of its rows: both the exit and
	// the end actions take effect after the LAST line, so a multi-paragraph
	// branch reads best with them at the bottom, next to the line they follow.
	// Actions accumulate in row order; the other two are one per branch, and a
	// second claim is an error rather than a silent overwrite.
	static void ApplyBranchFields(SheetBranch branch, SheetRow row, Report report)
	{
		if (row.Goto.Length > 0)
		{
			if (branch.GotoRow != null)
			{
				report.Error(row, $"branch '{branch.Name}' already goes to '{branch.ExitGroup}' at {branch.GotoRow.Where} - a branch has one exit");
			}
			else
			{
				branch.ExitGroup = row.Goto;
				branch.GotoRow = row;
			}
		}
		if (row.Language.Length > 0)
		{
			if (branch.LanguageRow != null)
			{
				report.Error(row, $"branch '{branch.Name}' already speaks '{branch.Language}' at {branch.LanguageRow.Where} - a branch has one default language (switch tongue mid-line with a [lang:...] span instead)");
			}
			else
			{
				branch.Language = row.Language;
				branch.LanguageRow = row;
			}
		}
		branch.Actions.AddRange(SplitNames(row.Action));
	}

	static void AddLine(SheetCharacter character, SheetBranch branch, SheetRow row, Report report)
	{
		if (row.Text.Length == 0)
		{
			return;
		}
		string key = $"{character.Name}_{branch.Name}_{(branch.LineKeys.Count + 1):00}";
		branch.LineKeys.Add(key);
		character.Strings.Add(new KeyValuePair<string, string>(key, row.Text));
	}

	static void AddResponse(SheetCharacter character, SheetRow row, Report report)
	{
		if (row.Key.Length == 0)
		{
			report.Error(row, "a player row needs a conversation key - it names the response group this choice belongs to");
			return;
		}
		SheetGroup group = null;
		foreach (SheetGroup existing in character.Groups)
		{
			if (existing.Name == row.Key)
			{
				group = existing;
				break;
			}
		}
		if (group == null)
		{
			group = new SheetGroup { Name = row.Key, Row = row };
			character.Groups.Add(group);
		}

		var response = new SheetResponse
		{
			Language = NullIfEmpty(row.Language),
			Destination = row.Goto,
			Actions = SplitNames(row.Action),
			Row = row,
		};
		List<string> conditions = SplitNames(row.Condition);
		if (conditions.Count > 1)
		{
			report.Error(row, "a response has one condition slot - there is no way to and two together yet");
		}
		if (conditions.Count > 0)
		{
			response.Condition = conditions[0];
		}
		if (row.Text.Length > 0)
		{
			// Keyed by destination rather than group position, so inserting a
			// choice above this one does not renumber its text.
			string stem = row.Goto.Length > 0 ? row.Goto : "end";
			string key = $"{character.Name}_{group.Name}_{stem}";
			int suffix = 2;
			while (HasKey(character, key))
			{
				key = $"{character.Name}_{group.Name}_{stem}_{suffix}";
				suffix++;
			}
			response.LocKey = key;
			character.Strings.Add(new KeyValuePair<string, string>(key, row.Text));
		}
		group.Responses.Add(response);
	}

	static void AddEntry(SheetCharacter character, SheetRow row, Report report)
	{
		if (row.Text.Length > 0)
		{
			report.Error(row, "an 'entry' row has no text of its own - it points at the branch that speaks");
		}
		if (row.Goto.Length == 0)
		{
			report.Error(row, "an 'entry' row needs a goto naming the branch it opens on");
			return;
		}
		var entry = new SheetEntry
		{
			Branch = row.Goto,
			Actions = SplitNames(row.Action),
			Row = row,
		};
		List<string> conditions = SplitNames(row.Condition);
		if (conditions.Count > 1)
		{
			report.Error(row, "an entry has one condition slot - there is no way to and two together yet");
		}
		if (conditions.Count > 0)
		{
			entry.Condition = conditions[0];
		}
		character.Entries.Add(entry);
	}

	// Cross-checks the graph and fills in what the sheet leaves implicit: the
	// fallback entry and which branch is a group's canonical introduction.
	static void Resolve(SheetCharacter character, Report report)
	{
		var branchNames = new HashSet<string>(StringComparer.Ordinal);
		foreach (SheetBranch branch in character.Branches)
		{
			branchNames.Add(branch.Name);
		}
		var groupNames = new HashSet<string>(StringComparer.Ordinal);
		foreach (SheetGroup group in character.Groups)
		{
			groupNames.Add(group.Name);
		}

		foreach (SheetBranch branch in character.Branches)
		{
			if (branch.ExitGroup.Length > 0 && !groupNames.Contains(branch.ExitGroup))
			{
				report.Error(branch.Row, $"branch '{branch.Name}' goes to '{branch.ExitGroup}', which no player row declares as a response group");
			}
		}
		foreach (SheetGroup group in character.Groups)
		{
			foreach (SheetResponse response in group.Responses)
			{
				if (response.Destination.Length > 0 && !branchNames.Contains(response.Destination))
				{
					report.Error(response.Row, $"response goes to '{response.Destination}', which is not a branch of {character.Name}");
				}
			}
		}
		foreach (SheetEntry entry in character.Entries)
		{
			if (!branchNames.Contains(entry.Branch))
			{
				report.Error(entry.Row, $"entry goes to '{entry.Branch}', which is not a branch of {character.Name}");
			}
		}

		if (character.Entries.Count == 0)
		{
			// No explicit entry rows: the conversation opens on the character's
			// first branch, unconditionally.
			if (character.Branches.Count == 0)
			{
				report.Error(character.FirstRow, $"{character.Name} has no branches");
				return;
			}
			character.Entries.Add(new SheetEntry { Branch = character.Branches[0].Name, Row = character.Branches[0].Row });
		}

		// The runtime scores a group's response visibility against ONE canonical
		// branch; the first branch that reaches a group is that branch.
		var claimed = new HashSet<string>(StringComparer.Ordinal);
		foreach (SheetBranch branch in character.Branches)
		{
			if (branch.ExitGroup.Length > 0 && claimed.Add(branch.ExitGroup))
			{
				branch.IsPrimaryGroupEntry = true;
			}
		}

		// Reachability, as warnings: a typo'd goto is an error above, but an
		// orphan is usually a half-finished edit rather than a mistake.
		var reachedGroups = new HashSet<string>(StringComparer.Ordinal);
		foreach (SheetBranch branch in character.Branches)
		{
			if (branch.ExitGroup.Length > 0)
			{
				reachedGroups.Add(branch.ExitGroup);
			}
		}
		foreach (SheetGroup group in character.Groups)
		{
			if (!reachedGroups.Contains(group.Name))
			{
				report.Warn(group.Row, $"response group '{group.Name}' is never reached - no branch of {character.Name} goes to it");
			}
		}
		var reachedBranches = new HashSet<string>(StringComparer.Ordinal);
		foreach (SheetEntry entry in character.Entries)
		{
			reachedBranches.Add(entry.Branch);
		}
		foreach (SheetGroup group in character.Groups)
		{
			foreach (SheetResponse response in group.Responses)
			{
				if (response.Destination.Length > 0)
				{
					reachedBranches.Add(response.Destination);
				}
			}
		}
		foreach (SheetBranch branch in character.Branches)
		{
			if (!reachedBranches.Contains(branch.Name))
			{
				report.Warn(branch.Row, $"branch '{branch.Name}' is never reached - no entry or response goes to it");
			}
		}
	}

	static bool HasKey(SheetCharacter character, string key)
	{
		foreach (KeyValuePair<string, string> pair in character.Strings)
		{
			if (pair.Key == key)
			{
				return true;
			}
		}
		return false;
	}

	// Condition / action cells name authored .tres files; several are separated
	// by ';'.
	static List<string> SplitNames(string cell)
	{
		var names = new List<string>();
		if (cell.Length == 0)
		{
			return names;
		}
		foreach (string part in cell.Split(';'))
		{
			string name = part.Trim();
			if (name.Length > 0)
			{
				names.Add(name);
			}
		}
		return names;
	}

	static string NullIfEmpty(string value)
	{
		return value.Length > 0 ? value : null;
	}

	static string Cell(string[] cells, Dictionary<string, int> columns, string name)
	{
		int index = columns[name];
		return index < cells.Length ? cells[index].Trim() : "";
	}

	// FileShare.ReadWrite: a sheet export is routinely still open in the
	// spreadsheet, and that holder's write handle fails the default request.
	static List<string> ReadAllLinesShared(string path)
	{
		var lines = new List<string>();
		using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
		using (var reader = new StreamReader(stream))
		{
			string line;
			while ((line = reader.ReadLine()) != null)
			{
				lines.Add(line);
			}
		}
		return lines;
	}
}
