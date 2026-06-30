using Godot;

// Idle behavior for a WILD (untamed) dog: sit in place, watch the player once
// aware of them, and bark on an interval while they stay within barkRadius. A
// tamed dog never reaches this state — the brain's TamedCondition edges route a
// companion to BehaviorWanderFollow and a wild dog here, so one dog brain serves
// both. The wary/attack states aren't reachable either: a wild dog isn't
// dangerous and isn't a companion, so its threat-perception channel stays empty
// (see MobAI.AccumulateThreatPerception). Watching and warning is its whole life.
public partial class BehaviorWildIdle : BehaviorBase
{
    private readonly WildIdleBehaviorData _data;

    // Next game-time (ms) a bark may fire. Re-armed to "now" whenever the player
    // isn't a valid bark target (unaware or out of range) so re-acquiring them
    // barks immediately rather than waiting out a stale interval.
    private ulong _nextBarkMs;

    public BehaviorWildIdle(WildIdleBehaviorData data)
    {
        _data = data;
    }

    public override void OnEnter(Mob me, ulong time)
    {
        me.Navigator?.Stop();
        _nextBarkMs = time;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        output.speed = 0f;

        // "Knows about the player": player-perception has reached at least the
        // wary tier. Below that the dog hasn't noticed them — stay quiet and still.
        Player player = me.World?.player;
        bool aware = player != null && me.mobData != null
            && targetPerception.perception >= me.mobData.perceptionThresholdWary;
        if (!aware)
        {
            _nextBarkMs = time;
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        // Face the player while it watches them.
        Vector3 diff = player.GlobalPosition - me.GlobalPosition;
        Vector2 flat = new Vector2(diff.X, diff.Z);
        if (flat.LengthSquared() > 0.0001f)
        {
            output.yaw = Mathf.Atan2(flat.X, flat.Y);
        }

        // Bark on the interval while the player stays within barkRadius. Intent
        // only — the Mob scene turns it into sound/anim.
        if (flat.LengthSquared() <= _data.barkRadius * _data.barkRadius)
        {
            if (time >= _nextBarkMs)
            {
                output.vocalization = EVocalization.Bark;
                _nextBarkMs = time + (ulong)(Mathf.Max(0.1f, _data.barkIntervalSeconds) * 1000f);
            }
        }
        else
        {
            _nextBarkMs = time;
        }

        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
