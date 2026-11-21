using System;
using System.Collections.Generic;
using Godot;

// A resource's AUTHORED values — its script-declared properties — captured,
// written back and compared. What both tools' undo snapshots a placement's
// fork with, and what the scene editor's preview tells an edited fork by.
//
// Found by asking the resource rather than from a list written by hand: a list
// is the thing that rots, and a field missing from a snapshot fails silently by
// just not being undoable. ScriptVariable is the flag Godot sets on a script's
// own exports, so engine bookkeeping (resource_path, script,
// resource_local_to_scene) is skipped — restoring THOSE would do real damage.
//
// Shallow: a nested resource is compared by identity, so an edit inside a list
// element's own fields is not seen.
public static class ResourceValues
{
    private static readonly Dictionary<Type, StringName[]> _propsByType = new();

    private static StringName[] PropsOf(Resource r)
    {
        Type type = r.GetType();
        if (_propsByType.TryGetValue(type, out StringName[] cached))
        {
            return cached;
        }
        var names = new List<StringName>();
        foreach (Godot.Collections.Dictionary entry in r.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)(long)entry["usage"];
            if ((usage & PropertyUsageFlags.ScriptVariable) != 0)
            {
                names.Add(new StringName(entry["name"].AsString()));
            }
        }
        cached = names.ToArray();
        _propsByType[type] = cached;
        return cached;
    }

    // Null for a null resource.
    public static Variant[] Capture(Resource r)
    {
        if (r == null)
        {
            return null;
        }
        StringName[] props = PropsOf(r);
        var values = new Variant[props.Length];
        for (int i = 0; i < props.Length; i++)
        {
            values[i] = r.Get(props[i]);
        }
        return values;
    }

    public static void Write(Resource r, Variant[] values)
    {
        if (r == null || values == null)
        {
            return;
        }
        StringName[] props = PropsOf(r);
        for (int i = 0; i < props.Length && i < values.Length; i++)
        {
            r.Set(props[i], values[i]);
        }
    }

    public static bool Same(Variant[] a, Variant[] b)
    {
        if (a == null || b == null)
        {
            return ReferenceEquals(a, b);
        }
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            // Compared as TEXT, deliberately. Variant does not compare by value
            // here — MEASURED: two Variants holding the same Vector2I are not
            // Equal, so a no-op press registered as a change and cost an undo
            // slot every time. Every authored property prints faithfully (ints,
            // strings, enums, vectors, bools) and an object reference prints its
            // instance id, so identity still compares as identity.
            if (a[i].ToString() != b[i].ToString())
            {
                return false;
            }
        }
        return true;
    }
}
