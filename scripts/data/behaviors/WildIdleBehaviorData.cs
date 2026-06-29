using Godot;

// Tuning for BehaviorWildIdle: a WILD (untamed) dog that sits in place and
// barks at the player when it is aware of them and they come within barkRadius.
// The tamed companion never runs this — the dog brain routes between this and
// BehaviorWanderFollow on the mob's tamed state (TamedCondition).
[GlobalClass]
public partial class WildIdleBehaviorData : BehaviorData
{
    // Player must be within this horizontal distance (meters) of the dog for it
    // to bark.
    [Export] public float barkRadius = 5f;
    // Seconds between barks while the player stays in range. The first fires as
    // soon as the player crosses into range.
    [Export] public float barkIntervalSeconds = 2f;

    public override BehaviorBase CreateRuntime() => new BehaviorWildIdle(this);
}
