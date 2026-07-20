using Godot;

// Companion variant of BehaviorAttack: identical approach / encircle / swing
// logic, but the victim is the enemy the mob has built threat-perception toward
// (MobSimState.ThreatPerception, accumulated in MobAI.AccumulateThreatPerception)
// instead of the player. Overriding ResolveTarget is the entire difference —
// standoff slots, cooldowns, yaw, and firing the action profile are inherited
// unchanged. The brain enters here once threat perception latches `triggered`
// (perceptionThresholdAlert) and drops back to BehaviorWary when it clears.
public partial class BehaviorDogAttack : BehaviorAttack
{
    private readonly DogAttackBehaviorData _data;
    // Set on entry, consumed on the first Run tick that commits to the fight so
    // the combat snarl rides out on AIOutput as intent only.
    private bool _snarlPending;

    public BehaviorDogAttack(DogAttackBehaviorData data) : base(data)
    {
        _data = data;
    }

    // Entering the attack state means threat perception has latched and the dog
    // is closing in; arm a snarl. A re-entry from Wary (after the fight briefly
    // lulls) re-arms it, which reads as re-engaging.
    public override void OnEnter(Mob me, ulong time)
    {
        base.OnEnter(me, time);
        _snarlPending = true;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        BehaviorOutput result = RunAttack(me, time, ref targetPerception, ref output);
        // Snarl the first tick we actually commit to the fight (entering combat).
        if (_snarlPending && result.result == EBehaviorResult.Running)
        {
            _snarlPending = false;
            output.vocalization = EVocalization.Snarl;
        }
        return result;
    }

    private BehaviorOutput RunAttack(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        // Master leash: while the player is beyond masterBreakoffDistance, abandon
        // the chase and run straight back rather than committing to a fight across
        // the map. We stay in this behavior while returning (transitioning to the
        // follow state here would just bounce straight back through the threat edge
        // while still too far and freeze the dog), and resume attacking once back in
        // range. The threat-cleared edge still takes us home promptly if the fight
        // ends mid-return.
        Player master = me.Sim?.player;
        if (_data.masterBreakoffDistance > 0f && master != null
            && me.GlobalPosition.DistanceSquaredTo(master.GlobalPosition) > _data.masterBreakoffDistance * _data.masterBreakoffDistance)
        {
            if (TryTransitions(me, time, ref targetPerception, out StringName destination))
            {
                ReleaseSlot(me);
                return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
            }
            ReleaseSlot(me);
            me.Navigator?.Goto(master.GlobalPosition, allowFalling: true, avoidHazards: true);
            output.speed = _data.breakoffReturnSpeed;
            return new BehaviorOutput(EBehaviorResult.Running);
        }
        return base.Run(me, time, ref targetPerception, ref output);
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
