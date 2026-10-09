using Godot;

// Fires when the mob has noticed a dead body it has not finished reacting to.
// Pair it on the edge into the corpse-inspect node, ordered BELOW the aggro and
// alarm edges — a fight or a yell outranks a body, and the sighting keeps until
// the mob gets around to it.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class HasCorpseSightingCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return me.CorpseSighting.HasValue;
    }
}
