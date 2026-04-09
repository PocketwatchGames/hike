using Godot;

public partial class BehaviorIdle : BehaviorBase
{
    private readonly IdleBehaviorData _data;

    public BehaviorIdle(IdleBehaviorData data)
    {
        _data = data;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }
        output.speed = 0f;
        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
