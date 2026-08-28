using Godot;

// Storage/branch type of a scripting variable. Bool covers binary world flags
// (boss defeated, gate opened); Int covers counters and staged quest progress
// (0 = unstarted, 1 = active, 2 = done). Both are stored as one long at
// runtime — this only drives defaults and validation.
public enum EScriptVarType
{
    Bool,
    Int,
}

// Authored declaration of one named scripting variable in the central bank.
// One .tres per variable under resources/data/worlds/shared/script_variables/, collected
// into a ScriptVariableRegistry. Conditions/actions reference a variable by
// its Id (a raw StringName) so authoring stays quick and mod-friendly; the
// declaration here gives that name a type + default and gives the validator
// something to check references against. Runtime values live in
// ScriptVariableBank, never here — this is static authored Data.
[GlobalClass]
public partial class ScriptVariableData : Resource
{
    // Stable lookup key referenced by conditions/actions and save files.
    // Renaming it orphans existing references and any saved value — edit the
    // .tres contents, not the Id, once a variable is in use.
    [Export] public StringName id;

    [Export] public EScriptVarType type = EScriptVarType.Bool;

    // Seeded into the bank at world creation, before save data loads. Bool
    // reads this as (value != 0).
    [Export] public int defaultValue;

    // Author-facing note: what this variable means and who reads/writes it.
    [Export(PropertyHint.MultilineText)] public string description = "";
}
