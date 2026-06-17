# Scripting Variables — Quest Flags / World State

Covers `scripts/gameplay/scripting/` (runtime bank), `scripts/data/scripting/` (authored data + read/write sub-resources), and `resources/data/script_variables/` (authored `.tres`).

A central, save-persisted bank of named variables that conditions/actions read and write **by name** to branch mob conversations and behaviors — quest progress (staged `int`), permanent world flags (`bool` "boss defeated"), counters.

## The bank

`ScriptVariableBank` lives on `WorldSimState.ScriptVars` (reachable via `World.WorldState.SimState.ScriptVars`), is seeded from an authored `ScriptVariableRegistry` at world creation (`WorldState` ctor), and serializes through `SaveGame` (v3). Values are `Bool` (stored as `0/1`) or `Int`, both held as one `long`.

## References are raw `StringName`s

Mod-friendly and quick to author, kept safe by a declared set + two-layer validation rather than typed resource refs:

- Each variable is one authored `ScriptVariableData` (`Id` + `Type` + `DefaultValue` + `Description`) under `resources/data/script_variables/`, collected into `script_variables.tres` (`ScriptVariableRegistry`), wired onto `SimData.ScriptVariables`.
- **Load-time:** the registry self-validates (dup/empty ids) and the bank warns on access to an undeclared name.
- **Data-entry-time:** `tools/validate_script_vars` (mirrors `validate_uids`, wired non-blocking into the build) scans every `.tres`/`.tscn` and flags references to undeclared names, ordering comparisons on a `Bool`, and variables declared but missing from a registry.

## Read / write

**Read** with `ScriptVarCondition` (conversation entry/response gate) or `ScriptVarTransition` (behavior-tree edge) — both `[Export] variable` + `EScriptVarCompareOp op` (`IsTrue`/`IsFalse`/`Equal`/`Greater…`) + `operand`. **Write** with `SetScriptVarAction` (conversation action) — `variable` + `EScriptVarSetOp` (`Set`/`Add`) + `operand`. All three are thin wrappers over `ScriptVarOps.Compare`/`Apply`.

**Adding a use:** author the variable `.tres` + register it, then drop a condition/action sub-resource into the conversation/brain `.tres` referencing its name.

**Adding a write source beyond conversations** (e.g. on boss death): call `world.WorldState.SimState.ScriptVars.SetBool(id, true)` from the gameplay event.
