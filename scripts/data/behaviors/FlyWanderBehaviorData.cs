using Godot;

// Airborne idle for a flying mob: the same wander cadence as WanderBehaviorData,
// but the mob patrols the airspace at cruise altitude instead of walking the
// ground. Runtime adds the airborne flag on every running tick (see
// BehaviorFlyWander); all the wander tuning is inherited.
[GlobalClass]
public partial class FlyWanderBehaviorData : WanderBehaviorData
{
    public override BehaviorBase CreateRuntime() => new BehaviorFlyWander(this);
}
