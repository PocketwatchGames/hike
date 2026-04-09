using Godot;

[GlobalClass]
public partial class WanderBehaviorData : BehaviorData
{
    [Export] public Vector2 pauseTimeRange = new Vector2(2f, 5f);

    public override BehaviorBase CreateRuntime() => new BehaviorWander(this);
}
