using Godot;

// Base class for per-behavior tuning data. Subclass this for each behavior type
// (e.g. IdleBehaviorData, AttackBehaviorData) and override CreateRuntime to
// instantiate the matching BehaviorBase subclass. This keeps per-behavior exports
// typed and lets two nodes in the same brain share a behavior type but differ in
// tuning.
[GlobalClass]
public partial class BehaviorData : Resource
{
    public virtual BehaviorBase CreateRuntime()
    {
        GD.PushError($"BehaviorData subclass '{GetType().Name}' did not override CreateRuntime");
        return null;
    }
}
