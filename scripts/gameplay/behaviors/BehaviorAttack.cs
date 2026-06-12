using System.Collections.Generic;
using Godot;

public partial class BehaviorAttack : BehaviorBase
{
    private readonly AttackBehaviorData _data;
    private ulong _weaponCooldownUntilMs;
    // Separate cooldown for the optional secondary attack so a long-cooldown
    // utility profile (e.g. battle cry) doesn't share the primary's window.
    private ulong _secondaryCooldownUntilMs;
    // The target this attack session is leasing an encircle slot against.
    // Tracked so we can release the slot when the target changes (different
    // perception target) or the behavior exits.
    private Node3D _slotTarget;
    // Reused per-tick to count same-team allies in range for the secondary
    // attack's ally-count gate. Cleared before each query.
    private readonly List<Mob> _allyScratch = new();

    public BehaviorAttack(AttackBehaviorData data)
    {
        _data = data;
    }

    // No-op cross-tick state to reset — the slot allocator on World owns
    // the standoff slot across re-entries (see also OnExit-like cleanup
    // in TryTransitions and Run when the target changes). Cooldown is
    // intentionally NOT reset: behaviors that swap out (e.g. to Investigate
    // and back) shouldn't grant a free attack on re-entry.
    public override void OnEnter(Mob me, ulong time)
    {
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            // Behavior is about to swap out — release any encircle slot we
            // were holding so a different mob can take it.
            ReleaseSlot(me);
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        output.useTorch = me.ShouldUseTorch;

        Node3D target = ResolveTarget(me, ref targetPerception, out bool canSee, out Vector3 targetPos, out Vector3 lastKnownPosition);
        if (target == null)
        {
            ReleaseSlot(me);
            return new BehaviorOutput(EBehaviorResult.Running);
        }
        // Target changed since last tick — release the old slot before we
        // request a new one against the new target.
        if (_slotTarget != null && _slotTarget != target)
        {
            ReleaseSlot(me);
        }

        // Yell once on first sighting this engagement. Mob's AIOutput
        // processing flips _simState.Yelled when the yell actually fires;
        // MobAI clears it again when perception drops so the next engagement
        // yells again.
        if (!me.yelled && canSee)
        {
            output.yell = true;
        }

        output.targetPos = targetPos;

        Vector3 diff = targetPos - me.weaponPosition;
        Vector2 dir2d = new Vector2(diff.X, diff.Z);
        float dist2d = dir2d.Length();
        if (dist2d > 0.0001f)
        {
            // yaw is atan2(x, z) to match Mob._PhysicsProcess's yaw convention.
            output.yaw = Mathf.Atan2(dir2d.X, dir2d.Y);
        }

        // In range — pick which attack to fire. The secondary wins when it's
        // off cooldown AND its ally-count gate is satisfied (battle-cry style
        // gating: don't yell if nobody's around to buff). Falls through to
        // the primary otherwise. Both gates also require canSee + maxAttackRange
        // so the goblin commits to combat distance regardless of which attack
        // resolves. The vertical gate is checked here (not on the tier's
        // requirements array) so we never even commit the attackProfile when
        // out of vertical reach — that way the cooldown isn't bumped and
        // ActionRunner's rejectEffect doesn't fire on every tick of a target
        // standing one plateau above.
        bool inVerticalRange = Mathf.Abs(diff.Y) <= _data.maxVerticalAttackRange;
        bool inRangeAndSeen = dist2d < _data.maxAttackRange && inVerticalRange && canSee;
        if (inRangeAndSeen
            && _data.secondaryAttackProfile != null
            && time >= _secondaryCooldownUntilMs
            && (_data.secondaryAttackMinAllies <= 0
                || CountAlliesInRange(me, _data.secondaryAttackAllyRange) >= _data.secondaryAttackMinAllies))
        {
            output.attackProfile = _data.secondaryAttackProfile;
            output.attackContext = new ActionContext { target = target };
            _secondaryCooldownUntilMs = time + (ulong)(_data.secondaryAttackCooldownSeconds * 1000f);
        }
        else if (inRangeAndSeen && time >= _weaponCooldownUntilMs && _data.actionProfile != null)
        {
            // In range — fire the primary. Populate the action runner request;
            // Mob's _PhysicsProcess will TryStart the profile this same tick.
            output.attackProfile = _data.actionProfile;
            output.attackContext = new ActionContext { target = target };
            _weaponCooldownUntilMs = time + (ulong)(_data.attackCooldownSeconds * 1000f);
            // Hold position at the slot for a tick after the swing — fall
            // through to the standoff path below.
        }

        // A locks-movement action owns the body for its full duration —
        // windup, dart (via ApplyMotion), strike, and recovery tail. The
        // navigation goal would be ignored by Mob._PhysicsProcess anyway
        // (which gates the path impulse on the same flag), so don't compute
        // it. The encircle slot stays leased — LeaseSlot is idempotent for
        // the same target and the body will resume against the same slot
        // when the action ends; if the mob dies mid-attack, TreeExiting
        // cleans the slot up.
        if (me.Runner != null && me.Runner.LocksMovement)
        {
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        // Standoff via encircle slot. Each mob leases one angular slot
        // around the current target; PickStandoffPoint resolves it to a
        // walkable, line-of-sight world point that the navigator paths
        // toward. Far-out mobs (outside approachRange) just head for the
        // last known position so they don't waste a slot resolution
        // when they aren't even close to the ring yet. Both paths route
        // through MobNavigator so A* steers around obstacles; allowFalling
        // lets a chase drop off a ledge the mob can't climb back up.
        if (dist2d > _data.approachRange)
        {
            me.Navigator.Goto(lastKnownPosition, allowFalling: true);
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        World world = me.World;
        EncircleSlotAllocator allocator = world?.EncircleAllocator;
        int slotIdx = allocator?.LeaseSlot(me, target, Mathf.Max(1, _data.encircleSlotCount)) ?? -1;
        _slotTarget = (slotIdx >= 0) ? target : null;

        // Slot count of 1 (or no slot available) collapses to "stand at
        // desired range on the line between mob and target" — no encircle
        // structure, just a hold-distance.
        // Encircle around the *perceived* target position (targetPos), not the
        // live target.GlobalPosition. Using the real position let a mob that
        // had lost sight keep circling exactly where the player actually is —
        // a wallhack that also looked broken, since yaw faces lastKnownPosition
        // (so the mob adjusted its surround as you moved while staring at the
        // wrong spot and never closing for an attack, which is canSee-gated).
        // Keyed off targetPos, the ring sits on the last-known spot until the
        // mob reacquires line of sight, matching where it's facing.
        Vector3 standoff;
        float standoffDistance = (_data.encircleDistance > 0f) ? _data.encircleDistance : _data.desiredAttackRange;
        if (slotIdx < 0)
        {
            float angleToTarget = Mathf.Atan2(diff.X, diff.Z);
            standoff = NavigationGoals.PickStandoffPoint(world, targetPos, standoffDistance, angleToTarget);
        }
        else
        {
            float slotAngle = EncircleSlotAllocator.SlotAngle(slotIdx, _data.encircleSlotCount);
            standoff = NavigationGoals.PickStandoffPoint(world, targetPos, standoffDistance, slotAngle);
        }
        me.Navigator.Goto(standoff, allowFalling: true);
        return new BehaviorOutput(EBehaviorResult.Running);
    }

    // Resolve who this attack is engaging. The default is the mob's player
    // perception slot — the standard "mob attacks the player" target. Subclasses
    // override this to retarget the identical approach / encircle / swing logic
    // at a different victim (BehaviorDogAttack picks the nearest triggered enemy
    // mob). Returns the target Node3D (null = no valid target, attack idles),
    // and writes whether it's currently visible (gates the swing + yell), the
    // position to face / encircle, and the last-known position used as the far-
    // approach goal.
    protected virtual Node3D ResolveTarget(Mob me, ref PerceptionState targetPerception, out bool canSee, out Vector3 targetPos, out Vector3 lastKnownPosition)
    {
        Player player = targetPerception.pawnTarget;
        canSee = targetPerception.canSee;
        lastKnownPosition = targetPerception.lastKnownPosition;
        targetPos = (canSee && player != null) ? player.GlobalPosition : targetPerception.lastKnownPosition;
        return player;
    }

    private void ReleaseSlot(Mob me)
    {
        if (_slotTarget == null)
        {
            return;
        }
        me.World?.EncircleAllocator?.ReleaseSlot(me);
        _slotTarget = null;
    }

    // Counts same-team Mobs (including `me`) within `radius` of me, via the
    // mob spatial hash for cheap nearest-neighbor queries. Used by the
    // secondary-attack ally gate so a battle cry only fires when it has
    // someone to buff. radius <= 0 short-circuits to 0.
    private int CountAlliesInRange(Mob me, float radius)
    {
        if (radius <= 0f || me.mobData == null)
        {
            return 0;
        }
        MobSpatialHash hash = me.World?.MobSpatialHash;
        if (hash == null)
        {
            return 0;
        }
        _allyScratch.Clear();
        hash.QueryRadius(me.GlobalPosition, radius, _allyScratch);
        ETeam team = me.mobData.team;
        int count = 0;
        // The crier counts itself — a goblin alone still has someone to buff
        // (itself) when minAllies = 1 is authored. Authors who don't want
        // self-only cries should set minAllies = 2.
        if (me.alive)
        {
            count++;
        }
        for (int i = 0; i < _allyScratch.Count; i++)
        {
            Mob m = _allyScratch[i];
            if (m == null || m == me || !m.alive || m.mobData == null)
            {
                continue;
            }
            if (m.mobData.team == team)
            {
                count++;
            }
        }
        _allyScratch.Clear();
        return count;
    }
}
