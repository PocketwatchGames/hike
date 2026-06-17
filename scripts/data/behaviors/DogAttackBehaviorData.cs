using Godot;

// Companion attack tuning. Inherits all of AttackBehaviorData's standoff /
// encircle / cooldown / action-profile tuning; the only behavioral difference
// is the target — BehaviorDogAttack engages the mob's accumulated threat
// (MobSimState.ThreatPerception) instead of the player. The threat team / range
// / awareness thresholds live on MobData (threatTeam / VisionRange /
// PerceptionThreshold*), so there is nothing extra to author here.
[GlobalClass]
public partial class DogAttackBehaviorData : AttackBehaviorData
{
    public override BehaviorBase CreateRuntime() => new BehaviorDogAttack(this);
}
