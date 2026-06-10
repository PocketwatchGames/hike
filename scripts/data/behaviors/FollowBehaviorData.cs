using Godot;

// Tuning for BehaviorFollow: a companion mob trails its master (the player),
// closing to followDistance and holding once inside stopDistance.
[GlobalClass]
public partial class FollowBehaviorData : BehaviorData
{
    // Beyond this flat distance from the master the companion paths toward
    // them; inside it she holds position. Kept a little larger than
    // stopDistance so she doesn't jitter start/stop at the boundary.
    [Export] public float followDistance = 3.0f;

    // Path-success radius handed to the navigator — how close the pathing
    // counts as "arrived" so she settles a step short rather than overlapping
    // the player.
    [Export] public float stopDistance = 2.0f;

    // Normalized move speed (fraction of MobData.maxSpeed) used while closing
    // the gap.
    [Export(PropertyHint.Range, "0,1,0.01")] public float followSpeed = 1.0f;

    public override BehaviorBase CreateRuntime() => new BehaviorFollow(this);
}
