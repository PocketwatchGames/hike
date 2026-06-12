using Godot;

// Which awareness tier a threat transition tests against. Wary is the lower
// PerceptionThresholdWary tier (turn + growl); Alert is the full
// PerceptionThresholdAlert / triggered tier (attack).
public enum EThreatTier
{
    Wary,
    Alert,
}

// Brain transition predicate for companion combat. Reads the mob's accumulated
// threat perception (MobSimState.ThreatPerception, built in
// MobAI.AccumulateThreatPerception) and fires when it has crossed `tier` — or,
// when `requireNone` is set, when it has NOT. Drives every threat edge: acquire
// (Follow/Stay → Wary at Wary, → DogAttack at Alert), de-escalate (DogAttack →
// Wary when Alert clears), and release (Wary → Follow when Wary clears). The
// actual thresholds live on MobData so a single set of numbers governs both the
// transitions and the perception accumulation.
[GlobalClass]
public partial class ThreatPerceivedCondition : BehaviorTransitionData
{
    [Export] public EThreatTier tier = EThreatTier.Wary;
    // Invert the test: fire when the tier is NOT met (the "threat cleared" edge).
    [Export] public bool requireNone = false;

    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        bool met = tier == EThreatTier.Alert ? me.ThreatTriggered : me.ThreatWary;
        return requireNone ? !met : met;
    }
}
