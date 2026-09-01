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
    // Game-time (ms) the post-cooldown behavior pause ends. Rolled once on the
    // edge where a weapon first comes off its fixed cooldown (no-weapon-ready →
    // ready); until it elapses the mob holds at the encircle ring rather than
    // swinging, so the cadence isn't a tight cooldown loop. 0 = no pause armed
    // (nothing currently ready). See AttackBehaviorData.attackPauseSeconds.
    private ulong _attackPauseUntilMs;

    // The weapon being brought to bear this tick (the highest-priority ready
    // one, or null when all are on cooldown / out of reach and the mob holds at
    // the encircle ring). Exposed so a subclass can react to which weapon is
    // engaging — BehaviorFlyAttack reads it to pick its hover altitude tier
    // (rise for a long-range weapon, descend to the target for a melee one).
    // Valid only on ticks that reach weapon selection (Run past the no-target /
    // transition early-outs).
    protected WeaponData ChosenWeapon { get; private set; }

    public BehaviorAttack(AttackBehaviorData data)
    {
        _data = data;
    }

    // Cooldown is intentionally NOT reset here: behaviors that swap out (e.g.
    // to Investigate and back) shouldn't grant a free attack on re-entry. The
    // encircle slot is released on exit/target-change, not on enter. The pause
    // IS cleared so re-entry re-rolls a fresh post-cooldown beat (still no free
    // swing — a ready weapon arms a new pause before it can fire).
    public override void OnEnter(Mob me, ulong time)
    {
        _attackPauseUntilMs = 0;
        _walkLineCheckedMs = 0;
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
        // dangerous and player-perceived). Composed on top of the behavior's
        // Engaging base; set after the transition / no-target early-outs so
        // leaving the attack state doesn't read as combat.
        output.behaviorFlags |= EBehaviorFlags.Attacking;
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
            output.vocalization = EVocalization.Yell;
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
        // plateau up. Fire it the instant we're inside its maxAttackRange.
        WeaponData readyWeapon = ChooseReadyWeapon(me, time, diff.Y, dist2d, canSee, targetPos);
        // Post-cooldown behavior pause. Once a weapon clears its fixed cooldown
        // we hold one extra (authored) beat — circling the ring — before the
        // swing commits, so attacks aren't a tight cooldown loop and there's a
        // readable gap between them (the window dodging happens in). The pause is
        // armed on the no-ready → ready edge and cleared the moment nothing is
        // ready again (i.e. right after a swing puts the weapon back on cooldown),
        // so each cycle rolls a fresh beat.
        if (readyWeapon == null)
        {
            _attackPauseUntilMs = 0;
        }
        else if (_attackPauseUntilMs == 0)
        {
            float pause = _data.attackPauseSeconds + (float)GD.RandRange(0.0, _data.attackPauseRandomSeconds);
            _attackPauseUntilMs = time + (ulong)(pause * 1000f);
        }
        // During the pause the mob is treated as having no ready weapon for the
        // swing + standoff, so it falls back to the encircle ring and waits.
        WeaponData ready = (readyWeapon != null && time >= _attackPauseUntilMs) ? readyWeapon : null;
        // ChosenWeapon reflects the weapon being ENGAGED with (pause-independent),
        // not just the one swinging this tick — a flying attacker reads it to hold
        // its cruise altitude through the pause instead of bobbing down between
        // volleys.
        ChosenWeapon = readyWeapon;
        // Don't commit a swing until roughly facing the target, so attacks don't
        // fire off-axis. The mob keeps turning toward the target (output.yaw) while
        // it waits. Bypassed when its facing is frozen off-screen (it can't turn to
        // satisfy the gate, and an off-axis swing the player can't see is harmless)
        // so an unseen attacker never deadlocks. dist2d ~ 0 (target on top) passes.
        bool facingShown = me.playerCanSee || Teams.AreAllied(me.ActorTeam, ETeam.Player);
        float facingTolerance = Mathf.DegToRad(_data.attackFacingToleranceDegrees);
        bool facingTarget = !facingShown
            || dist2d <= 0.0001f
            || Mathf.Abs(Mathf.Wrap((output.yaw ?? me.Rotation.Y) - me.Rotation.Y, -Mathf.Pi, Mathf.Pi)) <= facingTolerance;
        if (ready != null && dist2d < ready.maxAttackRange && facingTarget)
        {
            // Mob's _PhysicsProcess will TryStart the profile this same tick.
            output.attackProfile = ready.actionProfile;
            output.attackContext = new ActionContext { target = target, primaryItem = me.GetWeapon(ready) };
            // Fixed per-weapon cooldown. Cadence variety lives in the separate
            // post-cooldown behavior pause above (AttackBehaviorData), not here.
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

        Sim sim = me.Sim;
        EncircleSlotAllocator allocator = sim?.EncircleAllocator;
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
        // Standoff distance splits by intent. When a non-reactive weapon is ready
        // we close to its desiredAttackRange (which sits inside maxAttackRange) so
        // the swing lands; with everything on cooldown — or when the only ready
        // weapon is reactive (a kiter's melee, which must never pull the mob in) —
        // we hold at the ring / ranged standoff and wait. This split is why
        // encircleDistance may sit at — or beyond — maxAttackRange without freezing
        // the mob: the attack approach never holds at the ring.
        Vector3 standoff;
        float holdDistance = (_data.encircleDistance > 0f) ? _data.encircleDistance : ClosestDesiredRange(me);
        float standoffDistance = (ready != null && !ready.aiReactiveOnly)
            ? ready.desiredAttackRange
            : holdDistance;
        if (slotIdx < 0)
        {
            float angleToTarget = Mathf.Atan2(diff.X, diff.Z);
            standoff = NavigationGoals.PickStandoffPoint(sim, me.Navigator.Profile, targetPos, standoffDistance, angleToTarget);
        }
        else
        {
            float slotAngle = EncircleSlotAllocator.SlotAngle(slotIdx, _data.encircleSlotCount);
            standoff = NavigationGoals.PickStandoffPoint(sim, me.Navigator.Profile, targetPos, standoffDistance, slotAngle);
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
    // maxAttackRange — so a mob whose encircle ring sits outside attack range
    // still approaches and swings. The exception is an aiReactiveOnly weapon,
    // which IS distance-gated here (only eligible inside its maxAttackRange) so
    // the mob never closes for it — a kiter's reactive melee. Ties break toward
    // the earlier weapon in the list. Returns null when nothing qualifies (all on
    // cooldown, out of vertical reach, can't see, or the mob has no weapons), in
    // which case the mob holds at the encircle ring.
    // "Could I walk to the target from here" — the gate on any attack that darts
    // the body forward, memoized.
    //
    // The query samples a nav window, which is priced to ride the navigator's
    // 0.4s repath cadence, not a 60Hz one; and this is asked from
    // ChooseReadyWeapon, which runs EVERY tick a weapon is off cooldown (the
    // authored attack pause alone is dozens of ticks). Both bodies move at
    // walking speed, so the answer keeps for a beat — and the cost of it being
    // stale is at most one lunge either way.
    private const ulong WalkLineRecheckMs = 250;
    private ulong _walkLineCheckedMs;
    private bool _walkLineClear;

    private bool WalkLineClear(Mob me, ulong time, Vector3 targetPos)
    {
        if (_walkLineCheckedMs != 0 && time - _walkLineCheckedMs < WalkLineRecheckMs)
        {
            return _walkLineClear;
        }
        _walkLineCheckedMs = time;
        _walkLineClear = me.Navigator != null && me.Navigator.CanWalkStraightTo(targetPos);
        return _walkLineClear;
    }

    private WeaponData ChooseReadyWeapon(Mob me, ulong time, float diffY, float dist2d, bool canSee, Vector3 targetPos)
    {
        if (!canSee)
        {
            return null;
        }
        // A land mob knocked into deep water can't fight with no footing.
        // Suppressing the swing here (rather than letting ActionRunner reject
        // it every cooldown) keeps it from flailing — combined with the nav
        // grid refusing deep water, it just makes for the shallows. Intentional
        // swimmers (AvoidsDeepWater false) attack normally while swimming.
        if (me.IsSwimming && me.mobData != null && me.mobData.AvoidsDeepWater)
        {
            return null;
        }
        Godot.Collections.Array<WeaponData> weapons = me.Weapons;
        if (weapons == null)
        {
            return null;
        }
        WeaponData chosen = null;
        int weaponCount = weapons.Count;
        for (int i = 0; i < weaponCount; i++)
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
            if (Mathf.Abs(diffY) > w.maxVerticalAttackRange)
            {
                continue;
            }
            // A reactive weapon is never approached for — it's eligible only once
            // the target is already inside its reach, so a ranged kiter doesn't
            // close to melee between shots.
            if (w.aiReactiveOnly && dist2d > w.maxAttackRange)
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
            // A lunging attack darts the body forward along its facing for the
            // whole motion window, with no steering and no ledge sense — so it
            // must only be committed toward ground the mob could have walked to.
            // Without this a goblin lunges at a player standing across a drop and
            // the dart carries it over the edge, which is not a decision its own
            // pathfinder would ever have made (see MobNavigator.CanWalkStraightTo).
            //
            // Refused HERE rather than on the tier's requirements, for the same
            // reason as the vertical gate above: the profile is never committed, so
            // the cooldown isn't bumped and ActionRunner's rejectEffect doesn't
            // fire every tick. The mob falls through to the encircle ring and
            // repositions instead. Non-lunging attacks — every ranged weapon, a
            // plain swing — are untouched and still fire across a gap.
            if (w.actionProfile.Lunges && !WalkLineClear(me, time, targetPos))
            {
                continue;
            }
            chosen = w;
        }
        return chosen;
    }

    // Standoff fallback when encircleDistance isn't authored: the closest desired
    // range among the mob's NON-reactive weapons, so the mob holds within reach of
    // the weapons it actually closes to use. Reactive weapons (a kiter's melee) are
    // excluded so they don't collapse the hold distance onto melee range — the
    // mob kites at its ranged weapon's range instead. Falls back to including
    // reactive weapons only if that's all the mob has, then to the default.
    private static float ClosestDesiredRange(Mob me)
    {
        Godot.Collections.Array<WeaponData> weapons = me.Weapons;
        float bestNonReactive = float.MaxValue;
        float bestAny = float.MaxValue;
        if (weapons != null)
        {
            for (int i = 0; i < weapons.Count; i++)
            {
                WeaponData w = weapons[i];
                if (w == null)
                {
                    continue;
                }
                if (w.desiredAttackRange < bestAny)
                {
                    bestAny = w.desiredAttackRange;
                }
                if (!w.aiReactiveOnly && w.desiredAttackRange < bestNonReactive)
                {
                    bestNonReactive = w.desiredAttackRange;
                }
            }
        }
        if (bestNonReactive != float.MaxValue)
        {
            return bestNonReactive;
        }
        return bestAny != float.MaxValue ? bestAny : DefaultStandoffDistance;
    }

    protected void ReleaseSlot(Mob me)
    {
        if (_slotTarget == null)
        {
            return;
        }
        me.Sim?.EncircleAllocator?.ReleaseSlot(me);
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
        MobSpatialHash hash = me.Sim?.MobSpatialHash;
        if (hash == null)
        {
            return 0;
        }
        _allyScratch.Clear();
        hash.QueryRadius(me.GlobalPosition, radius, _allyScratch);
        // Runtime ActorTeam + Teams.AreAllied, mirroring DoApplyAreaStatusEffect
        // so the ally gate counts exactly the mobs the cry will actually buff.
        ETeam team = me.ActorTeam;
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
            if (Teams.AreAllied(team, m.ActorTeam))
            {
                count++;
            }
        }
        _allyScratch.Clear();
        return count;
    }
}
