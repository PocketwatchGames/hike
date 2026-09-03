using Godot;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

// Static integrity check over the authored data, driven by the `resource_check`
// cvar off Main._Ready. The data-side twin of shader_check / block_check: needs
// no world, no menu and no renderer, and quits on its own.
//
// Three passes, each chasing a failure that is invisible from the others' side:
//
//   Tool closure — a Resource type reachable from a typed [Export] on a [Tool]
//   class must itself be [Tool], or the editor materializes it as a base
//   Godot.Resource, the typed setter throws, the field reads EMPTY in the
//   inspector, and the next editor save writes the file back without the
//   reference. That is silent data loss, and it is invisible at runtime (there
//   is no [Tool] gate outside the editor), so no amount of playing finds it.
//   This pass reads the C# type graph, so it reports the gap BEFORE any data is
//   lost rather than after.
//
//   Load sweep — every .tres actually loads, and the script the file names is
//   the script the loaded object ended up with. Catches a renamed or moved
//   class, a broken dependency, and a parse error.
//
//   Damage tags — every damaging template declares the damage TYPE it is made
//   of, so "physical" is never inferred from an empty mask. Rides on the load
//   sweep's loaded graph, since almost every DamageData is a [sub_resource]
//   inside a weapon / mob / status file rather than a .tres of its own.
public static class ResourceCheck
{
    private const string ResourceRoot = "res://resources";

    public static void RunAndQuit(SceneTree tree)
    {
        var problems = new List<string>();
        int toolTypes = CheckToolClosure(problems);
        int loaded = CheckLoads(problems);

        GD.Print($"[resource_check] {toolTypes} [Tool] resource types, {loaded} .tres loaded, "
            + $"{_damageTemplates} damage templates typed");
        if (problems.Count == 0)
        {
            GD.Print("[resource_check] ok");
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[resource_check] FAIL: {problems.Count} problems");
            foreach (string p in problems)
            {
                sb.AppendLine($"  {p}");
            }
            GD.PrintErr(sb.ToString().TrimEnd());
        }
        GD.Print("[resource_check] done");
        tree.Quit();
    }

    // --- pass 1: [Tool] closure ------------------------------------------

    private static int CheckToolClosure(List<string> problems)
    {
        Type[] types = typeof(ResourceCheck).Assembly.GetTypes();
        var toolTypes = new List<Type>();
        foreach (Type t in types)
        {
            if (IsResource(t) && IsTool(t))
            {
                toolTypes.Add(t);
            }
        }

        foreach (Type t in toolTypes)
        {
            // A subclass does not inherit [Tool] — Godot's generator reads the
            // attribute off the class itself — so a non-[Tool] subclass of a
            // [Tool] resource has the same problem wherever it is assigned.
            foreach (Type sub in types)
            {
                if (sub != t && t.IsAssignableFrom(sub) && IsResource(sub) && !IsTool(sub))
                {
                    problems.Add($"[Tool] {t.Name} has a non-[Tool] subclass {sub.Name}");
                }
            }

            foreach (MemberInfo member in ExportedMembers(t))
            {
                Type declared = MemberType(member);
                Type element = ElementType(declared);
                if (element == null || !IsResource(element))
                {
                    continue;
                }
                // Only C#-SCRIPTED resources are subject to the rule. A
                // built-in engine type (PackedScene, Texture2D, Curve,
                // AudioStream) has no script for [Tool] to gate, and the editor
                // always materializes it as its real type.
                if (element.Assembly != typeof(ResourceCheck).Assembly)
                {
                    continue;
                }
                // An [Export] typed as bare Godot.Resource has no cast to fail,
                // so it is not subject to the rule either.
                if (element == typeof(Resource) || IsTool(element))
                {
                    continue;
                }
                problems.Add($"[Tool] {t.Name}.{member.Name} is a {element.Name}, which is NOT [Tool] "
                    + "— reads empty in the inspector and is dropped on the next editor save");
            }
        }

        return toolTypes.Count;
    }

    private static bool IsResource(Type t)
    {
        return t != null && !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(Resource).IsAssignableFrom(t);
    }

    // [Tool] is per-class, never inherited — check the class itself.
    private static bool IsTool(Type t) => t.GetCustomAttribute<ToolAttribute>(inherit: false) != null;

    private static IEnumerable<MemberInfo> ExportedMembers(Type t)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (FieldInfo f in t.GetFields(flags))
        {
            if (f.GetCustomAttribute<ExportAttribute>() != null)
            {
                yield return f;
            }
        }
        foreach (PropertyInfo p in t.GetProperties(flags))
        {
            if (p.GetCustomAttribute<ExportAttribute>() != null)
            {
                yield return p;
            }
        }
    }

    private static Type MemberType(MemberInfo m)
    {
        return m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;
    }

    // The Resource type an [Export] ultimately holds: itself, its array element,
    // or the element of a Godot.Collections.Array<T>.
    private static Type ElementType(Type declared)
    {
        if (declared == null)
        {
            return null;
        }
        if (declared.IsArray)
        {
            return declared.GetElementType();
        }
        if (declared.IsGenericType && declared.GetGenericTypeDefinition() == typeof(Godot.Collections.Array<>))
        {
            return declared.GetGenericArguments()[0];
        }
        return declared;
    }

    // --- pass 2: load sweep ----------------------------------------------

    private static int CheckLoads(List<string> problems)
    {
        var paths = new List<string>();
        Collect(ResourceRoot, paths);
        paths.Sort();

        int loaded = 0;
        foreach (string path in paths)
        {
            Resource res;
            try
            {
                res = GD.Load<Resource>(path);
            }
            catch (Exception e)
            {
                problems.Add($"{path}: load threw — {e.Message}");
                continue;
            }
            if (res == null)
            {
                problems.Add($"{path}: failed to load (missing dependency or parse error)");
                continue;
            }
            loaded++;
            WalkForDamageTags(res, path, problems);

            string declared = DeclaredScriptClass(path);
            if (string.IsNullOrEmpty(declared))
            {
                continue;
            }
            string actual = res.GetType().Name;
            if (actual != declared)
            {
                problems.Add($"{path}: declares script_class=\"{declared}\" but loaded as {actual} "
                    + "— the script did not attach (renamed, moved, or the UID points elsewhere)");
            }
        }

        return loaded;
    }

    // --- pass 3: damage-type tags ----------------------------------------

    // Every damaging template must declare what it is MADE of. `Damage` marks a
    // template as damaging; the type bits (Physical / Fire / Electrical /
    // Poison / Magical) say which kind, and receivers key resistance and
    // vulnerability off them — Destructible.destroyedBy is authored against
    // exactly that set.
    //
    // Without this the failure is silent both ways: a physical template that
    // forgets Physical lands as a typeless hit no modifier entry can scale and
    // no destructible accepts, and nothing distinguishes it from a template
    // that genuinely has no type. Walking the loaded graph (rather than just
    // the file roots) is the whole point — almost every DamageData is a
    // [sub_resource] inside a weapon, mob or status file, not a .tres of its own.
    private static int _damageTemplates;
    private static readonly HashSet<ulong> _damageVisited = new();

    private static void WalkForDamageTags(Resource res, string path, List<string> problems)
    {
        if (res == null || !_damageVisited.Add(res.GetInstanceId()))
        {
            return;
        }

        EStat tags = EStat.None;
        string kind = null;
        switch (res)
        {
            case DamageData d: tags = d.tags; kind = nameof(DamageData); break;
            case ContinuousDamageData c: tags = c.tags; kind = nameof(ContinuousDamageData); break;
            case StatusEffectData s: tags = s.tags; kind = nameof(StatusEffectData); break;
        }
        if (kind != null && (tags & EStat.Damage) != 0)
        {
            _damageTemplates++;
            if ((tags & StatModifierUtil.DamageTypeTags) == 0)
            {
                problems.Add($"{path}: a {kind} sets Damage but no damage TYPE "
                    + "(Physical / Fire / Electrical / Poison / Magical) — such a hit is "
                    + "unresistable and breaks no destructible; add the type it is made of");
            }
        }

        // Inherited exports count too, so climb the chain — ExportedMembers is
        // DeclaredOnly.
        for (Type t = res.GetType(); t != null && typeof(Resource).IsAssignableFrom(t); t = t.BaseType)
        {
            foreach (MemberInfo m in ExportedMembers(t))
            {
                object value;
                try
                {
                    value = m is FieldInfo f ? f.GetValue(res) : ((PropertyInfo)m).GetValue(res);
                }
                catch (Exception)
                {
                    // A getter that throws is the [Tool]-closure pass's problem,
                    // not ours — don't let it abort the sweep.
                    continue;
                }
                VisitDamageTagValue(value, path, problems);
            }
        }
    }

    private static void VisitDamageTagValue(object value, string path, List<string> problems)
    {
        switch (value)
        {
            case null:
                return;
            case Resource r:
                WalkForDamageTags(r, path, problems);
                return;
            case Variant v:
                WalkForDamageTags(v.Obj as Resource, path, problems);
                return;
            case string:
                return;
            case System.Array a when a.GetType().GetElementType()?.IsPrimitive == true:
                // PackedFloat32Array / byte buffers — nothing resource-shaped inside.
                return;
            case System.Collections.IEnumerable seq when !IsKeyValuePair(value.GetType()):
                foreach (object item in seq)
                {
                    VisitDamageTagValue(item, path, problems);
                }
                return;
        }

        // A Godot Dictionary enumerates as KeyValuePair, and that is where most
        // damage templates actually live — WeaponData.damageProfiles is keyed by
        // the ItemEvent's damageProfileKey rather than held inline. Missing this
        // case reached only 6 of the ~30 templates in the project.
        Type t = value.GetType();
        if (IsKeyValuePair(t))
        {
            VisitDamageTagValue(t.GetProperty("Value")?.GetValue(value), path, problems);
        }
    }

    private static bool IsKeyValuePair(Type t)
    {
        return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
    }

    // The script_class recorded in the .tres header, or null when the file is a
    // built-in resource type (Gradient, Curve, …) that names no script.
    private static string DeclaredScriptClass(string path)
    {
        using FileAccess fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (fa == null)
        {
            return null;
        }
        string header = fa.GetLine();
        const string Key = "script_class=\"";
        int start = header.IndexOf(Key, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start += Key.Length;
        int end = header.IndexOf('"', start);
        return end < 0 ? null : header.Substring(start, end - start);
    }

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
            if (file.EndsWith(".tres"))
            {
                paths.Add($"{dir}/{file}");
            }
        }
        da.ListDirEnd();
    }
}
