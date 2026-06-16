using Godot;

// Conversation predicate that reads a scripting variable from the central
// bank. Gate an entry branch or a player response on quest progress / world
// flags — e.g. only offer "[About the dragon...]" once boss_dragon_defeated
// is true, or show a follow-up once main_quest_stage >= 2. No world / bank
// reads as false (the response stays hidden).
[GlobalClass]
public partial class ScriptVarCondition : ConversationCondition
{
    // Authored variable name. Must match a ScriptVariableData.Id in the
    // registry; validate_script_vars flags typos at build time.
    [Export] public StringName variable;
    [Export] public EScriptVarCompareOp op = EScriptVarCompareOp.IsTrue;
    // Right-hand operand for the int comparisons (Equal / Greater / Less /
    // ...). Ignored by IsTrue / IsFalse.
    [Export] public int operand;

    public override bool Evaluate(ConversationContext ctx)
    {
        return ScriptVarOps.Compare(ctx.world?.WorldState?.SimState?.ScriptVars, variable, op, operand);
    }
}
