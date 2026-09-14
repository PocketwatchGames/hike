using Godot;
using System.Collections.Generic;

// Lazily-built name -> res:// path index over the authored content roots, so the
// `spawn` / `give` console verbs can name content the way an author does
// ("goblin_forest", "berry") instead of by path.
//
// This is a dev harness, like HeadlessBot — not gameplay code. The "never
// hardcode resource paths" rule exists so gameplay and worldgen can't reach past
// their authored wiring; these verbs have no authored wiring to reach past,
// because naming any file in the project IS the feature. Only directory roots
// are named here, and only filenames are scanned: nothing is loaded until a
// command actually asks for it, so the index costs a directory walk and no
// resource loads.
public static class DebugContentIndex
{
    // Species live in two roots: the creature library, and the world-authoring
    // NPC cast (whose subspecies moved out of characters/ with their conversations).
    private static readonly string[] SpeciesRoots =
    {
        "res://resources/data/characters",
        "res://resources/data/worlds/shared/npcs",
    };
    // Items live in items/ and in each world's own worlds/<name>/items/.
    private const string ItemRoot = "res://resources/data/items";
    private const string WorldsRoot = "res://resources/data/worlds";
    private const string WorldItemsDir = "items";

    private static Dictionary<string, string> _species;
    private static List<(string Name, string Path)> _itemFiles;
    private static readonly Dictionary<string, Dictionary<string, string>> _itemsByWorld = new();

    public static Dictionary<string, string> Species => _species ??= Scan(SpeciesRoots);

    // The items a game in `world` may name (WorldScope): that world's own, then
    // every unscoped one. A world's own item shadows an unscoped one of the same
    // name, and another world's are not listed at all.
    public static Dictionary<string, string> ItemsIn(string world)
    {
        string key = world ?? "";
        if (_itemsByWorld.TryGetValue(key, out Dictionary<string, string> cached))
        {
            return cached;
        }
        _itemFiles ??= ScanItemFiles();
        var map = new Dictionary<string, string>();
        foreach ((string name, string path) in _itemFiles)
        {
            if (world != null && WorldScope.Of(path) == world)
            {
                map[name] = path;
            }
        }
        foreach ((string name, string path) in _itemFiles)
        {
            if (WorldScope.Of(path) == null)
            {
                map.TryAdd(name, path);
            }
        }
        _itemsByWorld[key] = map;
        return map;
    }

    private static List<(string Name, string Path)> ScanItemFiles()
    {
        var files = new List<(string, string)>();
        ScanInto(ItemRoot, files);
        using DirAccess worlds = DirAccess.Open(WorldsRoot);
        if (worlds != null)
        {
            foreach (string world in worlds.GetDirectories())
            {
                string dir = $"{WorldsRoot}/{world}/{WorldItemsDir}";
                if (DirAccess.DirExistsAbsolute(dir))
                {
                    ScanInto(dir, files);
                }
            }
        }
        return files;
    }

    // Resolve a name against one index and load it as T. Accepts an exact
    // basename or a unique substring; returns null with a filled-in `error` for
    // an unknown name, an ambiguous one (naming the candidates), or a file that
    // turned out to be the wrong type.
    public static T Resolve<T>(Dictionary<string, string> index, string name, out string error) where T : Resource
    {
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "no name given";
            return null;
        }

        string key = name.Trim().ToLowerInvariant();
        if (!index.TryGetValue(key, out string path))
        {
            var hits = new List<string>();
            foreach (KeyValuePair<string, string> kv in index)
            {
                if (kv.Key.Contains(key))
                {
                    hits.Add(kv.Key);
                }
            }
            if (hits.Count == 0)
            {
                error = $"unknown '{name}'";
                return null;
            }
            if (hits.Count > 1)
            {
                hits.Sort();
                error = $"'{name}' is ambiguous: {string.Join(", ", hits)}";
                return null;
            }
            path = index[hits[0]];
        }

        Resource loaded = GD.Load<Resource>(path);
        if (loaded is T typed)
        {
            return typed;
        }
        error = loaded == null
            ? $"'{name}' ({path}) failed to load"
            : $"'{name}' ({path}) is a {loaded.GetType().Name}, not a {typeof(T).Name}";
        return null;
    }

    // Every indexed name, sorted — for a bare `spawn` / `give` listing.
    public static List<string> Names(Dictionary<string, string> index)
    {
        var names = new List<string>(index.Keys);
        names.Sort();
        return names;
    }

    private static Dictionary<string, string> Scan(params string[] roots)
    {
        var files = new List<(string Name, string Path)>();
        foreach (string root in roots)
        {
            ScanInto(root, files);
        }
        var map = new Dictionary<string, string>();
        foreach ((string name, string path) in files)
        {
            // First one wins; a duplicate basename across two folders is
            // reachable by its unique-substring path form instead.
            map.TryAdd(name, path);
        }
        return map;
    }

    private static void ScanInto(string dir, List<(string Name, string Path)> files)
    {
        using DirAccess da = DirAccess.Open(dir);
        if (da == null)
        {
            GD.PrintErr($"[debug] content root not found: {dir}");
            return;
        }

        da.ListDirBegin();
        for (string entry = da.GetNext(); entry != ""; entry = da.GetNext())
        {
            if (entry.StartsWith("."))
            {
                continue;
            }
            if (da.CurrentIsDir())
            {
                ScanInto($"{dir}/{entry}", files);
                continue;
            }
            // An exported build serves resources as `<file>.tres.remap`; an
            // editor run sees the .tres itself. Index the logical name either way.
            string file = entry.EndsWith(".remap") ? entry.Substring(0, entry.Length - ".remap".Length) : entry;
            if (!file.EndsWith(".tres"))
            {
                continue;
            }
            string name = file.Substring(0, file.Length - ".tres".Length).ToLowerInvariant();
            files.Add((name, $"{dir}/{file}"));
        }
        da.ListDirEnd();
    }
}
