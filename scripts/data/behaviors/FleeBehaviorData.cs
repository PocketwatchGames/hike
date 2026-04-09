using Godot;

[GlobalClass]
public partial class FleeBehaviorData : BehaviorData
{
    // Radius of the candidate flee point around the mob. Picked via a random
    // angle biased roughly away from the threat.
    [Export] public float fleeRange = 10f;
    // How long the mob will try to reach a flee point before giving up and
    // picking a new one.
    [Export] public float pathTimeoutSeconds = 3f;
    // When no walkable flee point is found this frame, the mob stalls for a
    // random interval in this range before trying again, to avoid burning
    // every tick on rejected random rolls.
    [Export] public Vector2 pauseTimeRange = new Vector2(0.5f, 1.5f);

    public override BehaviorBase CreateRuntime() => new BehaviorFlee(this);
}
