# Scripting Variables — Quest Flags / World State

Covers `scripts/gameplay/scripting/` (runtime bank), `scripts/data/scripting/` (authored data + read/write sub-resources), and `resources/data/worlds/shared/script_variables/` (authored `.tres`).

A central, save-persisted bank of named variables that conditions/actions read and write **by name** to branch mob conversations and behaviors — quest progress (staged `int`), permanent world flags (`bool` "boss defeated"), counters.

## The bank

`ScriptVariableBank` lives on `WorldSimState.ScriptVars` (reachable via `World.WorldState.SimState.ScriptVars`), is seeded from every `ScriptVariableRegistry` on `SimData.scriptVariables` at world creation (`WorldState` ctor), and serializes through `SaveGame` (v3). Ids are unique across the whole list, not just within one registry — there are two, because one of them is generated (see below). Values are `Bool` (stored as `0/1`) or `Int`, both held as one `long`.

## References are raw `StringName`s

Mod-friendly and quick to author, kept safe by a declared set + two-layer validation rather than typed resource refs:

- Each variable is one authored `ScriptVariableData` (`Id` + `Type` + `DefaultValue` + `Description`) under `resources/data/worlds/shared/script_variables/`, collected into `script_variables.tres` (`ScriptVariableRegistry`), wired onto `SimData.scriptVariables`.
- **An NPC's own flag is not authored at all** — a conversation sheet writes `npcvar:<name>` in a condition or action cell and the importer generates both the sub-resource and the declaration, into `npc_variables.tres` beside the authored registry. A sheet can read a global one with `var:<name>`. See the Conversations section of the root CLAUDE.md for the cell syntax.
- **Load-time:** the registry self-validates (dup/empty ids) and the bank warns on access to an undeclared name.
- **Data-entry-time:** `tools/validate_script_vars` (mirrors `validate_uids`, wired non-blocking into the build) scans every `.tres`/`.tscn` and flags references to undeclared names, ordering comparisons on a `Bool`, and variables declared but missing from a registry.

## Read / write

**Read** with `ScriptVarCondition` (conversation entry/response gate) or `ScriptVarTransition` (behavior-tree edge) — both `[Export] variable` + `EScriptVarCompareOp op` (`IsTrue`/`IsFalse`/`Equal`/`Greater…`) + `operand`. **Write** with `SetScriptVarAction` (conversation action) — `variable` + `EScriptVarSetOp` (`Set`/`Add`) + `operand`. All three are thin wrappers over `ScriptVarOps.Compare`/`Apply`.

**Adding a use:** in a conversation, write `npcvar:`/`var:` in the sheet cell and there is nothing else to do. Elsewhere (a brain `.tres`), author the variable `.tres` + register it, then drop a condition/transition/action sub-resource referencing its name.

**Adding a write source beyond conversations** (e.g. on boss death): call `world.WorldState.SimState.ScriptVars.SetBool(id, true)` from the gameplay event.
