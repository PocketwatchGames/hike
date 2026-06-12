using Godot;

// Tuning for BehaviorWary: a companion that has perceived an enemy at the wary
// tier (but not yet the full alert/attack tier) holds its ground, faces the
// threat, and growls periodically as a warning. Escalates to BehaviorDogAttack
// when perception latches `triggered`, and falls back to Follow when the threat
// clears or the player leaves (transitions on the brain node).
[GlobalClass]
public partial class DogWaryBehaviorData : BehaviorData
{
    // One-shot Fx (audio) spawned at the mob on each growl. Null = silent wary.
    [Export] public PackedScene growlEffect;
    // Seconds between growls while wary. The first growl fires on entry.
    [Export] public float growlIntervalSeconds = 2.5f;

    public override BehaviorBase CreateRuntime() => new BehaviorWary(this);
}
