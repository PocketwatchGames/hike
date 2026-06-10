using Godot;

// Companion behavior: trail the master (the player). When farther than
// followDistance, path toward them; once within stopDistance, hold and face
// them. Transitions (e.g. to Stay) are evaluated first so a command toggle
// takes effect immediately.
public partial class BehaviorFollow : BehaviorBase
{
    private readonly FollowBehaviorData _data;

    public BehaviorFollow(FollowBehaviorData data)
    {
        _data = data;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        Player master = me.World?.player;
        if (master == null)
        {
            output.speed = 0f;
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        Vector3 toMaster = master.GlobalPosition - me.GlobalPosition;
        toMaster.Y = 0f;
        if (toMaster.LengthSquared() > _data.followDistance * _data.followDistance)
        {
            output.pathTarget = master.GlobalPosition;
            output.speed = _data.followSpeed;
            output.pathSuccessDistance = _data.stopDistance;
        }
        else
        {
            output.speed = 0f;
            if (toMaster.LengthSquared() > 0.0001f)
            {
                output.yaw = Mathf.Atan2(toMaster.X, toMaster.Z);
            }
        }
        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
