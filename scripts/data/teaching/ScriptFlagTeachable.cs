using Godot;

// Sets a named bool in the scripting-variable bank when taught — the bridge
// from the polymorphic TeachableConcept system to the quest-flag store. Used by
// scrolls that record a piece of quest progress (learning one verse of a song)
// rather than granting a language / recipe / spell. Dedup is the flag itself:
// Teach reports a new grant only the first time it flips the flag, so re-reading
// an already-learned scroll is a silent no-op and can't double-count.
[GlobalClass]
public partial class ScriptFlagTeachable : TeachableConcept
{
    // Bank variable (a Bool ScriptVariableData id) set true when taught.
    [Export] public StringName variable;

    // Player-facing name of what this concept represents, used for the scroll's
    // generated "Scroll of <name>" title. Authored directly here because a quest
    // flag has no underlying named Data resource to derive it from.
    [Export] public string conceptName = "";

    ScriptVariableBank Bank(Player player) => player?.Sim?.WorldState?.SimState?.ScriptVars;

    public override string GetDisplayName()
    {
        return conceptName;
    }

    public override bool Teach(Player player)
    {
        ScriptVariableBank bank = Bank(player);
        if (bank == null || string.IsNullOrEmpty(variable.ToString()))
        {
            return false;
        }
        if (bank.GetBool(variable))
        {
            return false;
        }
        bank.SetBool(variable, true);
        return true;
    }

    public override bool IsKnown(Player player)
    {
        ScriptVariableBank bank = Bank(player);
        return bank != null && !string.IsNullOrEmpty(variable.ToString()) && bank.GetBool(variable);
    }
}
