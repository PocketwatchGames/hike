using Godot;

// Brain transition predicate on the mob's tamed state. Fires when the mob is a
// tamed companion — or, when `requireNone` is set, when it is NOT (a wild mob).
// Branches the shared dog brain between BehaviorWanderFollow (companion) and
// BehaviorWildIdle (wild) without forking the brain per individual.
[GlobalClass]
public partial class TamedCondition : BehaviorTransitionData
{
    // Invert the test: fire when the mob is NOT tamed (the wild edge).
    [Export] public bool requireNone = false;

    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return requireNone ? !me.IsCompanion : me.IsCompanion;
    }
}
