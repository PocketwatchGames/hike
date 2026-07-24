using Godot;

// Tuning for BehaviorWary: a companion that has perceived an enemy at the wary
// tier (but not yet the full alert/attack tier) holds its ground, faces the
// threat, and vocalizes periodically as a warning — barking at a dangerous
// enemy, growling at anything else. Escalates to BehaviorDogAttack when
// perception latches `triggered`, and falls back to Follow when the threat
// clears or the player leaves (transitions on the brain node).
[GlobalClass]
public partial class DogWaryBehaviorData : BehaviorData
{
    public DogWaryBehaviorData() { behaviorFlags = EBehaviorFlags.Engaging; }

    // Seconds between wary vocalizations (bark or growl). The first fires on entry.
    [Export] public float growlIntervalSeconds = 2.5f;

    public override BehaviorBase CreateRuntime() => new BehaviorWary(this);
}
