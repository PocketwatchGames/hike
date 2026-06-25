using System.Collections.Generic;
using Godot;

// Which candidates a scan considers by their `dangerous` flag. Threats (a
// companion's Wary/attack channel) want DangerousOnly; a companion's idle
// curiosity wants HarmlessOnly; a hostile hunting the player's (non-dangerous)
// companions wants Any.
public enum EThreatDanger
{
    Any,
    DangerousOnly,
    HarmlessOnly,
}

// Shared cross-faction target acquisition for the threat-perception channel:
// among the alive mobs within `range` of `me` on the OPPOSITE side of the
// player divide (Teams.IsPlayerSide) that `me` has a clear line of sight to, the
// one holding the most aggro (see AggroTracker) — i.e. the enemy that has dealt
// the most damage to `me` or, for a companion, to `me`'s master. Ties (notably
// the no-damage-dealt-yet case, where every candidate sits at 0 aggro) break
// toward the nearest, preserving proximity behavior until someone draws blood.
// So a player-side companion scans the hostile/wild side, and a hostile scans
// the player's companions — keyed off ActorTeam, with no per-mob faction to
// author. "Perception" here is deliberately lightweight — sight range plus an
// unobstructed ray — rather than the full accumulating MobAI perception model,
// which only ever tracks the player.
public static class ThreatScan
{
    // Eye / nose height the line-of-sight ray is cast from and to, matching the
    // mob-vision ray in MobAI.UpdatePerception.
    private const float EyeHeight = 1.5f;

    // Reused across calls — ThreatScan only runs on the physics thread, so a
    // single shared scratch list is safe and keeps the scan allocation-free.
    private static readonly List<Mob> _scratch = new();

    // requireTriggered (default) limits candidates to enemies already in combat
    // (IsTriggered) — so a hostile only engages a companion once it's fighting,
    // not while it idles harmlessly. Pass false for a guard companion that should
    // also become aware of (and bark at) an enemy it merely sees; the brain still
    // gates whether that awareness escalates to an attack.
    public static Mob FindNearest(Mob me, float range, bool requireTriggered = true, EThreatDanger danger = EThreatDanger.Any)
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

        bool mePlayerSide = Teams.IsPlayerSide(me.ActorTeam);
        Vector3 origin = me.GlobalPosition + Vector3.Up * EyeHeight;
        PhysicsDirectSpaceState3D space = me.GetWorld3D().DirectSpaceState;
        Mob best = null;
        float bestAggro = 0f;
        float bestDistSq = float.MaxValue;
        for (int i = 0; i < _scratch.Count; i++)
        {
            Mob candidate = _scratch[i];
            if (candidate == null || !candidate.alive || candidate.mobData == null)
            {
                continue;
            }
            if (Teams.IsPlayerSide(candidate.ActorTeam) == mePlayerSide || (requireTriggered && !candidate.IsTriggered))
            {
                continue;
            }
            if (danger == EThreatDanger.DangerousOnly && !candidate.mobData.dangerous)
            {
                continue;
            }
            if (danger == EThreatDanger.HarmlessOnly && candidate.mobData.dangerous)
            {
                continue;
            }
            // Rank by aggro, nearest as the tiebreak. Skip any candidate that
            // can't beat the current best on (aggro desc, distance asc) — this
            // also keeps the LOS raycast bounded to candidates that could win.
            float aggro = me.GetAggro(candidate);
            float distSq = me.GlobalPosition.DistanceSquaredTo(candidate.GlobalPosition);
            if (best != null)
            {
                bool wins = aggro > bestAggro || (aggro == bestAggro && distSq < bestDistSq);
                if (!wins)
                {
                    continue;
                }
            }
            // Line of sight — blocked by solid world geometry / props (same mask
            // the mob-vision raycast uses).
            Vector3 target = candidate.GlobalPosition + Vector3.Up * EyeHeight;
            using PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, target, (uint)ECollisionLayer.Solid);
            query.CollideWithAreas = false;
            query.CollideWithBodies = true;
            if (space.IntersectRay(query).Count > 0)
            {
                continue;
            }
            best = candidate;
            bestAggro = aggro;
            bestDistSq = distSq;
        }
        _scratch.Clear();
        return best;
    }
}
