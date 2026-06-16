using Godot;

// How a ScriptVar predicate compares the stored value against its operand.
// IsTrue/IsFalse are the bool tests (value != 0); the rest are int
// comparisons. Equal/NotEqual work on either type; the ordering ops
// (Greater.. Less..) only make sense on Int variables — the validator flags
// them when used on a Bool.
public enum EScriptVarCompareOp
{
    IsTrue,
    IsFalse,
    Equal,
    NotEqual,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
}

// How a write action mutates the stored value. Set replaces it (bools use
// 0/1); Add increments (counters, quest-stage advance).
public enum EScriptVarSetOp
{
    Set,
    Add,
}

// Shared read/write logic for the scripting-variable conditions and actions,
// so ScriptVarCondition (conversation), ScriptVarTransition (behavior), and
// SetScriptVarAction stay tiny and identical in semantics. A null bank
// (no world yet) compares as false and applies as a no-op.
public static class ScriptVarOps
{
    public static bool Compare(ScriptVariableBank bank, StringName id, EScriptVarCompareOp op, long operand)
    {
        if (bank == null)
        {
            return false;
        }
        long v = bank.GetInt(id);
        return op switch
        {
            EScriptVarCompareOp.IsTrue => v != 0,
            EScriptVarCompareOp.IsFalse => v == 0,
            EScriptVarCompareOp.Equal => v == operand,
            EScriptVarCompareOp.NotEqual => v != operand,
            EScriptVarCompareOp.GreaterThan => v > operand,
            EScriptVarCompareOp.GreaterOrEqual => v >= operand,
            EScriptVarCompareOp.LessThan => v < operand,
            EScriptVarCompareOp.LessOrEqual => v <= operand,
            _ => false,
        };
    }

    public static void Apply(ScriptVariableBank bank, StringName id, EScriptVarSetOp op, long operand)
    {
        if (bank == null)
        {
            return;
        }
        switch (op)
        {
            case EScriptVarSetOp.Set:
                bank.SetInt(id, operand);
                break;
            case EScriptVarSetOp.Add:
                bank.AddInt(id, operand);
                break;
        }
    }
}
