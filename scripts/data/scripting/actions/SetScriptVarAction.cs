using Godot;

// Conversation side-effect that writes a scripting variable in the central
// bank — the write half of the quest / world-flag system. Author on a
// response (or branch endActions / entry actions) to record permanent
// progress: Set boss_dragon_defeated = 1 when the player reports the kill, or
// Add 1 to advance a quest stage / bump a counter. Persists via SaveGame.
[GlobalClass]
public partial class SetScriptVarAction : ConversationAction
{
    // Authored variable name. Must match a ScriptVariableData.Id in the
    // registry; validate_script_vars flags typos at build time.
    [Export] public StringName variable;
    [Export] public EScriptVarSetOp op = EScriptVarSetOp.Set;
    // For Set: the new value (bools use 0 / 1). For Add: the delta.
    [Export] public int operand = 1;

    public override void Execute(ConversationContext ctx)
    {
        ScriptVarOps.Apply(ctx.sim?.WorldState?.SimState?.ScriptVars, variable, op, operand);
    }
}
