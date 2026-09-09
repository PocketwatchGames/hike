using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

// A res:// reference the generated .tres will point at. `Uid` is null for a
// target that has none of its own (common.tres is one) - Godot accepts a
// path-only [ext_resource], it just backfills the attribute the next time it
// saves the file.
class ResRef
{
	public string ResPath;
	public string Uid;
	public string Name;

	public ResRef(string resPath, string uid, string name)
	{
		ResPath = resPath;
		Uid = uid;
		Name = name;
	}
}

// Everything the writer has to name by uid: the conversation scripts, the
// languages, and the authored condition / action resources a sheet cell can
// name. All of it is read off disk rather than listed here, so a new condition
// becomes available to authors by existing.
class ResourceIndex
{
	readonly string _repoRoot;

	// Script class name -> its .cs.uid sidecar value.
	readonly Dictionary<string, ResRef> _scripts = new Dictionary<string, ResRef>(StringComparer.Ordinal);
	// LanguageData.id (and displayName) -> the .tres holding it.
	readonly Dictionary<string, ResRef> _languages = new Dictionary<string, ResRef>(StringComparer.OrdinalIgnoreCase);
	// Per world: file stem -> the .tres. Conditions and actions are separate
	// namespaces so a gate and a side effect may share a name.
	readonly Dictionary<string, Dictionary<string, ResRef>> _conditions = new Dictionary<string, Dictionary<string, ResRef>>(StringComparer.Ordinal);
	readonly Dictionary<string, Dictionary<string, ResRef>> _actions = new Dictionary<string, Dictionary<string, ResRef>>(StringComparer.Ordinal);

	public ResourceIndex(string repoRoot)
	{
		_repoRoot = repoRoot;
		IndexScripts();
		IndexLanguages();
	}

	public ResRef Script(string className)
	{
		return _scripts.TryGetValue(className, out ResRef r) ? r : null;
	}

	public ResRef Language(string name)
	{
		return _languages.TryGetValue(name, out ResRef r) ? r : null;
	}

	public ResRef Condition(string world, string name)
	{
		return Lookup(_conditions, world, "conditions", name);
	}

	public ResRef Action(string world, string name)
	{
		return Lookup(_actions, world, "actions", name);
	}

	// Three folders, widest first, each shadowing the one before it: the
	// reusable verbs in world_authoring/ (open_shop, language_incomplete - a
	// type, no proper noun), the game's own in worlds/shared/ (teach_vyeshal,
	// give_lantern), then this world's, so a world can override one without
	// renaming it.
	ResRef Lookup(Dictionary<string, Dictionary<string, ResRef>> cache, string world, string kind, string name)
	{
		if (!cache.TryGetValue(world, out Dictionary<string, ResRef> table))
		{
			table = new Dictionary<string, ResRef>(StringComparer.OrdinalIgnoreCase);
			IndexFolder(Path.Combine(_repoRoot, "resources", "data", "world_authoring", "conversation", kind), table);
			IndexFolder(Path.Combine(_repoRoot, "resources", "data", "worlds", "shared", "conversation", kind), table);
			IndexFolder(Path.Combine(_repoRoot, "resources", "data", "worlds", world, "conversations", kind), table);
			cache[world] = table;
		}
		if (name == null)
		{
			return null;
		}
		return table.TryGetValue(name, out ResRef r) ? r : null;
	}

	void IndexFolder(string dir, Dictionary<string, ResRef> table)
	{
		if (!Directory.Exists(dir))
		{
			return;
		}
		foreach (string path in Directory.GetFiles(dir, "*.tres", SearchOption.TopDirectoryOnly))
		{
			string name = Path.GetFileNameWithoutExtension(path);
			table[name] = new ResRef(ResPath(path), HeaderUid(path), name);
		}
	}

	void IndexScripts()
	{
		string scriptsDir = Path.Combine(_repoRoot, "scripts");
		foreach (string path in Directory.EnumerateFiles(scriptsDir, "*.cs", SearchOption.AllDirectories))
		{
			string sidecar = path + ".uid";
			if (!File.Exists(sidecar))
			{
				continue;
			}
			string uid = File.ReadAllText(sidecar).Trim();
			string name = Path.GetFileNameWithoutExtension(path);
			// First one wins; the conversation classes are uniquely named.
			if (!_scripts.ContainsKey(name))
			{
				_scripts[name] = new ResRef(ResPath(path), uid, name);
			}
		}
	}

	void IndexLanguages()
	{
		string resourcesDir = Path.Combine(_repoRoot, "resources");
		foreach (string path in Directory.EnumerateFiles(resourcesDir, "*.tres", SearchOption.AllDirectories))
		{
			string text;
			try
			{
				text = File.ReadAllText(path);
			}
			catch (IOException)
			{
				continue;
			}
			if (!text.Contains("script_class=\"LanguageData\""))
			{
				continue;
			}
			var reference = new ResRef(ResPath(path), HeaderUid(path), Path.GetFileNameWithoutExtension(path));
			Match id = Regex.Match(text, "^id = &\"([^\"]*)\"", RegexOptions.Multiline);
			if (id.Success && id.Groups[1].Value.Length > 0)
			{
				_languages[id.Groups[1].Value] = reference;
			}
			Match display = Regex.Match(text, "^displayName = &?\"([^\"]*)\"", RegexOptions.Multiline);
			if (display.Success && display.Groups[1].Value.Length > 0 && !_languages.ContainsKey(display.Groups[1].Value))
			{
				_languages[display.Groups[1].Value] = reference;
			}
		}
	}

	// The uid a .tres declares in its own header, or null if it declares none.
	public static string HeaderUid(string path)
	{
		string first;
		using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
		using (var reader = new StreamReader(stream))
		{
			first = reader.ReadLine();
		}
		if (first == null)
		{
			return null;
		}
		Match match = Regex.Match(first, "uid=\"(uid://[^\"]+)\"");
		return match.Success ? match.Groups[1].Value : null;
	}

	string ResPath(string fullPath)
	{
		string relative = Path.GetRelativePath(_repoRoot, fullPath).Replace('\\', '/');
		return "res://" + relative;
	}
}
