using System.Collections.Generic;
using Godot;

// Flat list of in-flight Projectiles, used by mobs to react to incoming shots
// (the dodge / perch-flee reaction). Projectiles self-register on tree-enter and
// unregister on tree-exit, so this holds exactly the shots currently airborne —
// almost always a handful, so a linear scan per query is cheaper than any
// spatial structure. Distinct from MobSpatialHash / PerchRegistry only in that
// projectiles move every tick (no cached cell), so there's nothing to update.
public class ProjectileRegistry
{
    private readonly List<Projectile> _projectiles = new();

    public void Add(Projectile p)
    {
        if (p != null && !_projectiles.Contains(p))
        {
            _projectiles.Add(p);
        }
    }

    public void Remove(Projectile p)
    {
        if (p != null)
        {
            _projectiles.Remove(p);
        }
    }

    public void Clear()
    {
        _projectiles.Clear();
    }

    // Find the most imminent hostile projectile whose straight-line path will
    // pass within `radius` of `mobPos` within the next `leadTime` seconds, while
    // it is still approaching (heading toward the mob, not already past it).
    // "Hostile" = the projectile's firing team isn't allied with `mobTeam`, so a
    // mob never dodges its own side's shots. Returns the one with the soonest
    // closest-approach (the first to arrive); null when nothing threatens. The
    // mob is treated as stationary over the short window — fine at dodge ranges.
    public Projectile FindIncoming(Vector3 mobPos, float radius, ETeam mobTeam, float leadTime)
    {
        float radiusSq = radius * radius;
        Projectile best = null;
        float bestTime = float.MaxValue;
        for (int i = 0; i < _projectiles.Count; i++)
        {
            Projectile p = _projectiles[i];
            if (p == null || !GodotObject.IsInstanceValid(p))
            {
                continue;
            }
            if (Teams.AreAllied(p.AttackerTeam, mobTeam))
            {
                continue;
            }
            Vector3 vel = p.Velocity;
            float speedSq = vel.LengthSquared();
            if (speedSq < 0.0001f)
            {
                continue;
            }
            // p = mob position relative to the projectile; closest approach is at
            // t* = (toMob·vel) / |vel|², clamped to [0, leadTime]. Require
            // toMob·vel > 0 so only an approaching shot (one still closing the
            // gap) qualifies — a projectile already past the mob has a negative
            // dot and is ignored.
            Vector3 toMob = mobPos - p.GlobalPosition;
            float along = toMob.Dot(vel);
            if (along <= 0f)
            {
                continue;
            }
            float t = Mathf.Min(along / speedSq, leadTime);
            Vector3 closest = vel * t - toMob;
            if (closest.LengthSquared() > radiusSq)
            {
                continue;
            }
            if (t < bestTime)
            {
                bestTime = t;
                best = p;
            }
        }
        return best;
    }
}
