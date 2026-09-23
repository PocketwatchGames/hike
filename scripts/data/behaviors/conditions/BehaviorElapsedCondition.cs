using Godot;

// Fires once the mob has been running its CURRENT behavior node for longer than
// `seconds`, on the sim clock. Deliberately independent of perception: TickAI
// hands a behavior a zeroed perception slot as soon as nothing is `triggered`,
// so a timer read off the engagement dies exactly when a committed behavior
// still needs it. A fairy that has started its getaway must vanish whether or
// not it can still see what spooked it.
[GlobalClass]
public partial class BehaviorElapsedCondition : BehaviorTransitionData
{
    [Export] public float seconds = 30f;

    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return me.GameTimeMs - me.CurrentBehaviorStartMs >= (ulong)(seconds * 1000f);
    }
}
