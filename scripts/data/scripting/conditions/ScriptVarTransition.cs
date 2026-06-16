using Godot;

// Behavior-tree transition predicate that reads a scripting variable from the
// central bank — the AI-side analog of ScriptVarCondition. Fires an edge when
// a world flag / quest variable meets the comparison (e.g. a guard mob leaves
// its post once town_gate_opened is true). No world / bank reads as false.
[GlobalClass]
public partial class ScriptVarTransition : BehaviorTransitionData
{
    [Export] public StringName variable;
    [Export] public EScriptVarCompareOp op = EScriptVarCompareOp.IsTrue;
    [Export] public int operand;

    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return ScriptVarOps.Compare(me?.World?.WorldState?.SimState?.ScriptVars, variable, op, operand);
    }
}
