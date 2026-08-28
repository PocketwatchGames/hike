using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;

// Every authored `.tres` under `resources/`, grouped by the C# class it carries,
// so the entity inspector can offer "which ConversationData?" as a dropdown
// without anyone registering conversations in a second resource.
//
// Discovered on disk for the same reason `.hikescene` stamps are: a conversation
// is authored as a file, and a registration step in a palette is one that gets
// forgotten every time. Nothing else in the project needs this — it exists
// because the inspector REFLECTS an entry rather than naming its fields, so it
// has to answer the question for a type it is seeing for the first time.
//
// Two things it is careful about:
//   - **Nothing is LOADED to identify it.** A `.tres` names its script as an
//     `ext_resource` path, so the class is the script file's own basename —
//     read off the text. Loading a resource to find out what it is pulls in its
//     whole dependency graph, which for one `WorldGenData` is most of the game.
//   - **The header's `script_class` is NOT enough.** Plenty of files in this
//     repo were written without one (`spawn_entries/chest.tres` has a bare
//     `[gd_resource type="Resource" format=3]`), so keying on it would silently
//     offer a partial list — the worst failure a picker has, because it looks
//     like the resource does not exist.
//
// Built once per session. The scan is ~700 small text reads and only the first
// dropdown pays for it.
public static class ResourceTypeIndex
{
    // Where authored data lives. Scenes are deliberately not scanned: a
    // PackedScene field is left read-only in the inspector.
    private const string ROOT = "res://resources/";

    // How far into a .tres to look for the [resource] section's script line.
    // The ext_resource block sits above it and a big authored resource can carry
    // a lot of sub_resources between the two, so this is generous — but bounded,
    // since the alternative is reading megabytes of embedded data to learn one
    // class name.
    private const int MAX_LINES = 4000;

    private static Dictionary<Type, List<string>> _byType;

    // Every .tres whose resource is `type` or a subclass of it, sorted by name.
    public static string[] Candidates(Type type)
    {
        if (type == null)
        {
            return Array.Empty<string>();
        }
        Build();
        var found = new List<string>();
        foreach ((Type carried, List<string> paths) in _byType)
        {
            if (type.IsAssignableFrom(carried))
            {
                found.AddRange(paths);
            }
        }
        found.Sort();
        return found.ToArray();
    }

    private static void Build()
    {
        if (_byType != null)
        {
            return;
        }
        _byType = new Dictionary<Type, List<string>>();
        // The game's own assembly: this project uses no namespaces, so a script
        // file's basename IS its type name.
        Dictionary<string, Type> types = new();
        foreach (Type t in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (typeof(Resource).IsAssignableFrom(t))
            {
                types[t.Name] = t;
            }
        }
        Scan(ROOT, types);
    }

    private static void Scan(string dir, Dictionary<string, Type> types)
    {
        using DirAccess access = DirAccess.Open(dir);
        if (access == null)
        {
            return;
        }
        foreach (string sub in access.GetDirectories())
        {
            Scan(dir + sub + "/", types);
        }
        foreach (string file in access.GetFiles())
        {
            // An exported build serves "x.tres.remap"; the loader still wants
            // the original name.
            string name = file.EndsWith(".remap") ? file.Substr(0, file.Length - 6) : file;
            if (!name.EndsWith(".tres"))
            {
                continue;
            }
            string path = dir + name;
            Type carried = ClassOf(path, types);
            if (carried == null)
            {
                continue;
            }
            if (!_byType.TryGetValue(carried, out List<string> paths))
            {
                paths = new List<string>();
                _byType[carried] = paths;
            }
            paths.Add(path);
        }
    }

    // The class a .tres carries, read off the text: the [resource] section names
    // a script by ext_resource id, and that ext_resource names a .cs path whose
    // basename is the class.
    private static Type ClassOf(string path, Dictionary<string, Type> types)
    {
        using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file == null)
        {
            return null;
        }
        // ext_resource id -> script class name, for the script rows only.
        var scripts = new Dictionary<string, string>();
        bool inResource = false;
        for (int i = 0; i < MAX_LINES && !file.EofReached(); i++)
        {
            string line = file.GetLine();
            if (line.StartsWith("[resource]"))
            {
                inResource = true;
                continue;
            }
            if (line.StartsWith("[ext_resource") && line.Contains("type=\"Script\""))
            {
                string id = Between(line, " id=\"", "\"");
                string src = Between(line, " path=\"", "\"");
                if (id != null && src != null)
                {
                    scripts[id] = src.GetFile().GetBaseName();
                }
                continue;
            }
            // Only the [resource] section's own script line: a sub_resource
            // names a script too, and taking the first one seen would type the
            // file as whatever it happens to embed.
            if (inResource && line.StartsWith("script = ExtResource("))
            {
                string id = Between(line, "ExtResource(\"", "\"");
                if (id != null && scripts.TryGetValue(id, out string className)
                    && types.TryGetValue(className, out Type type))
                {
                    return type;
                }
                return null;
            }
        }
        return null;
    }

    private static string Between(string line, string open, string close)
    {
        int a = line.IndexOf(open, StringComparison.Ordinal);
        if (a < 0)
        {
            return null;
        }
        a += open.Length;
        int b = line.IndexOf(close, a, StringComparison.Ordinal);
        return b < 0 ? null : line[a..b];
    }
}
