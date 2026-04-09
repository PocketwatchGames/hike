using Godot;

public partial class BehaviorAttack : BehaviorBase
{
    private readonly AttackBehaviorData _data;

    public BehaviorAttack(AttackBehaviorData data)
    {
        _data = data;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }
        output.pathTarget = targetPerception.lastKnownPosition;
        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
