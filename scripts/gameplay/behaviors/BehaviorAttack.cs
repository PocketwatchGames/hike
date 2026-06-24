using System.Collections.Generic;
using Godot;

public partial class BehaviorAttack : BehaviorBase
{
    private readonly AttackBehaviorData _data;
    // Per-weapon cooldown deadlines (game-time ms). Each of the mob's weapons
    // fires on its own WeaponData.cooldownSeconds cadence, so a long-cooldown cry
    // doesn't share the always-available basic attack's window.
    private readonly Dictionary<WeaponData, ulong> _weaponCooldownUntilMs = new();
    // Standoff fallback (meters) when neither encircleDistance nor any weapon
    // authors a desired range — mirrors the old AttackBehaviorData default.
    private const float DefaultStandoffDistance = 1.75f;
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

    // Cooldown is intentionally NOT reset here: behaviors that swap out (e.g.
    // to Investigate and back) shouldn't grant a free attack on re-entry. The
    // encircle slot is released on exit/target-change, not on enter.
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


        Node3D target = ResolveTarget(me, ref targetPerception, out bool canSee, out Vector3 targetPos, out Vector3 lastKnownPosition);
        if (target == null)
        {
            ReleaseSlot(me);
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        // We have a target and are engaging it — this tick counts as combat for
        // the player-facing CombatTracker (gated downstream by the mob being
        // dangerous and player-perceived). Set after the transition / no-target
        // early-outs so leaving the attack state doesn't read as combat.
        output.combatBehavior = true;
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

        // Bring a weapon to bear. `ready` is the highest-priority weapon that's
        // off its own cooldown, currently seen, ally-gated, and within vertical
        // reach — picked IGNORING 2D distance so that out of range we CLOSE to it
        // (standoff below) instead of stalling on the encircle ring. A gated
        // special (a battle cry: long cooldown, minAllies > 0, higher priority)
        // wins when its conditions hold and otherwise falls through to the
        // always-available basic attack. The vertical gate lives in
        // ChooseReadyWeapon (not on the tier's requirements) so we never commit
        // the profile out of vertical reach — the cooldown isn't bumped and
        // ActionRunner's rejectEffect doesn't fire every tick against a target one
        // plateau up. Fire it the instant we're inside its MaxAttackRange.
        WeaponData ready = ChooseReadyWeapon(me, time, diff.Y, canSee);
        if (ready != null && dist2d < ready.MaxAttackRange)
        {
            // Mob's _PhysicsProcess will TryStart the profile this same tick.
            output.attackProfile = ready.actionProfile;
            output.attackContext = new ActionContext { target = target, primaryItem = me.GetWeapon(ready) };
            _weaponCooldownUntilMs[ready] = time + (ulong)(ready.cooldownSeconds * 1000f);
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
            // avoidHazards: false — a mob committed to the player ignores fire
            // traps / campfires / spike traps so the player can lure it in.
            me.Navigator.Goto(lastKnownPosition, allowFalling: true, avoidHazards: false);
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
        // Standoff distance splits by intent. When a weapon is ready we close to
        // its desiredAttackRange (which sits inside MaxAttackRange) so the swing
        // actually lands; with everything on cooldown we fall back to the
        // encircle ring to spread out and wait between swings. This split is why
        // encircleDistance may sit at — or beyond — MaxAttackRange without
        // freezing the mob: the attack approach never holds at the ring.
        Vector3 standoff;
        float standoffDistance = (ready != null)
            ? ready.desiredAttackRange
            : ((_data.encircleDistance > 0f) ? _data.encircleDistance : ClosestDesiredRange(me));
        if (slotIdx < 0)
        {
            float angleToTarget = Mathf.Atan2(diff.X, diff.Z);
            standoff = NavigationGoals.PickStandoffPoint(world, me.mobData.verticalClearance, targetPos, standoffDistance, angleToTarget);
        }
        else
        {
            float slotAngle = EncircleSlotAllocator.SlotAngle(slotIdx, _data.encircleSlotCount);
            standoff = NavigationGoals.PickStandoffPoint(world, me.mobData.verticalClearance, targetPos, standoffDistance, slotAngle);
        }
        me.Navigator.Goto(standoff, allowFalling: true, avoidHazards: false);
        return new BehaviorOutput(EBehaviorResult.Running);
    }

    // Resolve who this attack is engaging. The mob weighs the two enemies it can
    // track — the player (its perception slot) and the companion it's scanning
    // for via ThreatPerception — and commits to whichever holds the most aggro
    // (who has hurt it most), defaulting to the player when aggro ties (e.g.
    // before anyone has drawn blood). A mob that doesn't scan threats has no
    // companion candidate and so always picks the player, unchanged. Subclasses
    // override this entirely to retarget the identical approach / encircle /
    // swing logic at a different victim (BehaviorDogAttack picks the highest-
    // aggro triggered enemy mob). Returns the target Node3D (null = no valid
    // target, attack idles), and writes whether it's currently visible (gates
    // the swing + yell), the position to face / encircle, and the last-known
    // position used as the far-approach goal.
    protected virtual Node3D ResolveTarget(Mob me, ref PerceptionState targetPerception, out bool canSee, out Vector3 targetPos, out Vector3 lastKnownPosition)
    {
        Player player = targetPerception.pawnTarget;
        // The companion is only a candidate once threat perception has latched
        // it (so the mob doesn't peel off to chase a dog it can't actually see).
        Mob threat = me.ThreatTriggered ? me.ThreatTarget : null;
        bool preferThreat = threat != null
            && (player == null || me.GetAggro(threat) > me.GetAggro(player));
        if (preferThreat)
        {
            canSee = me.ThreatCanSee;
            lastKnownPosition = me.ThreatLastKnownPosition;
            targetPos = canSee ? threat.GlobalPosition : lastKnownPosition;
            return threat;
        }
        canSee = targetPerception.canSee;
        lastKnownPosition = targetPerception.lastKnownPosition;
        targetPos = (canSee && player != null) ? player.GlobalPosition : targetPerception.lastKnownPosition;
        return player;
    }

    // The highest-priority of the mob's weapons (WeaponData.priority) the mob is
    // ready to commit this tick: a runnable profile, currently seen, off its
    // per-weapon cooldown, within vertical reach, and with enough same-team
    // allies nearby. 2D distance is deliberately NOT gated here — the caller
    // closes to the weapon's desiredAttackRange and fires once inside its
    // MaxAttackRange — so a mob whose encircle ring sits outside attack range
    // still approaches and swings. Ties break toward the earlier weapon in the
    // list. Returns null when nothing qualifies (all on cooldown, out of
    // vertical reach, can't see, or the mob has no weapons), in which case the
    // mob holds at the encircle ring.
    private WeaponData ChooseReadyWeapon(Mob me, ulong time, float diffY, bool canSee)
    {
        if (!canSee)
        {
            return null;
        }
        Godot.Collections.Array<WeaponData> weapons = me.Weapons;
        if (weapons == null)
        {
            return null;
        }
        WeaponData chosen = null;
        for (int i = 0; i < weapons.Count; i++)
        {
            WeaponData w = weapons[i];
            if (w == null || w.actionProfile == null)
            {
                continue;
            }
            if (chosen != null && w.priority <= chosen.priority)
            {
                continue;
            }
            if (Mathf.Abs(diffY) > w.MaxVerticalAttackRange)
            {
                continue;
            }
            if (_weaponCooldownUntilMs.TryGetValue(w, out ulong until) && time < until)
            {
                continue;
            }
            if (w.minAllies > 0 && CountAlliesInRange(me, w.allyRange) < w.minAllies)
            {
                continue;
            }
            chosen = w;
        }
        return chosen;
    }

    // Standoff fallback when encircleDistance isn't authored: the closest desired
    // range among the mob's weapons, so the mob closes to within reach of all of
    // them. Defaults when the mob has no weapons authoring a range.
    private static float ClosestDesiredRange(Mob me)
    {
        Godot.Collections.Array<WeaponData> weapons = me.Weapons;
        float best = float.MaxValue;
        if (weapons != null)
        {
            for (int i = 0; i < weapons.Count; i++)
            {
                WeaponData w = weapons[i];
                if (w != null && w.desiredAttackRange < best)
                {
                    best = w.desiredAttackRange;
                }
            }
        }
        return best == float.MaxValue ? DefaultStandoffDistance : best;
    }

    protected void ReleaseSlot(Mob me)
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
