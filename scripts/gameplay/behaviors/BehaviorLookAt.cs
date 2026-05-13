using Godot;

// Non-combatant response to a yell / noise investigation: stand still, face
// the source for a fixed window, then go back to the default behavior. Used
// by mobs whose brains don't wire BehaviorInvestigate (kun-kuns, villagers)
// so they react to nearby commotion without committing to walking toward it.
public partial class BehaviorLookAt : BehaviorBase
{
    private readonly LookAtBehaviorData _data;
    private ulong _lookUntilMs;

    public BehaviorLookAt(LookAtBehaviorData data)
    {
        _data = data;
    }

    // Latch the deadline on every re-entry so a follow-up yell that arrives
    // after we'd already returned to default gives us the full duration
    // again. Stop the navigator so any in-progress wander goal halts here
    // instead of carrying us through the look window.
    public override void OnEnter(Mob me, ulong time)
    {
        _lookUntilMs = time + (ulong)(_data.lookDurationSeconds * 1000f);
        me.Navigator?.Stop();
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        output.useTorch = me.ShouldUseTorch;
        output.speed = 0f;

        // No look position (cleared mid-look) or our window elapsed — clear
        // the investigation before returning so the default behavior's
        // HasInvestigation transition can't bounce us straight back in.
        if (!me.investigation.HasValue || time >= _lookUntilMs)
        {
            output.resetInvestigation = true;
            return new BehaviorOutput(EBehaviorResult.Complete);
        }

        // Read the position fresh each tick so a follow-up yell that
        // overwrites investigation re-aims us at the new source.
        Vector3 diff = me.investigation.Value.position - me.GlobalPosition;
        Vector2 flat = new Vector2(diff.X, diff.Z);
        if (flat.LengthSquared() > 0.0001f)
        {
            output.yaw = Mathf.Atan2(flat.X, flat.Y);
        }

        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
