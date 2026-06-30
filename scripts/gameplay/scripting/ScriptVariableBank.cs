using System;
using System.Collections.Generic;
using System.IO;
using Godot;

// Runtime store for the central bank of named scripting variables — quest
// progress, world flags (boss defeated), counters. Owned by WorldSimState
// (worldState.SimState.ScriptVars), so it serializes with the rest of the
// run-spanning player progression and is reachable from gameplay via
// World.WorldState.SimState.ScriptVars (a behavior predicate gets there
// through Mob.World; a conversation through ConversationContext.world).
//
// Values are keyed by the variable's authored StringName Id. Bool and Int
// alike are stored as a single long (bool = value != 0); the declared type
// lives on the registry and drives validation, not storage. References are
// raw names (mod-friendly, quick to author); the registry + the
// validate_script_vars tool are the safety net against typos.
public class ScriptVariableBank
{
    private readonly Dictionary<StringName, long> _values = new();
    private readonly Dictionary<StringName, ScriptVariableData> _declared = new();

    // Fired whenever a variable changes value (or is first assigned). World
    // reactions / UI can subscribe to refresh when a flag flips.
    public event Action<StringName> OnChanged;

    // Seed defaults from the authored registry and capture the declared set
    // for type lookups + undeclared-access warnings. Safe with a null
    // registry (a world authored without any variables) — the bank just
    // starts empty. Save data loaded afterward overrides these defaults.
    public void Initialize(ScriptVariableRegistry registry)
    {
        _values.Clear();
        _declared.Clear();
        if (registry == null)
        {
            return;
        }
        var issues = new List<string>();
        registry.Validate(issues);
        foreach (string issue in issues)
        {
            GD.PushError(issue);
        }
        foreach (ScriptVariableData v in registry.variables)
        {
            if (v == null || string.IsNullOrEmpty(v.id.ToString()))
            {
                continue;
            }
            _declared[v.id] = v;
            _values[v.id] = v.defaultValue;
        }
    }

    public bool IsDeclared(StringName id)
    {
        return _declared.ContainsKey(id);
    }

    public long GetInt(StringName id)
    {
        WarnIfUndeclared(id);
        return _values.TryGetValue(id, out long v) ? v : 0;
    }

    public bool GetBool(StringName id)
    {
        return GetInt(id) != 0;
    }

    public void SetInt(StringName id, long value)
    {
        WarnIfUndeclared(id);
        if (_values.TryGetValue(id, out long existing) && existing == value)
        {
            return;
        }
        _values[id] = value;
        OnChanged?.Invoke(id);
    }

    public void SetBool(StringName id, bool value)
    {
        SetInt(id, value ? 1 : 0);
    }

    public void AddInt(StringName id, long delta)
    {
        SetInt(id, GetInt(id) + delta);
    }

    // --- Serialization (SaveGame v3+) ---
    // Writes (count, [idString, value]*). Keyed by name rather than registry
    // index so a registry edit between sessions can't shift a saved value
    // onto the wrong variable; values for names no longer declared still
    // round-trip (a removed-then-restored variable keeps its progress).
    public void Serialize(BinaryWriter w)
    {
        w.Write(_values.Count);
        foreach (KeyValuePair<StringName, long> kv in _values)
        {
            w.Write(kv.Key.ToString());
            w.Write(kv.Value);
        }
    }

    public void Deserialize(BinaryReader r)
    {
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string id = r.ReadString();
            long value = r.ReadInt64();
            _values[id] = value;
        }
    }

    // Undeclared access is almost always a typo'd name; warn once at the call
    // so it surfaces during authoring. Gated on a non-empty declared set so a
    // world with no registry (or pre-Initialize) doesn't spam. The access
    // still functions (reads default 0, stores loosely) so a mod referencing
    // a not-yet-registered name degrades gracefully rather than crashing.
    private void WarnIfUndeclared(StringName id)
    {
        if (_declared.Count > 0 && !_declared.ContainsKey(id))
        {
            GD.PushWarning($"ScriptVariableBank: access to undeclared variable '{id}' — declare it in a ScriptVariableRegistry (resources/data/script_variables/).");
        }
    }
}
