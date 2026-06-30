using System;
using Godot;

// A one-shot sidestep/back-hop out of an incoming projectile's path. Entered
// from the attack/encircle state (via IncomingProjectileCondition) while the mob
// is between swings and facing the player. On its first tick it picks the best
// evade direction — sideways or backward, never forward into the shot, and only
// to a spot that's valid ground — and drives a facing-independent dash there
// (Mob.ApplyDodge). The mob keeps facing the player throughout, takes NO
// invulnerability (the dash is positional evasion, not an i-frame), then hands
// control back to the attack state when the dash window ends.
public partial class BehaviorDodge : BehaviorBase
{
    private readonly DodgeBehaviorData _data;
    private bool _started;
    private ulong _endMs;

    public BehaviorDodge(DodgeBehaviorData data)
    {
        _data = data;
    }

    public override void OnEnter(Mob me, ulong time)
    {
        _started = false;
        _endMs = 0;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (!_started)
        {
            _started = true;
            _endMs = time + (ulong)(_data.dashDurationSeconds * 1000f);
            // Arm the reaction cooldown whether or not a direction works out, so
            // a mob boxed in on all sides doesn't re-enter the dodge every tick.
            me.ReactionReadyMs = time + (ulong)(_data.reactionCooldownSeconds * 1000f);

            Vector3 dir = ChooseDodgeDir(me, ref targetPerception);
            if (dir != Vector3.Zero)
            {
                float speed = _data.dashDistance / Mathf.Max(0.01f, _data.dashDurationSeconds);
                me.ApplyDodge(dir, speed, _data.dashDurationSeconds);
                output.dash = true;
            }
        }

        // Keep facing the player while sliding — the dash must not reorient the
        // body along its travel direction.
        FaceTarget(me, ref targetPerception, ref output);

        if (time >= _endMs)
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, _data.resumeBehavior);
        }
        return new BehaviorOutput(EBehaviorResult.Running);
    }

    // Pick a sideways or backward direction (relative to facing the target) that
    // both moves off the incoming shot's line and lands on valid ground. Returns
    // Vector3.Zero when no candidate is standable (the mob is boxed in and just
    // eats the shot). Scores by how perpendicular the move is to the shot's
    // velocity — so a sidestep that fully clears the line beats a back-hop that
    // merely retreats along it.
    private Vector3 ChooseDodgeDir(Mob me, ref PerceptionState targetPerception)
    {
        Vector3 targetPos = TargetPos(me, ref targetPerception);
        Vector3 forward = targetPos - me.GlobalPosition;
        forward.Y = 0f;
        if (forward.LengthSquared() < 0.0001f)
        {
            return Vector3.Zero;
        }
        forward = forward.Normalized();
        // Horizontal perpendicular (the mob's right). Candidates: strafe both
        // ways and hop straight back — never forward into the shot.
        Vector3 right = new Vector3(forward.Z, 0f, -forward.X);
        Span<Vector3> candidates = stackalloc Vector3[3] { right, -right, -forward };

        // The shot we're evading — its velocity gives the line to clear. May be
        // null if it has already passed in the tick since the transition fired;
        // fall back to "anything sideways beats backward" via a null shot dir.
        Projectile threat = me.World?.Projectiles?.FindIncoming(
            me.GlobalPosition, me.mobData.clearanceRadius + _data.dashDistance, me.ActorTeam, _data.threatLeadTime);
        Vector3 shotDir = Vector3.Zero;
        if (threat != null)
        {
            Vector3 v = threat.Velocity;
            v.Y = 0f;
            if (v.LengthSquared() > 0.0001f)
            {
                shotDir = v.Normalized();
            }
        }

        Vector3 best = Vector3.Zero;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < candidates.Length; i++)
        {
            Vector3 dir = candidates[i];
            Vector3 landing = me.GlobalPosition + dir * _data.dashDistance;
            if (!NavigationGoals.IsGroundStandable(me.World, me.Navigator.Profile, landing, out _))
            {
                continue;
            }
            // Perpendicular-to-shot component (1 = fully clears the line, 0 =
            // moves straight along it). With no known shot, prefer strafes (the
            // first two candidates) over the back-hop via a small index bias.
            float score = shotDir != Vector3.Zero
                ? (dir - dir.Dot(shotDir) * shotDir).Length()
                : (i < 2 ? 1f : 0.5f);
            if (score > bestScore)
            {
                bestScore = score;
                best = dir;
            }
        }
        return best;
    }

    private static Vector3 TargetPos(Mob me, ref PerceptionState targetPerception)
    {
        Node3D target = targetPerception.pawnTarget;
        return target != null ? target.GlobalPosition : targetPerception.lastKnownPosition;
    }

    private static void FaceTarget(Mob me, ref PerceptionState targetPerception, ref AIOutput output)
    {
        Vector3 to = TargetPos(me, ref targetPerception) - me.GlobalPosition;
        to.Y = 0f;
        if (to.LengthSquared() > 0.0001f)
        {
            output.yaw = Mathf.Atan2(to.X, to.Z);
        }
    }
}
