using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Errors are fatal and write nothing: a dangling goto or a misspelled condition
// is exactly the typo a spreadsheet invites, and there is no other guard on it.
// Warnings (an unreachable branch, a group nothing points at) are a half-finished
// edit, and must never block a build or a playtest.
class Report
{
	public int Errors;
	public int Warnings;

	public void Error(SheetRow row, string message)
	{
		Console.Error.WriteLine($"ConversationImport: ERROR {row.Where}: {message}.");
		Errors++;
	}

	public void Error(string file, int line, string message)
	{
		Console.Error.WriteLine($"ConversationImport: ERROR {Path.GetFileName(file)}:{line}: {message}.");
		Errors++;
	}

	public void Warn(SheetRow row, string message)
	{
		Console.Error.WriteLine($"ConversationImport: {row.Where}: {message}.");
		Warnings++;
	}
}

// Turns the authored conversation sheets under
// resources/data/worlds/<world>/conversations/*.tsv into the ConversationData
// .tres files the game loads, plus the localized dialogue text they reference.
//
// The sheet is the source; both outputs are generated. Do not hand-edit a
// conversation .tres in the Godot inspector - the next import overwrites it.
class Program
{
	static int Main(string[] args)
	{
		string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
		if (args.Length >= 1)
		{
			repoRoot = args[0];
		}

		string worldsDir = Path.Combine(repoRoot, "resources", "data", "worlds");
		if (!Directory.Exists(worldsDir))
		{
			Console.Error.WriteLine($"Worlds folder not found: {worldsDir}");
			return 1;
		}

		var report = new Report();
		var index = new ResourceIndex(repoRoot);
		var writer = new TresWriter(index, report);
		// Path -> contents, staged so a sheet with an error writes nothing at all.
		var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
		int characterCount = 0;

		var worlds = new List<string>(Directory.GetDirectories(worldsDir));
		worlds.Sort(StringComparer.Ordinal);
		foreach (string worldDir in worlds)
		{
			string world = Path.GetFileName(worldDir);
			string conversationsDir = Path.Combine(worldDir, "conversations");
			if (!Directory.Exists(conversationsDir))
			{
				continue;
			}
			var sheets = new List<string>(Directory.GetFiles(conversationsDir, "*.tsv", SearchOption.TopDirectoryOnly));
			if (sheets.Count == 0)
			{
				continue;
			}
			sheets.Sort(StringComparer.Ordinal);

			var rows = new List<SheetRow>();
			foreach (string sheet in sheets)
			{
				rows.AddRange(ConversationSheet.ReadRows(sheet, report));
			}
			List<SheetCharacter> characters = ConversationSheet.Build(rows, world, report);

			var strings = new StringBuilder();
			strings.AppendLine("key\tvalue");
			foreach (SheetCharacter character in characters)
			{
				string tresPath = Path.Combine(conversationsDir, character.Name + "_conversation.tres");
				string existingUid = File.Exists(tresPath) ? ResourceIndex.HeaderUid(tresPath) : null;
				outputs[tresPath] = writer.Write(character, existingUid);
				characterCount++;
				foreach (KeyValuePair<string, string> line in character.Strings)
				{
					strings.Append(line.Key).Append('\t').AppendLine(line.Value);
				}
			}
			outputs[Path.Combine(repoRoot, "resources", "localization", "english", "dialogue", world + ".tsv")] = strings.ToString();
		}

		if (report.Errors > 0)
		{
			Console.Error.WriteLine($"ConversationImport: {report.Errors} error(s) - nothing written.");
			return 1;
		}

		int written = 0;
		foreach (KeyValuePair<string, string> output in outputs)
		{
			if (TresWriter.WriteIfChanged(output.Key, output.Value))
			{
				written++;
			}
		}
		Console.WriteLine($"ConversationImport: {characterCount} conversation(s), {written} file(s) updated, {report.Warnings} warning(s).");
		return 0;
	}
}
