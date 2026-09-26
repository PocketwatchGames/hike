using Godot;

[GlobalClass]
public partial class IdleBehaviorData : BehaviorData
{
    // When the pathfinder can't reach the spawn post (across water, up a
    // rise), stand where it is for this long before planning again rather
    // than pushing blindly at it.
    [Export] public float unreachableHomeRetrySeconds = 3f;

    public override BehaviorBase CreateRuntime() => new BehaviorIdle(this);
}
