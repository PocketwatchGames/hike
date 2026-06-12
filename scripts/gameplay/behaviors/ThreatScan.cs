using System.Collections.Generic;
using Godot;

// Shared companion target acquisition: the nearest alive, triggered mob on
// `enemyTeam` within `range` (XZ) of `me` that `me` has a clear line of sight
// to. "Perception" here is deliberately lightweight — sight range plus an
// unobstructed ray — rather than the full accumulating MobAI perception model,
// which only ever tracks the player. Both the brain transition
// (ThreatPerceivedCondition) and the attack behavior (BehaviorDogAttack) call
// this so acquisition and pursuit always agree on the target.
public static class ThreatScan
{
    // Eye / nose height the line-of-sight ray is cast from and to, matching the
    // mob-vision ray in MobAI.UpdatePerception.
    private const float EyeHeight = 1.5f;

    // Reused across calls — ThreatScan only runs on the physics thread, so a
    // single shared scratch list is safe and keeps the scan allocation-free.
    private static readonly List<Mob> _scratch = new();

    public static Mob FindNearest(Mob me, ETeam enemyTeam, float range)
    {
        if (me == null || range <= 0f)
        {
            return null;
        }
        MobSpatialHash hash = me.World?.MobSpatialHash;
        if (hash == null)
        {
            return null;
        }

        _scratch.Clear();
        hash.QueryRadius(me.GlobalPosition, range, _scratch, exclude: me);

        Vector3 origin = me.GlobalPosition + Vector3.Up * EyeHeight;
        PhysicsDirectSpaceState3D space = me.GetWorld3D().DirectSpaceState;
        Mob best = null;
        float bestDistSq = float.MaxValue;
        for (int i = 0; i < _scratch.Count; i++)
        {
            Mob candidate = _scratch[i];
            if (candidate == null || !candidate.alive || candidate.mobData == null)
            {
                continue;
            }
            if (candidate.ActorTeam != enemyTeam || !candidate.IsTriggered)
            {
                continue;
            }
            float distSq = me.GlobalPosition.DistanceSquaredTo(candidate.GlobalPosition);
            if (distSq >= bestDistSq)
            {
                continue;
            }
            // Line of sight — blocked by solid world geometry / props (same mask
            // the mob-vision raycast uses). Only ray-test candidates that could
            // still win (closer than the current best) so the scan stays cheap.
            Vector3 target = candidate.GlobalPosition + Vector3.Up * EyeHeight;
            using PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, target, (uint)ECollisionLayer.Solid);
            query.CollideWithAreas = false;
            query.CollideWithBodies = true;
            if (space.IntersectRay(query).Count > 0)
            {
                continue;
            }
            best = candidate;
            bestDistSq = distSq;
        }
        _scratch.Clear();
        return best;
    }
}
