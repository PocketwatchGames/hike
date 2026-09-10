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
		var declarations = new VarDeclarations();
		var writer = new TresWriter(index, report, declarations);
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

		// The npcvars every sheet named, as the one generated registry. Written
		// unconditionally so SimData's reference to it never dangles.
		string registryPath = Path.Combine(repoRoot, "resources", "data", "worlds", "shared", "script_variables", "npc_variables.tres");
		outputs[registryPath] = WriteVariableRegistry(index, declarations, File.Exists(registryPath) ? ResourceIndex.HeaderUid(registryPath) : null);

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
		Console.WriteLine($"ConversationImport: {characterCount} conversation(s), {declarations.Count} npcvar(s), {written} file(s) updated, {report.Warnings} warning(s).");
		return 0;
	}

	// The generated half of SimData.scriptVariables: one ScriptVariableData per
	// npcvar a sheet named, embedded rather than one file each - these have no
	// authoring surface of their own, the sheet cell is the declaration.
	static string WriteVariableRegistry(ResourceIndex index, VarDeclarations declarations, string existingUid)
	{
		ResRef registryScript = index.Script("ScriptVariableRegistry");
		ResRef dataScript = index.Script("ScriptVariableData");
		if (registryScript == null || dataScript == null)
		{
			throw new InvalidOperationException("No .cs.uid sidecar for ScriptVariableRegistry / ScriptVariableData - run tools/validate_uids --fix");
		}

		var file = new StringBuilder();
		string uidAttribute = existingUid != null ? $" uid=\"{existingUid}\"" : "";
		file.AppendLine($"[gd_resource type=\"Resource\" script_class=\"ScriptVariableRegistry\" format=3{uidAttribute}]");
		file.AppendLine();
		file.AppendLine($"[ext_resource type=\"Script\" uid=\"{registryScript.Uid}\" path=\"{registryScript.ResPath}\" id=\"1_registry\"]");
		file.AppendLine($"[ext_resource type=\"Script\" uid=\"{dataScript.Uid}\" path=\"{dataScript.ResPath}\" id=\"2_variable\"]");
		file.AppendLine();

		var ids = new List<string>();
		int slot = 0;
		foreach (string id in declarations.Ids)
		{
			slot++;
			string subId = $"Var_{slot}";
			ids.Add($"SubResource(\"{subId}\")");
			file.AppendLine($"[sub_resource type=\"Resource\" id=\"{subId}\"]");
			file.AppendLine($"script = ExtResource(\"2_variable\")");
			file.AppendLine($"id = &\"{id}\"");
			file.AppendLine($"type = {(declarations.IsBool(id) ? 0 : 1)}");
			file.AppendLine($"description = \"Declared by the {declarations.Source(id)} conversation sheet.\"");
			file.AppendLine();
		}

		file.AppendLine("[resource]");
		file.AppendLine("script = ExtResource(\"1_registry\")");
		file.AppendLine($"variables = Array[ExtResource(\"2_variable\")]([{string.Join(", ", ids)}])");
		return file.ToString();
	}
}
