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
	// The world this file belongs to (it sits under worlds/<name>/), or null for
	// one every world may name - see ResourceIndex.Pick.
	public string World;

	const string WorldsRoot = "res://resources/data/worlds/";

	public ResRef(string resPath, string uid, string name)
	{
		ResPath = resPath;
		Uid = uid;
		Name = name;
		if (resPath.StartsWith(WorldsRoot, StringComparison.Ordinal))
		{
			int end = resPath.IndexOf('/', WorldsRoot.Length);
			string world = end < 0 ? null : resPath.Substring(WorldsRoot.Length, end - WorldsRoot.Length);
			World = world == "shared" ? null : world;
		}
	}
}

// Everything the writer has to name by uid: the conversation scripts, the
// languages, and the authored condition / action resources a sheet cell can
// name. All of it is read off disk rather than listed here, so a new condition
// becomes available to authors by existing.
//
// Every by-name table is WORLD-SCOPED, the same rule the game's pickers and the
// console's `give` apply (scripts/authoring/WorldScope.cs): a file under
// worlds/<name>/ may be named only by <name>'s sheet, and shadows an unscoped
// file of the same name there. So a table keeps every file carrying a name, and
// the lookup picks.
class ResourceIndex
{
	readonly string _repoRoot;

	// Script class name -> its .cs.uid sidecar value.
	readonly Dictionary<string, ResRef> _scripts = new Dictionary<string, ResRef>(StringComparer.Ordinal);
	// LanguageData.id (and displayName) -> the .tres holding it.
	readonly Dictionary<string, List<ResRef>> _languages = new Dictionary<string, List<ResRef>>(StringComparer.OrdinalIgnoreCase);
	// Basename -> the .tres, per script_class, for the inline `teach:` action
	// cell. Keyed by basename rather than by any id inside the file because that
	// is what an author reads off the folder - a language is the exception, and
	// keeps its id because ids are what the [lang:] markup and the sheet's
	// language column already use.
	readonly Dictionary<string, Dictionary<string, List<ResRef>>> _byScriptClass = new Dictionary<string, Dictionary<string, List<ResRef>>>(StringComparer.Ordinal);
	// Every ScriptVariableData.id authored under worlds/shared/script_variables/,
	// so a `var:` cell naming one that does not exist is an error at import
	// rather than a gate that is silently always false at runtime. The npcvars
	// the sheets declare themselves are NOT in here - they are generated.
	readonly HashSet<string> _authoredVariables = new HashSet<string>(StringComparer.Ordinal);
	// Item basename -> the .tres, for the inline `give:` / `teach:item` cells.
	// Scanned from the same roots the console's `give` verb scans (items/ and
	// every worlds/<name>/items/), so the two name items identically. Exact
	// match only - no substring convenience here, a build-time reference should
	// say what it means.
	readonly Dictionary<string, List<ResRef>> _items = new Dictionary<string, List<ResRef>>(StringComparer.OrdinalIgnoreCase);
	// Per world: file stem -> the .tres. Conditions and actions are separate
	// namespaces so a gate and a side effect may share a name.
	readonly Dictionary<string, Dictionary<string, ResRef>> _conditions = new Dictionary<string, Dictionary<string, ResRef>>(StringComparer.Ordinal);
	readonly Dictionary<string, Dictionary<string, ResRef>> _actions = new Dictionary<string, Dictionary<string, ResRef>>(StringComparer.Ordinal);

	public ResourceIndex(string repoRoot)
	{
		_repoRoot = repoRoot;
		IndexScripts();
		IndexResources();
		IndexAuthoredVariables();
		IndexItems();
	}

	public ResRef Item(string world, string basename)
	{
		return Pick(_items, world, basename);
	}

	public bool IsAuthoredVariable(string id)
	{
		return _authoredVariables.Contains(id);
	}

	public ResRef Script(string className)
	{
		return _scripts.TryGetValue(className, out ResRef r) ? r : null;
	}

	public ResRef Language(string world, string name)
	{
		return Pick(_languages, world, name);
	}

	public ResRef Recipe(string world, string basename)
	{
		return ByScriptClass("RecipeData", world, basename);
	}

	public ResRef Spell(string world, string basename)
	{
		return ByScriptClass("SpellData", world, basename);
	}

	public ResRef Region(string world, string basename)
	{
		return ByScriptClass("RegionData", world, basename);
	}

	public ResRef Species(string world, string basename)
	{
		return ByScriptClass("SpeciesData", world, basename);
	}

	ResRef ByScriptClass(string scriptClass, string world, string basename)
	{
		return _byScriptClass.TryGetValue(scriptClass, out Dictionary<string, List<ResRef>> table)
			? Pick(table, world, basename)
			: null;
	}

	// The file `world`'s sheet means by `name`: that world's own if it has one,
	// else an unscoped one. Another world's file is never offered, and among
	// equals the first indexed wins.
	static ResRef Pick(Dictionary<string, List<ResRef>> table, string world, string name)
	{
		if (name == null || !table.TryGetValue(name, out List<ResRef> carriers))
		{
			return null;
		}
		ResRef unscoped = null;
		foreach (ResRef r in carriers)
		{
			if (r.World != null && r.World == world)
			{
				return r;
			}
			if (r.World == null && unscoped == null)
			{
				unscoped = r;
			}
		}
		return unscoped;
	}

	static void Add(Dictionary<string, List<ResRef>> table, string name, ResRef reference)
	{
		if (!table.TryGetValue(name, out List<ResRef> carriers))
		{
			carriers = new List<ResRef>();
			table[name] = carriers;
		}
		carriers.Add(reference);
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
	// type, no proper noun), the game's own in worlds/shared/ (a verb that names
	// a proper noun of the fiction), then this world's, so a world can override
	// one without renaming it.
	ResRef Lookup(Dictionary<string, Dictionary<string, ResRef>> cache, string world, string kind, string name)
	{
		if (!cache.TryGetValue(world, out Dictionary<string, ResRef> table))
		{
			table = new Dictionary<string, ResRef>(StringComparer.OrdinalIgnoreCase);
			IndexFolder(Path.Combine(_repoRoot, "resources", "data", "dialogue_scripts", kind), table);
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

	// One pass over every .tres, dispatched on the script_class its header
	// declares: the languages a sheet's language column and `teach:language`
	// name by id, and the basename tables the rest of `teach:` resolves against.
	// The header is the first line, so only a file that matches is read whole.
	static readonly string[] TeachableClasses = { "RecipeData", "SpellData", "RegionData", "SpeciesData" };

	void IndexResources()
	{
		string resourcesDir = Path.Combine(_repoRoot, "resources");
		foreach (string path in Directory.EnumerateFiles(resourcesDir, "*.tres", SearchOption.AllDirectories))
		{
			string header;
			try
			{
				header = FirstLine(path);
			}
			catch (IOException)
			{
				continue;
			}
			if (header == null)
			{
				continue;
			}
			if (header.Contains("script_class=\"LanguageData\""))
			{
				IndexLanguage(path);
				continue;
			}
			foreach (string scriptClass in TeachableClasses)
			{
				if (!header.Contains($"script_class=\"{scriptClass}\""))
				{
					continue;
				}
				if (!_byScriptClass.TryGetValue(scriptClass, out Dictionary<string, List<ResRef>> table))
				{
					table = new Dictionary<string, List<ResRef>>(StringComparer.OrdinalIgnoreCase);
					_byScriptClass[scriptClass] = table;
				}
				string name = Path.GetFileNameWithoutExtension(path);
				Add(table, name, new ResRef(ResPath(path), HeaderUid(path), name));
				break;
			}
		}
	}

	void IndexLanguage(string path)
	{
		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch (IOException)
		{
			return;
		}
		var reference = new ResRef(ResPath(path), HeaderUid(path), Path.GetFileNameWithoutExtension(path));
		Match id = Regex.Match(text, "^id = &\"([^\"]*)\"", RegexOptions.Multiline);
		if (id.Success && id.Groups[1].Value.Length > 0)
		{
			Add(_languages, id.Groups[1].Value, reference);
		}
		// The display name is a fallback spelling, so it never shadows an id.
		Match display = Regex.Match(text, "^displayName = &?\"([^\"]*)\"", RegexOptions.Multiline);
		if (display.Success && display.Groups[1].Value.Length > 0 && !_languages.ContainsKey(display.Groups[1].Value))
		{
			Add(_languages, display.Groups[1].Value, reference);
		}
	}

	// items/, then every world's own items/ folder (worlds/shared/items/ among
	// them) - which world may name each is Pick's business.
	void IndexItems()
	{
		var roots = new List<string> { Path.Combine(_repoRoot, "resources", "data", "items") };
		string worldsDir = Path.Combine(_repoRoot, "resources", "data", "worlds");
		if (Directory.Exists(worldsDir))
		{
			var worlds = new List<string>(Directory.GetDirectories(worldsDir));
			worlds.Sort(StringComparer.Ordinal);
			foreach (string world in worlds)
			{
				roots.Add(Path.Combine(world, "items"));
			}
		}
		foreach (string dir in roots)
		{
			if (!Directory.Exists(dir))
			{
				continue;
			}
			foreach (string path in Directory.EnumerateFiles(dir, "*.tres", SearchOption.AllDirectories))
			{
				string name = Path.GetFileNameWithoutExtension(path);
				Add(_items, name, new ResRef(ResPath(path), HeaderUid(path), name));
			}
		}
	}

	void IndexAuthoredVariables()
	{
		string dir = Path.Combine(_repoRoot, "resources", "data", "worlds", "shared", "script_variables");
		if (!Directory.Exists(dir))
		{
			return;
		}
		foreach (string path in Directory.EnumerateFiles(dir, "*.tres", SearchOption.AllDirectories))
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
			if (text.Contains("script_class=\"ScriptVariableRegistry\""))
			{
				// Including the generated npcvar registry, whose embedded
				// declarations are the importer's own output, not authoring.
				continue;
			}
			foreach (Match id in Regex.Matches(text, "^id = &\"([^\"]*)\"", RegexOptions.Multiline))
			{
				if (id.Groups[1].Value.Length > 0)
				{
					_authoredVariables.Add(id.Groups[1].Value);
				}
			}
		}
	}

	// First line of a file, opened shared so a running Godot editor holding it
	// does not fail the read.
	static string FirstLine(string path)
	{
		using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
		using (var reader = new StreamReader(stream))
		{
			return reader.ReadLine();
		}
	}

	// The uid a .tres declares in its own header, or null if it declares none.
	public static string HeaderUid(string path)
	{
		string first = FirstLine(path);
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
