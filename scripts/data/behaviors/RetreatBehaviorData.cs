using Godot;

// Tuning for BehaviorRetreat — the "disengage from a safe player" response:
// stare a beat, then walk away until far enough to lose interest. Wired on
// aggressive brains as the destination of Attack → Retreat (TargetSafeCondition)
// and Idle/Wander → Retreat (HurtWhileTargetSafeCondition).
[GlobalClass]
public partial class RetreatBehaviorData : BehaviorData
{
    // Seconds the mob holds its gaze on the (safe) player before turning to
    // leave. Skipped entirely when the mob is retreating because it was just
    // attacked — a hit means bolt, not stare.
    [Export] public float stareSeconds = 2f;
    // Distance (meters) from the player the mob must reach before it gives up
    // and reverts to its default behavior. Set above the safety-zone radius so
    // the mob actually clears the zone rather than loitering at its edge.
    [Export] public float disengageDistance = 22f;
    // Move speed while walking away (matches the wander/flee "ambient" pace by
    // default; a scared retreat can author this higher).
    [Export] public float retreatSpeed = 1f;
    // Radius of each candidate away-point picked around the mob as it leaves.
    [Export] public float legRange = 8f;
    // How long the mob tries to reach one away-point before picking another.
    [Export] public float legTimeoutSeconds = 3f;
    // A hit landed within this many seconds counts as "just attacked" — the
    // mob skips the stare and flees immediately.
    [Export] public float recentDamageSeconds = 1.5f;

    public override BehaviorBase CreateRuntime() => new BehaviorRetreat(this);
}
