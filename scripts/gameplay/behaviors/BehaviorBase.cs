using Godot;

public partial class BehaviorBase
{
    protected BehaviorNode behaviorNode { get; private set; }
    // The node's transitions minus any into a node this mob's abilities pruned.
    // A managed copy, so the per-tick walk never touches the Godot array.
    private BehaviorNodeTransition[] _transitions = System.Array.Empty<BehaviorNodeTransition>();

    // Authored resting stance for this node, seeded into AIOutput.behaviorFlags
    // each tick before Run so a behavior can compose extra bits on top of it.
    public EBehaviorFlags BaseFlags => behaviorNode?.data?.behaviorFlags ?? EBehaviorFlags.None;

    public void Init(BehaviorNode node, BehaviorNodeTransition[] transitions)
    {
        behaviorNode = node;
        _transitions = transitions;
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

    // debug_mob_behavior: this behavior's own cross-tick state, or null when it
    // keeps none worth showing.
    public virtual string DebugStatus(ulong time)
    {
        return null;
    }

    protected bool TryTransitions(Mob me, ulong time, ref PerceptionState targetPerception, out StringName destination)
    {
        foreach (BehaviorNodeTransition t in _transitions)
        {
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
