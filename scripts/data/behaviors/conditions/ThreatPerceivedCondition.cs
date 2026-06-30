using Godot;

// Which awareness tier a threat transition tests against. Wary is the lower
// perceptionThresholdWary tier (turn + growl); Alert is the full
// perceptionThresholdAlert / triggered tier (attack).
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
    // Also require that the player has actively entered combat (taken damage from
    // a mob, or hit one with a weapon — see Player.CombatEngaged). Gates the
    // escalate-to-attack edge so a guard companion holds at a wary growl until the
    // player chooses to fight. Ignored when requireNone is set (a "cleared" edge).
    [Export] public bool requirePlayerCombat = false;
    // Also require the player to be within this distance (meters) of the mob. 0 =
    // no gate. Pair it with a LARGER MasterTooFarCondition on the destination
    // state's break-off edge to make a hysteresis band (enter close, leave far) so
    // a companion doesn't flicker in and out of a reaction as the player hovers at
    // the boundary. Ignored when requireNone is set (a "cleared" edge).
    [Export] public float maxPlayerDistance = 0f;

    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        bool met = tier == EThreatTier.Alert ? me.ThreatTriggered : me.ThreatWary;
        if (requireNone)
        {
            return !met;
        }
        if (requirePlayerCombat && !(me.World?.player?.CombatEngaged ?? false))
        {
            return false;
        }
        if (maxPlayerDistance > 0f)
        {
            Player master = me.World?.player;
            if (master == null
                || me.GlobalPosition.DistanceSquaredTo(master.GlobalPosition) > maxPlayerDistance * maxPlayerDistance)
            {
                return false;
            }
        }
        return met;
    }
}
