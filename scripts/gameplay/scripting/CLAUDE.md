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

## Disabled interactives

**Any spawn entry can be switched off by a variable**: `SpawnEntryData.disabledVariable` + `disabledWhen` (`True` = a lock something clears, `False` = a gate something opens, e.g. a quest's `completedVariable`). `SpawnEntryData.Spawn` stamps it onto every state the entry files (`WorldState.SpawnStamp`, applied in `AddEntity`), so no entry type carries its own. A painter placement sets it on its fork.

- **Ask `IInteractive.CanUse(interactive, player)`, never `CanActorInteract` directly** — the gate is checked there once, not by each interactive. A site that skips it makes a disabled entity usable.
- An entity whose LOOK depends on the gate (the fountain's ready visuals) reads `EntitySimState.IsDisabled` itself and listens to `ScriptVars.OnChanged`.

## World scripts — logic per world

`WorldScriptData` (the world's `WorldStartData.scriptData`) has virtual hooks — `OnNewDay`, `OnNightfall`, `OnVariableChanged`, `OnMobKilled` — that `Sim` calls (`Sim.WorldScript.cs`). A world that needs logic subclasses it (`scripts/world_scripts/TestWorldScript.cs`) and its `.tres` names the subclass.

- **A script holds no state.** It is a shared resource and nothing on it is saved; whatever it must remember goes in a script variable. `TestWorldScript`'s `town_gate_locked` is both the gate's disabled variable and its run-once guard.
- **A script reaches the game only through `WorldScriptApi`** — variables, `OpenDoor` / `CloseDoor` by placement name. Add a verb there rather than handing out `Sim`; it is the surface a future data-driven trigger would share.
- **Named entities:** a painter placement's `name` is stamped onto `EntitySimState.Name`; `WorldState.FindNamed` finds it whether or not it is streamed in, which is why a door opens in the sim (`Sim.SetDoorOpen`) and the node, if any, follows.
- **Variable names written in C# are invisible to `validate_script_vars`** — declare every one in a registry; the bank warns at runtime on an undeclared name.
- **Modding:** a data-only mod can use every existing script class. A mod shipping its OWN C# script would need its assembly loaded and its types registered with Godot, which Godot .NET does not support out of the box.
