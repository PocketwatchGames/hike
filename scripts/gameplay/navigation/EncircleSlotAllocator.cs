using System.Collections.Generic;
using Godot;

// Hands out angular standoff slots around a target so a swarm of mobs
// fans out instead of stacking on the target's face. Each (target) has
// a ring of N slots at fixed angles around it; a Mob that wants to
// position itself for combat leases a slot, gets back an angle index,
// and resolves a world position from that angle every tick (the target
// is moving, so the resolved point is recomputed continuously).
//
// One allocator per World (sibling to MobSpatialHash). Slots survive
// across repaths and across behavior re-entries — only Released
// explicitly when the mob loses interest, dies, or switches targets.
//
// Design notes:
// - Slot count is a property of the FIRST lease for a target. Mixed
//   slot-count requests against the same target are rare in practice
//   (one mob type per swarm) and silently use the existing ring.
// - Slot picking is greedy-by-angle: the requesting mob gets the free
//   slot whose angle is closest to the mob's current angle around the
//   target, so a mob already on the target's left side keeps its
//   left-side slot rather than running around to a stale "next free"
//   index. Result is a stable encircle pattern that doesn't churn.
public class EncircleSlotAllocator
{
    private class Ring
    {
        public int slotCount;
        public Mob[] slots;
    }

    private readonly Dictionary<Node3D, Ring> _rings = new();
    // Mob → (target, slot) so Release can find the lessee's ring without
    // a target-keyed lookup at the call site.
    private readonly Dictionary<Mob, (Node3D target, int slot)> _leases = new();

    // Lease (or refresh) a slot for `mob` around `target` with the given
    // slot count. Returns the slot index in [0, slotCount); -1 if the
    // ring is full (slotCount mobs already chasing this target).
    public int LeaseSlot(Mob mob, Node3D target, int slotCount)
    {
        if (mob == null || target == null || slotCount <= 0)
        {
            return -1;
        }
        // If already leased: return existing if it's the same target,
        // otherwise release and re-lease.
        if (_leases.TryGetValue(mob, out var current))
        {
            if (current.target == target)
            {
                return current.slot;
            }
            ReleaseSlot(mob);
        }

        if (!_rings.TryGetValue(target, out Ring ring))
        {
            ring = new Ring { slotCount = slotCount, slots = new Mob[slotCount] };
            _rings[target] = ring;
        }

        Vector3 toMob = mob.GlobalPosition - target.GlobalPosition;
        toMob.Y = 0f;
        float mobAngle = (toMob.LengthSquared() > 0.0001f)
            ? Mathf.Atan2(toMob.X, toMob.Z)
            : 0f;

        int best = -1;
        float bestDelta = float.MaxValue;
        for (int i = 0; i < ring.slotCount; i++)
        {
            if (ring.slots[i] != null)
            {
                continue;
            }
            float slotAngle = SlotAngle(i, ring.slotCount);
            float delta = Mathf.Abs(Mathf.Wrap(slotAngle - mobAngle, -Mathf.Pi, Mathf.Pi));
            if (delta < bestDelta)
            {
                best = i;
                bestDelta = delta;
            }
        }
        if (best < 0)
        {
            return -1;
        }
        ring.slots[best] = mob;
        _leases[mob] = (target, best);
        return best;
    }

    // Free the slot held by `mob`, if any. Idempotent — safe to call from
    // both the behavior side (on transition out) and the mob side (on
    // death / despawn) without coordination.
    public void ReleaseSlot(Mob mob)
    {
        if (mob == null)
        {
            return;
        }
        if (!_leases.TryGetValue(mob, out var current))
        {
            return;
        }
        if (_rings.TryGetValue(current.target, out Ring ring) && current.slot < ring.slots.Length)
        {
            ring.slots[current.slot] = null;
            bool anyTaken = false;
            for (int i = 0; i < ring.slots.Length; i++)
            {
                if (ring.slots[i] != null)
                {
                    anyTaken = true;
                    break;
                }
            }
            if (!anyTaken)
            {
                _rings.Remove(current.target);
            }
        }
        _leases.Remove(mob);
    }

    // World angle (radians, atan2(x,z) convention to match Mob yaw) for a
    // given slot. Slot 0 is +Z (north); slots increase clockwise.
    public static float SlotAngle(int slot, int slotCount)
    {
        return slot * Mathf.Tau / slotCount;
    }
}
