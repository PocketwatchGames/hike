using Godot;

// Companion guard reaction to a perceived DANGEROUS enemy below the attack
// threshold: stand ground, face it, and bark on an interval. (Harmless creatures
// never reach this state — the companion's threat channel is dangerous-only; a
// curious glance at wildlife lives in BehaviorWanderFollow's sniff.) Transitions
// (evaluated first) take the mob to BehaviorDogAttack once perception latches
// `triggered`, and back to Follow when it clears or the player wanders out of
// leash range. The target and its last-known position come from the mob's
// accumulated threat-perception (MobSimState.ThreatPerception), so the dog keeps
// facing an enemy it briefly loses sight of rather than snapping back to idle.
public partial class BehaviorWary : BehaviorBase
{
    private readonly DogWaryBehaviorData _data;
    // Next game-time (ms) to emit a wary vocalization. The first one fires on
    // entry, then it repeats on the interval for as long as the dog stays wary.
    private ulong _nextVocalizeMs;

    public BehaviorWary(DogWaryBehaviorData data)
    {
        _data = data;
    }

    // Halt any in-progress follow goal and vocalize immediately on entering the
    // wary state (re-entry from a re-perceived threat re-arms the first cry).
    public override void OnEnter(Mob me, ulong time)
    {
        me.Navigator?.Stop();
        _nextVocalizeMs = time;
        if (CVars.companionDebug.Value)
        {
            GD.Print($"[companion] Wary.OnEnter threat={me.ThreatTarget?.mobData?.displayName.ToString() ?? "null"}");
        }
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

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

        // Periodic warning bark at the dangerous enemy it's facing. Intent only —
        // the Mob scene turns it into sound/anim.
        if (time >= _nextVocalizeMs)
        {
            output.vocalization = EVocalization.Bark;
            _nextVocalizeMs = time + (ulong)(Mathf.Max(0.1f, _data.growlIntervalSeconds) * 1000f);
        }

        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
