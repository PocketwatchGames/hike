using Godot;

// Fires when there's a pending investigation that is NOT look-only — i.e. a
// stimulus the mob should walk over and inspect (an ally's alarm), as opposed
// to merely glance at (a cross-team alarm). Pair it on the Investigate
// transition; pair plain HasInvestigationCondition on the LookAt transition
// (ordered after) so look-only stimuli fall through to a glance.
[GlobalClass]
public partial class HasActionableInvestigationCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return me.investigation.HasValue && !me.investigation.Value.lookOnly;
    }
}
