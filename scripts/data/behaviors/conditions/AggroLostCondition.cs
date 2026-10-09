using Godot;

// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class AggroLostCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        // Combat ends only when neither tracked enemy is engaged: the player
        // perception slot has cleared AND no threat-scanned enemy (the player's
        // companion, for a hostile mob) is still triggered. The threat term is
        // always false for mobs that don't scan threats, so their disengage
        // behavior is unchanged.
        return targetPerception.pawnTarget == null && !me.ThreatTriggered;
    }
}
