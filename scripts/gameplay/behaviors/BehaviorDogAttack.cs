using Godot;

// Companion variant of BehaviorAttack: identical approach / encircle / swing
// logic, but the victim is the enemy the mob has built threat-perception toward
// (MobSimState.ThreatPerception, accumulated in MobAI.AccumulateThreatPerception)
// instead of the player. Overriding ResolveTarget is the entire difference —
// standoff slots, cooldowns, yaw, and firing the action profile are inherited
// unchanged. The brain enters here once threat perception latches `triggered`
// (PerceptionThresholdAlert) and drops back to BehaviorWary when it clears.
public partial class BehaviorDogAttack : BehaviorAttack
{
    public BehaviorDogAttack(DogAttackBehaviorData data) : base(data)
    {
    }

    protected override Node3D ResolveTarget(Mob me, ref PerceptionState targetPerception, out bool canSee, out Vector3 targetPos, out Vector3 lastKnownPosition)
    {
        Mob threat = me.ThreatTarget;
        if (threat == null)
        {
            canSee = false;
            targetPos = me.GlobalPosition;
            lastKnownPosition = me.GlobalPosition;
            return null;
        }
        canSee = me.ThreatCanSee;
        lastKnownPosition = me.ThreatLastKnownPosition;
        targetPos = canSee ? threat.GlobalPosition : lastKnownPosition;
        return threat;
    }
}
