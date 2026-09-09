using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Serializes a SheetCharacter as the ConversationData .tres the game loads.
//
// The file is generated, but its identity is not: placements.tres and
// NpcSpawnEntry reference a conversation by uid, so an existing file's
// [gd_resource uid=...] is carried across a rewrite. A brand-new conversation
// is written without one and Godot assigns it on the next editor scan.
class TresWriter
{
	readonly ResourceIndex _index;
	readonly Report _report;

	readonly List<ResRef> _externals = new List<ResRef>();
	readonly Dictionary<string, string> _externalIds = new Dictionary<string, string>(StringComparer.Ordinal);

	public TresWriter(ResourceIndex index, Report report)
	{
		_index = index;
		_report = report;
	}

	public string Write(SheetCharacter character, string existingUid)
	{
		_externals.Clear();
		_externalIds.Clear();

		string dataScript = ScriptId("ConversationData");
		string branchScript = ScriptId("ConversationBranch");
		string entryScript = ScriptId("ConversationEntry");
		string responseScript = ScriptId("ConversationResponse");
		string groupScript = ScriptId("ConversationResponseGroup");

		var body = new StringBuilder();
		var entryIds = new List<string>();
		var branchIds = new List<string>();
		var groupIds = new List<string>();

		for (int i = 0; i < character.Entries.Count; i++)
		{
			SheetEntry entry = character.Entries[i];
			string id = $"Entry_{i + 1}";
			entryIds.Add(id);
			body.AppendLine($"[sub_resource type=\"Resource\" id=\"{id}\"]");
			body.AppendLine($"script = ExtResource(\"{entryScript}\")");
			AppendCondition(body, character, entry.Condition, entry.Row);
			body.AppendLine($"branch = &\"{entry.Branch}\"");
			AppendActions(body, "actions", character, entry.Actions, entry.Row);
			body.AppendLine();
		}

		foreach (SheetBranch branch in character.Branches)
		{
			string id = "Branch_" + branch.Name;
			branchIds.Add(id);
			body.AppendLine($"[sub_resource type=\"Resource\" id=\"{id}\"]");
			body.AppendLine($"script = ExtResource(\"{branchScript}\")");
			body.AppendLine($"name = &\"{branch.Name}\"");
			AppendLanguage(body, character, branch.Language, branch.Row);
			if (branch.LineKeys.Count > 0)
			{
				var keys = new List<string>();
				foreach (string key in branch.LineKeys)
				{
					keys.Add($"&\"{key}\"");
				}
				body.AppendLine($"lineLocKeys = Array[StringName]([{string.Join(", ", keys)}])");
			}
			AppendActions(body, "endActions", character, branch.Actions, branch.Row);
			if (branch.ExitGroup.Length > 0)
			{
				body.AppendLine($"exitGroup = &\"{branch.ExitGroup}\"");
			}
			if (branch.IsPrimaryGroupEntry)
			{
				body.AppendLine("isPrimaryGroupEntry = true");
			}
			body.AppendLine();
		}

		foreach (SheetGroup group in character.Groups)
		{
			var responseIds = new List<string>();
			for (int i = 0; i < group.Responses.Count; i++)
			{
				SheetResponse response = group.Responses[i];
				string id = $"Resp_{group.Name}_{i + 1}";
				responseIds.Add(id);
				body.AppendLine($"[sub_resource type=\"Resource\" id=\"{id}\"]");
				body.AppendLine($"script = ExtResource(\"{responseScript}\")");
				if (response.LocKey.Length > 0)
				{
					body.AppendLine($"textLocKey = &\"{response.LocKey}\"");
				}
				AppendLanguage(body, character, response.Language, response.Row);
				AppendCondition(body, character, response.Condition, response.Row);
				if (response.Destination.Length > 0)
				{
					body.AppendLine($"destination = &\"{response.Destination}\"");
				}
				AppendActions(body, "actions", character, response.Actions, response.Row);
				body.AppendLine();
			}

			string groupId = "Group_" + group.Name;
			groupIds.Add(groupId);
			body.AppendLine($"[sub_resource type=\"Resource\" id=\"{groupId}\"]");
			body.AppendLine($"script = ExtResource(\"{groupScript}\")");
			body.AppendLine($"name = &\"{group.Name}\"");
			body.AppendLine($"responses = [{SubList(responseIds)}]");
			body.AppendLine();
		}

		body.AppendLine("[resource]");
		body.AppendLine($"script = ExtResource(\"{dataScript}\")");
		body.AppendLine($"entryBranches = [{SubList(entryIds)}]");
		body.AppendLine($"branches = [{SubList(branchIds)}]");
		body.AppendLine($"responseGroups = [{SubList(groupIds)}]");

		var file = new StringBuilder();
		string uidAttribute = existingUid != null ? $" uid=\"{existingUid}\"" : "";
		file.AppendLine($"[gd_resource type=\"Resource\" script_class=\"ConversationData\" format=3{uidAttribute}]");
		file.AppendLine();
		foreach (ResRef external in _externals)
		{
			string type = external.ResPath.EndsWith(".cs", StringComparison.Ordinal) ? "Script" : "Resource";
			string uid = external.Uid != null ? $" uid=\"{external.Uid}\"" : "";
			file.AppendLine($"[ext_resource type=\"{type}\"{uid} path=\"{external.ResPath}\" id=\"{_externalIds[external.ResPath]}\"]");
		}
		file.AppendLine();
		file.Append(body);
		return file.ToString();
	}

	void AppendLanguage(StringBuilder body, SheetCharacter character, string name, SheetRow row)
	{
		if (name == null)
		{
			return;
		}
		ResRef language = _index.Language(name);
		if (language == null)
		{
			_report.Error(row, $"unknown language '{name}' - no LanguageData under resources/ declares it");
			return;
		}
		body.AppendLine($"language = ExtResource(\"{ExternalId(language, "Lang")}\")");
	}

	void AppendCondition(StringBuilder body, SheetCharacter character, string name, SheetRow row)
	{
		if (name == null)
		{
			return;
		}
		ResRef condition = _index.Condition(character.World, name);
		if (condition == null)
		{
			_report.Error(row, $"unknown condition '{name}' - no .tres by that name in {ConditionFolders(character.World)}");
			return;
		}
		body.AppendLine($"condition = ExtResource(\"{ExternalId(condition, "Cond")}\")");
	}

	void AppendActions(StringBuilder body, string property, SheetCharacter character, List<string> names, SheetRow row)
	{
		if (names.Count == 0)
		{
			return;
		}
		var ids = new List<string>();
		foreach (string name in names)
		{
			ResRef action = _index.Action(character.World, name);
			if (action == null)
			{
				_report.Error(row, $"unknown action '{name}' - no .tres by that name in {ActionFolders(character.World)}");
				continue;
			}
			ids.Add($"ExtResource(\"{ExternalId(action, "Act")}\")");
		}
		if (ids.Count == 0)
		{
			return;
		}
		body.AppendLine($"{property} = Array[Object]([{string.Join(", ", ids)}])");
	}

	static string ConditionFolders(string world)
	{
		return $"resources/data/world_authoring/conversation/conditions/, resources/data/worlds/shared/conversation/conditions/ or resources/data/worlds/{world}/conversations/conditions/";
	}

	static string ActionFolders(string world)
	{
		return $"resources/data/world_authoring/conversation/actions/, resources/data/worlds/shared/conversation/actions/ or resources/data/worlds/{world}/conversations/actions/";
	}

	string ScriptId(string className)
	{
		ResRef script = _index.Script(className);
		if (script == null)
		{
			throw new InvalidOperationException($"No .cs.uid sidecar found for {className} - run tools/validate_uids --fix");
		}
		return ExternalId(script, "Script");
	}

	string ExternalId(ResRef reference, string prefix)
	{
		if (_externalIds.TryGetValue(reference.ResPath, out string id))
		{
			return id;
		}
		id = $"{prefix}_{reference.Name}";
		_externalIds[reference.ResPath] = id;
		_externals.Add(reference);
		return id;
	}

	static string SubList(List<string> ids)
	{
		var parts = new List<string>();
		foreach (string id in ids)
		{
			parts.Add($"SubResource(\"{id}\")");
		}
		return string.Join(", ", parts);
	}

	// Writes only when the bytes actually change, so a build that imports
	// nothing new leaves mtimes (and Godot's importer) alone.
	public static bool WriteIfChanged(string path, string contents)
	{
		if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == contents)
		{
			return false;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		File.WriteAllText(path, contents, new UTF8Encoding(false));
		return true;
	}
}
