using Godot;
using Godot.Collections;

// A world's authored SCRIPTED content — the quests, and later the scripted
// events / cutscenes / scenario wiring specific to this world. Referenced from
// WorldGenData (per-world) and threaded onto WorldState.ScriptData at load, so
// the sim quest driver (World.Quests) reads it at runtime.
//
// Deliberately separate from SimData: SimData holds generic physics + content
// expected to be consistent across most sessions, whereas this varies per
// authored world/scenario. Null on a world with no scripted content.
[GlobalClass]
public partial class WorldScriptData : Resource
{
    // Quest surfaced when a party member dies — "Rescue <name>!" — cleared when
    // they're revived or their corpse is destroyed. A RescueQuestData. Null
    // disables the rescue quest in this world.
    [Export] public QuestData rescueQuest;

    // Quest added at nightfall (World.OnNightfall) and cleared by sleeping to
    // sunrise — "Return to Camp". A ReturnToCampQuestData. Null disables it.
    [Export] public QuestData returnToCampQuest;

    // Quests seeded into the log at the start of a fresh game (e.g. the Kunkun
    // hunt, the Vyeshal language quest). A save-load repopulates the log from
    // disk instead. Each is a QuestData subclass.
    [Export] public Array<QuestData> startingQuests = new();
}
