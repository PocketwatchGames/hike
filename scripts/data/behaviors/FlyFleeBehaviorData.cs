using Godot;

// Tuning for BehaviorFlyFlee — a flying mob taking off and traveling to a
// landing spot away from a threat.
[GlobalClass]
public partial class FlyFleeBehaviorData : BehaviorData
{
    public FlyFleeBehaviorData() { behaviorFlags = EBehaviorFlags.Disengaging; }

    // A perch must lie at least this far away to be considered (don't pick the
    // branch you're already on) and no farther than maxPerchRange.
    [Export] public float minPerchRange = 6f;
    [Export] public float maxPerchRange = 30f;

    // Cosine of the flee-cone half-angle the perch must fall within, measured
    // from the away-from-threat direction. 1 = directly away only, 0 = any
    // perch in the forward hemisphere, negative = even slightly behind.
    [Export(PropertyHint.Range, "-1,1,0.01")] public float perchConeDot = 0.3f;

    // When no perch qualifies, fly this far along the flee direction and land
    // on the ground there instead.
    [Export] public float fleeRange = 20f;

    // Give up on the chosen destination after this long (stuck against geometry,
    // perch became unreachable) and land where we are.
    [Export] public float pathTimeoutSeconds = 5f;

    // Minimum gap between flees, written to Mob.ReactionReadyMs on takeoff so the
    // shared IncomingProjectileCondition can't re-flee this bird every tick while
    // a volley arrives. Same reaction clock the grounded dodge uses.
    [Export] public float reactionCooldownSeconds = 2.5f;

    public override BehaviorBase CreateRuntime() => new BehaviorFlyFlee(this);
}
