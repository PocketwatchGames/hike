using Godot;

// Companion guard reaction below the attack threshold: stand ground, face the
// perceived threat, and growl on an interval. Transitions (evaluated first) take
// the mob to BehaviorDogAttack once perception latches `triggered`, and back to
// Follow when the threat clears or the player wanders out of leash range. The
// target and its last-known position come from the mob's accumulated
// threat-perception (MobSimState.ThreatPerception), so the dog keeps facing a
// threat it briefly loses sight of rather than snapping back to idle.
public partial class BehaviorWary : BehaviorBase
{
    private readonly DogWaryBehaviorData _data;
    private ulong _nextGrowlMs;

    public BehaviorWary(DogWaryBehaviorData data)
    {
        _data = data;
    }

    // Halt any in-progress follow goal and growl immediately on entering the
    // wary state (re-entry from a re-perceived threat re-arms the first growl).
    public override void OnEnter(Mob me, ulong time)
    {
        me.Navigator?.Stop();
        _nextGrowlMs = time;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        output.useTorch = me.ShouldUseTorch;
        output.speed = 0f;

        // Face the threat (its live position while seen, else last-known).
        Mob threat = me.ThreatTarget;
        if (threat != null)
        {
            Vector3 facePos = me.ThreatCanSee ? threat.GlobalPosition : me.ThreatLastKnownPosition;
            Vector3 diff = facePos - me.GlobalPosition;
            Vector2 flat = new Vector2(diff.X, diff.Z);
            if (flat.LengthSquared() > 0.0001f)
            {
                output.yaw = Mathf.Atan2(flat.X, flat.Y);
            }
        }

        // Periodic warning growl.
        if (_data.growlEffect != null && time >= _nextGrowlMs)
        {
            me.PlayWorldEffect(_data.growlEffect);
            _nextGrowlMs = time + (ulong)(Mathf.Max(0.1f, _data.growlIntervalSeconds) * 1000f);
        }

        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
