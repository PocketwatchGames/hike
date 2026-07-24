using Godot;

// Base class for per-behavior tuning data. Subclass this for each behavior type
// (e.g. IdleBehaviorData, AttackBehaviorData) and override CreateRuntime to
// instantiate the matching BehaviorBase subclass. This keeps per-behavior exports
// typed and lets two nodes in the same brain share a behavior type but differ in
// tuning.
[GlobalClass]
public partial class BehaviorData : Resource
{
    // Resting stance this behavior expresses (see EBehaviorFlags). Static per
    // behavior type — each subclass sets its correct default in its constructor,
    // so authors never touch this and existing brains pick it up on next save.
    // Still [Export] so a one-off brain could override a node's stance if needed.
    // Mob seeds AIOutput.behaviorFlags from this each tick; behaviors may compose
    // extra bits on top at runtime.
    [Export] public EBehaviorFlags behaviorFlags;

    public virtual BehaviorBase CreateRuntime()
    {
        GD.PushError($"BehaviorData subclass '{GetType().Name}' did not override CreateRuntime");
        return null;
    }
}
