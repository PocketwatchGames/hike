using Godot;

// "<title> (X/N)" — count how many of a set of bool scripting-variable flags are
// set. Generic collect-N-flags quest: the Song of the Gods (four verse flags set
// by verse scrolls) is the first user, but any "gather all the pieces" objective
// whose pieces are recorded as quest flags can reuse it. Progress is a Counter;
// N is the flag count. Nothing quest-side needs serializing — the flags live in
// the (save-persisted) ScriptVariableBank and are recomputed live each tick.
[GlobalClass]
public partial class ScriptFlagCountQuestData : QuestData
{
    // Bank variable ids (Bool ScriptVariableData) that count toward this quest;
    // the quest completes once every one is set. Length is the target count.
    // Authored as plain strings (they implicitly convert to the bank's StringName
    // keys) so they serialize as a friendly PackedStringArray.
    [Export] public string[] flags = System.Array.Empty<string>();

    public override QuestState CreateRuntime() => new ScriptFlagCountQuest(this);
}
