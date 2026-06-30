using System.Collections.Generic;
using Godot;
using Godot.Collections;

// Authored, editor-visible list of every declared scripting variable. Wired
// onto SimData (SimData.ScriptVariables) and seeded into the runtime
// ScriptVariableBank at world creation. The single source of truth for which
// variable names exist and their types/defaults; tools/validate_script_vars
// checks every authored reference against this set.
[GlobalClass]
public partial class ScriptVariableRegistry : Resource
{
    [Export] public Array<ScriptVariableData> variables = new();

    // Appends human-readable problems (null / empty / duplicate Ids) to
    // `issues`. Returns true when clean. Called at world load so a malformed
    // registry surfaces in the log rather than silently mis-seeding the bank.
    public bool Validate(List<string> issues)
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < variables.Count; i++)
        {
            ScriptVariableData v = variables[i];
            if (v == null)
            {
                issues.Add($"ScriptVariableRegistry: null entry at index {i}");
                continue;
            }
            string id = v.id.ToString();
            if (string.IsNullOrEmpty(id))
            {
                issues.Add($"ScriptVariableRegistry: empty Id at index {i}");
                continue;
            }
            if (!seen.Add(id))
            {
                issues.Add($"ScriptVariableRegistry: duplicate Id '{id}'");
            }
        }
        return issues.Count == 0;
    }
}
