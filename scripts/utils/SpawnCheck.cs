using Godot;
using System;
using System.Collections.Generic;
using System.Text;

// Dumps every authored SpawnListData as its RESOLVED rows — what each list
// places, at what rate, under what conditions — then quits. Driven by the
// `spawn_check` cvar off Main._Ready; no world, no menu, no renderer.
//
// The point is the DIFF. A spawn entry is authored data with no runtime error
// mode: mistype a density or drop a spawnCondition while re-shaping these files
// and nothing fails, the world just quietly stops placing something. So the
// dump prints every stored property of every entry rather than a chosen few —
// re-shaping the files is proved to have changed nothing when this output is
// byte-identical across it.
public static class SpawnCheck
{
    private const string ResourceRoot = "res://resources";

    public static void RunAndQuit(SceneTree tree)
    {
        var paths = new List<string>();
        Collect(ResourceRoot, paths);
        paths.Sort(StringComparer.Ordinal);

        var sb = new StringBuilder();
        int lists = 0;
        int rows = 0;
        foreach (string path in paths)
        {
            if (GD.Load<Resource>(path) is not SpawnListData list)
            {
                continue;
            }
            lists++;
            sb.AppendLine($"== {path}");
            rows += DumpList(sb, list, "  ");
        }
        GD.Print(sb.ToString().TrimEnd());
        GD.Print($"[spawn_check] {lists} lists, {rows} rows");
        GD.Print("[spawn_check] done");
        tree.Quit();
    }

    private static int DumpList(StringBuilder sb, SpawnListData list, string indent)
    {
        Godot.Collections.Array<SpawnListRow> rows = list?.rows;
        if (rows == null)
        {
            return 0;
        }
        int count = rows.Count;
        int dumped = 0;
        for (int i = 0; i < count; i++)
        {
            dumped += DumpRow(sb, rows[i], indent, i);
        }
        return dumped;
    }

    // One line per row: the entry's type, everything the ENTRY stores, then
    // everything the ROW stores. Both halves on one line because together they
    // are the whole answer to "what does this list place here" — which is the
    // thing the diff has to hold constant.
    private static int DumpRow(StringBuilder sb, SpawnRow row, string indent, int index)
    {
        if (row?.entry == null)
        {
            sb.AppendLine($"{indent}{index,2}  <null>");
            return 1;
        }
        SpawnEntryData entry = row.entry;
        // A group's member array is skipped here because it is recursed below —
        // rendering it inline as well dumped every member twice, and the second
        // rendering is what made an otherwise clean diff unreadable.
        sb.AppendLine($"{indent}{index,2}  {entry.GetType().Name,-24} "
            + $"{Properties(entry, skip: SpawnGroupData.PropertyName.rows)} "
            + $"{Properties(row, skip: SpawnRow.PropertyName.entry)}".Trim());
        int dumped = 1;
        if (entry is SpawnGroupData group && group.rows != null)
        {
            int count = group.rows.Count;
            for (int i = 0; i < count; i++)
            {
                dumped += DumpRow(sb, group.rows[i], indent + "    ", i);
            }
        }
        return dumped;
    }

    // Every property Godot would STORE for this resource, in declaration order.
    // Storage usage is the right filter: it is exactly the set a .tres can
    // carry, so a property that vanishes from this dump is one that would
    // vanish from the file.
    private static string Properties(Resource res, StringName skip = null)
    {
        var sb = new StringBuilder();
        foreach (Godot.Collections.Dictionary prop in res.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)(long)prop["usage"];
            if ((usage & PropertyUsageFlags.Storage) == 0)
            {
                continue;
            }
            var name = (string)prop["name"];
            if (name == "script" || name.StartsWith("metadata/") || name == skip)
            {
                continue;
            }
            Variant value = res.Get(name);
            string text = Format(value);
            if (text == null)
            {
                continue;
            }
            sb.Append($" {name}={text}");
        }
        return sb.ToString().TrimStart();
    }

    // Null for "not worth printing" (an unset or zero-ish value), so the dump
    // stays readable and a line only grows when an author actually set
    // something. Resources print as their FILE, which is what makes the row
    // stable when an embedded sub-resource is hoisted into a shared .tres —
    // the identity is the same either way.
    private static string Format(Variant value)
    {
        switch (value.VariantType)
        {
            case Variant.Type.Nil:
                return null;
            case Variant.Type.Bool:
                return value.AsBool() ? "true" : null;
            case Variant.Type.Int:
                long i = value.AsInt64();
                return i == 0 ? null : i.ToString();
            case Variant.Type.Float:
                float f = value.AsSingle();
                return Mathf.IsZeroApprox(f) ? null : f.ToString("0.####");
            case Variant.Type.String:
            case Variant.Type.StringName:
                string s = value.AsString();
                return string.IsNullOrEmpty(s) ? null : s;
            case Variant.Type.Object:
                var res = value.As<Resource>();
                if (res == null)
                {
                    return null;
                }
                // An embedded sub-resource has no file of its own; name it by
                // its type plus its own stored properties so hoisting it into a
                // file is still a visible, checkable change.
                string path = res.ResourcePath;
                if (string.IsNullOrEmpty(path) || path.Contains("::"))
                {
                    return $"<{res.GetType().Name}: {Properties(res)}>";
                }
                return StringExtensions.GetFile(path);
            case Variant.Type.Array:
            case Variant.Type.PackedStringArray:
                var array = value.AsGodotArray();
                if (array.Count == 0)
                {
                    return null;
                }
                var parts = new List<string>();
                foreach (Variant element in array)
                {
                    parts.Add(Format(element) ?? "<null>");
                }
                return $"[{string.Join(", ", parts)}]";
            default:
                string other = value.ToString();
                return string.IsNullOrEmpty(other) ? null : other;
        }
    }

    // Only files whose header names a SpawnListData are loaded — the header is
    // one line, where loading every .tres under resources/ is the 669-file
    // sweep resource_check pays for.
    private static void Collect(string dir, List<string> paths)
    {
        using DirAccess da = DirAccess.Open(dir);
        if (da == null)
        {
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
                Collect($"{dir}/{entry}", paths);
                continue;
            }
            string file = entry.EndsWith(".remap") ? entry.Substring(0, entry.Length - ".remap".Length) : entry;
            if (file.EndsWith(".tres") && DeclaresSpawnList($"{dir}/{file}"))
            {
                paths.Add($"{dir}/{file}");
            }
        }
        da.ListDirEnd();
    }

    private static bool DeclaresSpawnList(string path)
    {
        using FileAccess fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        return fa != null && fa.GetLine().Contains("script_class=\"SpawnListData\"");
    }
}
