using Godot;

// Tuning for BehaviorFairyEscape — the fairy's getaway. Once a fairy has fled
// far enough from the player it shoots straight up `ascentHeight` metres over
// `ascentSeconds` while fading out, then despawns for good.
[GlobalClass]
public partial class FairyEscapeBehaviorData : BehaviorData
{
    [Export] public float ascentHeight = 10f;
    [Export] public float ascentSeconds = 1.5f;

    public override BehaviorBase CreateRuntime() => new BehaviorFairyEscape(this);
}
