using Godot;

// Companion attack tuning. Inherits all of AttackBehaviorData's standoff /
// encircle / cooldown / action-profile tuning; the only behavioral difference
// is the target — BehaviorDogAttack engages the mob's accumulated threat
// (MobSimState.ThreatPerception) instead of the player. The vision range /
// awareness thresholds live on MobData (visionRange / perceptionThreshold*), and
// the dog scans threats by virtue of being a companion, so there is nothing
// extra to author here.
[GlobalClass]
public partial class DogAttackBehaviorData : AttackBehaviorData
{
    // Leash to the master: if the player gets farther than this (meters) while the
    // dog is chasing a threat, it breaks off the fight and runs back toward the
    // player, resuming the attack only once it's back within range. A companion
    // stays with its master rather than running down a threat across the map.
    // <= 0 disables the leash.
    [Export] public float masterBreakoffDistance = 16f;
    // Move speed (normalized, 1 = full) while running back to the master after a
    // breakoff — a brisk return so the dog doesn't dawdle out in the open.
    [Export] public float breakoffReturnSpeed = 1f;

    public override BehaviorBase CreateRuntime() => new BehaviorDogAttack(this);
}
