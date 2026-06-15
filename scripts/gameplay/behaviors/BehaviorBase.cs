using Godot;

public partial class BehaviorBase
{
    protected BehaviorNode behaviorNode { get; private set; }

    public void Init(BehaviorNode node)
    {
        behaviorNode = node;
    }

    // Called whenever this behavior becomes current — both first run and every
    // re-entry after another behavior had control. Behaviors holding cross-tick
    // state (timers, target flags, navigator intent) must reset it here so
    // re-entry doesn't pick up stale values from the previous run.
    public virtual void OnEnter(Mob me, ulong time)
    {
    }

    public virtual BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        return new BehaviorOutput(EBehaviorResult.Complete);
    }

    protected bool TryTransitions(Mob me, ulong time, ref PerceptionState targetPerception, out StringName destination)
    {
        foreach (BehaviorNodeTransition t in behaviorNode.transitions)
        {
            if (t == null)
            {
                continue;
            }
            if (t.condition != null && t.condition.Evaluate(me, ref targetPerception))
            {
                destination = t.destination;
                return true;
            }
        }
        destination = default;
        return false;
    }
}
