using System.Collections.Generic;
using Godot;

// Spreads the mobs engaging a target evenly around it so a swarm fans out
// instead of stacking. A target's ring is just the set of mobs currently
// engaging it — no fixed slots and no capacity, so there is nothing to run out
// of. On every membership change the members are sorted by angle and respaced
// evenly, rotated to the fit that moves them least: each keeps its neighbours
// and nobody is sent through the target.
//
// When the even gap at a member's radius would fall below its minSpacing, the
// overflow goes onto a ring `overflowGap` further out (a higher tier). Inner
// tiers fill first, incumbents before newcomers, so an outer mob moves in as
// soon as an inner one leaves.
//
// Angles are world-absolute around the target (atan2(x,z), matching Mob yaw);
// the caller resolves them against the target's position every tick.
public class EncircleAllocator
{
    private class Member
    {
        public Mob mob;
        // The assigned angle, and the anchor this member is fitted from on the
        // next respace (a newcomer's is its current bearing from the target).
        public float angle;
        public float radius;
        public float minSpacing;
        public float overflowGap;
        // -1 until first placed, so newcomers fill tiers after incumbents.
        public int tier = -1;
        public ulong joinSeq;
    }

    private class Ring
    {
        public readonly List<Member> members = new();
        public bool dirty;
    }

    private readonly Dictionary<Node3D, Ring> _rings = new();
    private readonly Dictionary<Mob, (Node3D target, Member member)> _byMob = new();
    private readonly List<Member> _order = new();
    private readonly List<Member> _tier = new();
    private ulong _nextJoinSeq;

    // Join (or stay on) `target`'s ring and read back this mob's place on it.
    // Idempotent per tick; switching targets leaves the old ring. radius is
    // the mob's tier-0 hold distance, and is refreshed every call.
    public void Join(Mob mob, Node3D target, float radius, float minSpacing, float overflowGap, out float angle, out int tier)
    {
        Member member = null;
        if (_byMob.TryGetValue(mob, out var current))
        {
            if (current.target == target)
            {
                member = current.member;
            }
            else
            {
                Leave(mob);
            }
        }

        if (!_rings.TryGetValue(target, out Ring ring))
        {
            ring = new Ring();
            _rings[target] = ring;
        }

        if (member == null)
        {
            Vector3 toMob = mob.GlobalPosition - target.GlobalPosition;
            member = new Member
            {
                mob = mob,
                angle = (toMob.X * toMob.X + toMob.Z * toMob.Z > 0.0001f) ? Mathf.Atan2(toMob.X, toMob.Z) : 0f,
                joinSeq = _nextJoinSeq++,
            };
            ring.members.Add(member);
            _byMob[mob] = (target, member);
            ring.dirty = true;
        }
        member.radius = radius;
        member.minSpacing = minSpacing;
        member.overflowGap = overflowGap;

        if (ring.dirty)
        {
            Respace(ring);
        }
        angle = member.angle;
        tier = member.tier;
    }

    // Idempotent — safe from both the behavior (on exit / target change) and
    // the mob (on death / despawn) without coordination.
    public void Leave(Mob mob)
    {
        if (mob == null || !_byMob.TryGetValue(mob, out var current))
        {
            return;
        }
        _byMob.Remove(mob);
        if (_rings.TryGetValue(current.target, out Ring ring))
        {
            ring.members.Remove(current.member);
            if (ring.members.Count == 0)
            {
                _rings.Remove(current.target);
            }
            else
            {
                ring.dirty = true;
            }
        }
    }

    private void Respace(Ring ring)
    {
        ring.dirty = false;

        // Fill order: placed members by tier (inner first), then newcomers, each
        // by join order — so a vacancy pulls the longest-waiting outer mob in.
        _order.Clear();
        _order.AddRange(ring.members);
        _order.Sort((a, b) =>
        {
            int ta = a.tier < 0 ? int.MaxValue : a.tier;
            int tb = b.tier < 0 ? int.MaxValue : b.tier;
            return ta != tb ? ta.CompareTo(tb) : a.joinSeq.CompareTo(b.joinSeq);
        });

        int tierIdx = 0;
        while (_order.Count > 0)
        {
            _tier.Clear();
            for (int i = 0; i < _order.Count; i++)
            {
                _tier.Add(_order[i]);
                // The first member of a tier always fits, so the loop terminates.
                if (_tier.Count > 1 && !TierFits(_tier, tierIdx))
                {
                    _tier.RemoveAt(_tier.Count - 1);
                }
            }
            for (int i = 0; i < _tier.Count; i++)
            {
                _tier[i].tier = tierIdx;
                _order.Remove(_tier[i]);
            }
            SpreadEvenly(_tier);
            tierIdx++;
        }
    }

    // Every member of the tier gets at least its own minSpacing of arc at its
    // own radius on that tier.
    private static bool TierFits(List<Member> tier, int tierIdx)
    {
        float step = Mathf.Tau / tier.Count;
        for (int i = 0; i < tier.Count; i++)
        {
            Member m = tier[i];
            if (step * (m.radius + tierIdx * m.overflowGap) < m.minSpacing)
            {
                return false;
            }
        }
        return true;
    }

    // Even spacing in angular order, rotated by the circular mean of each
    // member's offset from its even position — the rotation that moves the
    // group least.
    private static void SpreadEvenly(List<Member> tier)
    {
        tier.Sort((a, b) => a.angle.CompareTo(b.angle));
        float step = Mathf.Tau / tier.Count;
        float sumSin = 0f;
        float sumCos = 0f;
        for (int i = 0; i < tier.Count; i++)
        {
            float offset = tier[i].angle - i * step;
            sumSin += Mathf.Sin(offset);
            sumCos += Mathf.Cos(offset);
        }
        float rotation = Mathf.Atan2(sumSin, sumCos);
        for (int i = 0; i < tier.Count; i++)
        {
            tier[i].angle = Mathf.Wrap(rotation + i * step, -Mathf.Pi, Mathf.Pi);
        }
    }
}
