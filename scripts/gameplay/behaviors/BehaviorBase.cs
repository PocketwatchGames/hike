using Godot;

public partial class BehaviorBase
{
    protected BehaviorNode behaviorNode { get; private set; }

    public void Init(BehaviorNode node)
    {
        behaviorNode = node;
    }

    // Called by Mob.StartBehavior whenever this behavior becomes the current
    // behavior — both the first time it runs and on every later re-entry
    // after another behavior had control. The default is a no-op; behaviors
    // that hold cross-tick state (timers, "have I picked a target yet"
    // flags, navigator intent) should reset that state here so re-entry
    // doesn't pick up stale values from the previous time the behavior ran.
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
