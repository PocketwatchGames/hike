using Godot;

// Fires once the mob has been continuously triggered (combat-aware) for longer
// than `seconds`, measured from the rising edge of its perception latch
// (PerceptionState.triggeredTimeMs). Used as a getaway timer: a fleeing fairy
// that the player keeps engaged but can't bring down within the window bails
// out into its escape (vanish) rather than fleeing forever. No-op while the mob
// isn't triggered, so it only ever ends an active engagement.
[GlobalClass]
public partial class TriggeredTimeoutCondition : BehaviorTransitionData
{
    [Export] public float seconds = 60f;

    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        if (!targetPerception.triggered)
        {
            return false;
        }
        return me.GameTimeMs - targetPerception.triggeredTimeMs >= (ulong)(seconds * 1000f);
    }
}
