using Godot;
using Godot.Collections;

// A world's authored SCRIPTED content — the quests, and later the scripted
// events / cutscenes / scenario wiring specific to this world. Referenced from
// WorldGenData (per-world) and threaded onto WorldState.ScriptData at load, so
// the sim quest driver (Sim.Quests) reads it at runtime.
//
// Deliberately separate from SimData: SimData holds generic physics + content
// expected to be consistent across most sessions, whereas this varies per
// authored world/scenario. Null on a world with no scripted content.
//
// A world whose script needs LOGIC subclasses this (one [GlobalClass] per
// world, e.g. TestWorldScript) and its .tres names the subclass, so which code
// runs is still chosen by data. Sim calls the hooks below; a script never
// subscribes to anything itself, so nothing outlives a run.
//
// A script holds NO state of its own — it is a shared resource, and nothing on
// it is saved. Whatever it must remember ("the gate has been opened") lives in a
// script variable, which saves with the game. Everything it may touch goes
// through WorldScriptApi, never Sim directly.
[GlobalClass]
public partial class WorldScriptData : Resource
{
    // The day advanced at sunrise — every path that rolls it: a camp sleep, the
    // death sleep-off, pray-home.
    public virtual void OnNewDay(WorldScriptApi api, int day) { }

    // The day->night edge.
    public virtual void OnNightfall(WorldScriptApi api) { }

    // A script variable changed value — from a conversation, a quest, or this
    // script. Not fired when a save restores the bank.
    public virtual void OnVariableChanged(WorldScriptApi api, StringName id) { }

    public virtual void OnMobKilled(WorldScriptApi api, SpeciesData species, bool damagedByPlayer) { }

    // Quest surfaced when a party member dies — "Rescue <name>!" — cleared when
    // they're revived or their corpse is destroyed. A RescueQuestData. Null
    // disables the rescue quest in this world.
    [Export] public QuestData rescueQuest;

    // Quest added at nightfall (Sim.OnNightfall) and cleared by sleeping to
    // sunrise — "Return to Camp". A ReturnToCampQuestData. Null disables it.
    [Export] public QuestData returnToCampQuest;

    // Quests seeded into the log at the start of a fresh game (e.g. the Kunkun
    // hunt, the Vyeshal language quest). A save-load repopulates the log from
    // disk instead. Each is a QuestData subclass.
    [Export] public Array<QuestData> startingQuests = new();
}
