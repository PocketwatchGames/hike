using Godot;

// Fires when a hostile projectile is on a path to pass close to the mob soon
// (ProjectileRegistry.FindIncoming). Drives the reaction transitions: an
// encircling attacker dodging an arrow (Attack -> Dodge) and a perched bird
// bolting to a new perch when shot at (Perch -> FlyFlee).
//
// The two use-sites differ only in their gates, all authored here:
//   - requireTriggered: the attacker must already be in combat (default);
//     the skittish perched bird leaves this off so a surprise shot still works.
//   - requireFacingTarget: the attacker may only react to shots it could see
//     coming — it must be facing the player within facingToleranceDegrees; the
//     perched bird (omnidirectional lookout) leaves this off.
// A per-mob reaction cooldown (Mob.ReactionReadyMs, set by the reacting
// behavior) keeps a mob from chaining reactions every tick while shots keep
// arriving.
[GlobalClass]
public partial class IncomingProjectileCondition : BehaviorTransitionData
{
    // Seconds of look-ahead along each projectile's path. Longer = the mob
    // reacts to shots further out (more cautious / twitchy); shorter = it only
    // bolts at the last moment.
    [Export] public float leadTime = 0.6f;
    // Added to the mob's clearanceRadius to form the "this shot would hit me"
    // miss-distance threshold. A little slack so a near-grazing shot still
    // provokes a reaction.
    [Export] public float detectRadiusBonus = 0.75f;
    // The mob must already be combat-triggered against the target to react.
    [Export] public bool requireTriggered = true;
    // The mob must be facing the perception target (within facingToleranceDegrees)
    // to react — it dodges shots it sees coming, not ones from behind.
    [Export] public bool requireFacingTarget = false;
    [Export(PropertyHint.Range, "10,180,5")] public float facingToleranceDegrees = 70f;

    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        World world = me.World;
        if (world == null || me.mobData == null)
        {
            return false;
        }
        // Never interrupt an in-progress attack action (windup/strike/recovery).
        // This is the "between swings, not mid-swing" gate — the mob reacts while
        // circling on cooldown / in the post-cooldown pause, when its runner is
        // idle. Harmless for non-attacking reactors (a perched bird's runner is
        // never busy).
        if (me.Runner != null && me.Runner.IsBusy)
        {
            return false;
        }
        // Reaction cooldown — set by the behavior we transition into so the mob
        // can't re-dodge on the very next tick while shots keep coming.
        if (world.GameTimeMs < me.ReactionReadyMs)
        {
            return false;
        }
        if (requireTriggered && !targetPerception.triggered)
        {
            return false;
        }
        if (requireFacingTarget && !IsFacingTarget(me, ref targetPerception))
        {
            return false;
        }
        float radius = me.mobData.clearanceRadius + detectRadiusBonus;
        return world.Projectiles.FindIncoming(me.GlobalPosition, radius, me.ActorTeam, leadTime) != null;
    }

    // True when the mob's forward axis points within facingToleranceDegrees of
    // the (horizontal) direction to its target's known position.
    private bool IsFacingTarget(Mob me, ref PerceptionState targetPerception)
    {
        Node3D target = targetPerception.pawnTarget;
        Vector3 targetPos = target != null ? target.GlobalPosition : targetPerception.lastKnownPosition;
        Vector3 toTarget = targetPos - me.GlobalPosition;
        toTarget.Y = 0f;
        if (toTarget.LengthSquared() < 0.0001f)
        {
            return true;
        }
        Vector3 forward = me.ActorForward;
        forward.Y = 0f;
        if (forward.LengthSquared() < 0.0001f)
        {
            return false;
        }
        float dot = forward.Normalized().Dot(toTarget.Normalized());
        return dot >= Mathf.Cos(Mathf.DegToRad(facingToleranceDegrees));
    }
}
