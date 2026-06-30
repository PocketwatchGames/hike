using Godot;

public struct PerceptionState
{
    public float perception;
    public bool triggered;
    // GameTimeMs at which `triggered` last latched from false to true — the
    // start of this engagement. Stamped only on the rising edge so a sustained
    // alert doesn't keep resetting it; used by TriggeredTimeoutCondition to bail
    // a mob out (e.g. a fairy's escape) when it hasn't been killed within a
    // window of first being triggered. Meaningless while `triggered` is false.
    public ulong triggeredTimeMs;
    public bool canSee;
    public Vector3 lastKnownPosition;
    Node3D _target;
    Player _pawnTarget;
    public Node3D target
    {
        get { return _target; }
        set { _target = value; _pawnTarget = _target as Player; }
    }
    public Player pawnTarget
    {
        get { return _pawnTarget; }
    }
}
public struct InvestigateState
{
    public Vector3 position;
    public float range;
    public ulong cancelTime;
    public ulong pauseTime;
    // True when this stimulus should only make the receiver glance toward the
    // source (BehaviorLookAt), not walk over and inspect it (BehaviorInvestigate).
    // Set by a yell when the receiver isn't an ally of the yeller — cross-team
    // alarms draw a look, ally alarms draw a full investigation.
    public bool lookOnly;
}

// Per-mob per-frame breakdown of the perception math, captured for the
// debug HUD overlay (CVars.debugPlayerPerception, CVars.debugMobPerception).
// Top row: per-sense delta contributions (vision/hearing/smell) BEFORE the
// per-sense Strength multipliers. Bottom row: input modulators going into
// the vision computation specifically — lighting at the target, distance
// closeness, facing alignment, speed-based visibility, and (1 - camouflage).
// Filled twice per mob per perception tick: once for player-perceives-mob,
// once for mob-perceives-player.
// Tri-state for the debug LOS readout. Unchecked means the raycast was skipped
// entirely (out of range, or the signal was below the perception floor so it
// couldn't register even with a clear line) — distinct from a raycast that ran
// and came back Blocked. Rendered as ? / - / + respectively.
public enum EPerceptionLos
{
    Unchecked,
    Blocked,
    Clear,
}

public struct PerceptionDebug
{
    public float vision;
    public float hearing;
    public float smell;
    public float lighting;
    public float distance;
    public float facing;
    public float speed;
    public float camouflage;
    // Sightline state THIS tick. Clear = raycast ran and was unobstructed;
    // Blocked = raycast ran and hit; Unchecked = no raycast (out of range or
    // below the perception floor — we never looked).
    public EPerceptionLos los;
}


public partial class Mob
{
    private void UpdatePerception(float delta)
    {
        using var _profPerception = Profiler.Sample("Mob.UpdatePerception");
        if (_world.player == null)
        {
            return;
        }

        MobData mobData = _simState.MobData;
        if (mobData == null)
        {
            return;
        }

        // Dead mobs still run the player→mob pass below so the player can
        // notice a corpse they walk up on — but a corpse that has already been
        // seen (CorpseDiscovered) is latched permanently visible, so there's
        // nothing left to accumulate: early-out to skip its per-corpse raycast
        // entirely. The mob→player sensing block at the bottom is gated on
        // `alive` (a corpse perceives nothing).
        bool dead = !alive;
        if (dead && _simState.DiscoveryState == EPlayerPerceptionState.CorpseDiscovered)
        {
            return;
        }
        // A resurrected mob drops the corpse latch back to a normal live
        // discovery so the perception machinery below governs it again.
        if (!dead && _simState.DiscoveryState == EPlayerPerceptionState.CorpseDiscovered)
        {
            _simState.DiscoveryState = EPlayerPerceptionState.Discovered;
        }

        Vector3 toPlayer = _world.player.GlobalPosition - GlobalPosition;
        float distanceSqToPlayer = toPlayer.LengthSquared();

        // Player to mob — delegated to PlayerPerception.Tick. The mob-side
        // bits (speed/camouflage modulation, MemoryTimeMs decay back to
        // Hidden) live around the call.
        {
            float speedFactor = mobData.maxVisibilitySpeed > 0f
                ? Mathf.Clamp(Mathf.Pow(LinearVelocity.Length() / mobData.maxVisibilitySpeed, mobData.visibilityMovementPower), mobData.visibilityMovementMin, 1f)
                : 1f;
            float camouflage = 0f;
            foreach (Foliage foliage in _foliageCollisions)
            {
                camouflage = Mathf.Max(camouflage, foliage.camouflage);
            }
            // Murky water conceals an aquatic mob: fold the local water
            // muddiness (scaled by waterClarityCamouflage) in alongside foliage,
            // taking the max so the strongest concealment wins.
            if (mobData.waterClarityCamouflage > 0f && IsInWater())
            {
                camouflage = Mathf.Max(camouflage, LocalWaterMuddiness() * mobData.waterClarityCamouflage);
            }
            // Fold the transient mob-side visibility (movement / camouflage)
            // into prominence at the call site. Discoverables don't have a
            // transient term, so PerceptionInputs only carries one scalar
            // and mob composes its per-frame modulation into it here.
            float effectiveProminence = mobData.prominence * speedFactor * Mathf.Max(0f, 1f - camouflage);
            // Airborne / perched mobs read against the sky / from up on a branch
            // and are easier to spot — flying most of all. Mutually exclusive
            // states, so pick one multiplier.
            if (_simState.Airborne)
            {
                effectiveProminence *= mobData.flyingProminenceMultiplier;
            }
            else if (_perched)
            {
                effectiveProminence *= mobData.perchedProminenceMultiplier;
            }

            // Mob's own movement noise — sampled here so PlayerPerception
            // can add a hearing contribution. Sneak threshold is half max
            // speed; mobs don't have an authored sneakSpeed of their own.
            float mobSpeed = LinearVelocity.Length();
            float mobSneakSpeed = mobData.maxSpeed * 0.5f;
            float mobDecibels = PlayerPerception.ComputeMovementDecibels(mobSpeed, mobSneakSpeed, mobData.maxSpeed, mobData.sneakDecibels, mobData.runDecibels);

            // Burrowed mobs are underground and invisible (no mesh above
            // ground, X-ray suppressed). Zero prominence + decibels so the
            // perception helper takes the decay branch — a player who never
            // discovered the mob can't latch onto it through dirt, and an
            // already-discovered mob's perception drains so its memory
            // eventually expires.
            if (burrowed)
            {
                effectiveProminence = 0f;
                mobDecibels = 0f;
            }

            var inputs = new PerceptionInputs
            {
                prominence = effectiveProminence,
                rangeScale = mobData.visionRangeScale,
                detectedThreshold = mobData.detectedThreshold,
                discoveredThreshold = mobData.discoveredThreshold,
                lightSampleHeight = 1f,
                losRayHeight = 1.5f,
                decibels = mobDecibels,
                // A flying or perched mob is on/among foliage — don't let porous
                // props occlude the player's view of it (it'd vanish into the
                // tree it's sitting in). Grounded mobs stay occluded by trees.
                seeThroughPorous = _simState.Airborne || _perched,
            };

            // Marshal the two split fields on MobSimState into the helper's
            // packed struct, run the tick, then write the new values back.
            // Cheap (struct copy) and avoids touching every other reader of
            // PlayerPerception / DiscoveryState in this pass.
            var perception = new PerceivedByPlayerState
            {
                perception = _simState.PlayerPerception,
                state = _simState.DiscoveryState,
            };
            PerceptionTickResult result;
            PerceptionDebug ptmDebug;
            using (Profiler.Sample("Mob.PerceptionRays"))
            {
                result = PlayerPerception.Tick(_world, GlobalPosition, in inputs, ref perception, delta, out ptmDebug);
            }
            // Mob's transient visibility terms (speed factor, 1-camouflage) are
            // folded into prominence at the call site, so PlayerPerception.Tick
            // can't recover them — copy them in here.
            ptmDebug.speed = speedFactor;
            ptmDebug.camouflage = Mathf.Max(0f, 1f - camouflage);
            playerToMobDebug = ptmDebug;
            if (dead)
            {
                // Perception keeps functioning normally toward an UNDISCOVERED
                // corpse — it rises as the player approaches and dampens (decays)
                // when contact is lost, just like a live mob. But the instant the
                // player actually lays eyes on it (activelyPerceived) we latch
                // CorpseDiscovered. From there the early-out at the top of this
                // method skips it forever and the visibility layer keeps it
                // fully lit — a discovered body is never re-hidden and never
                // dampens. We deliberately don't run the live memory/decay
                // bookkeeping below: a corpse has no VisibleTime / MemoryTime window.
                _simState.PlayerPerception = perception.perception;
                if (result.activelyPerceived)
                {
                    _simState.PlayerPerception = 1f;
                    _simState.DiscoveryState = EPlayerPerceptionState.CorpseDiscovered;
                    _world.WorldState?.SimState?.DiscoverSpecies(_simState.Species);
                }
            }
            else
            {
                _simState.PlayerPerception = perception.perception;
                EPlayerPerceptionState prevDiscoveryState = _simState.DiscoveryState;
                _simState.DiscoveryState = perception.state;

                if (prevDiscoveryState != EPlayerPerceptionState.Discovered
                    && _simState.DiscoveryState == EPlayerPerceptionState.Discovered)
                {
                    _world.WorldState?.SimState?.DiscoverSpecies(_simState.Species);
                }

                if (_simState.DiscoveryState == EPlayerPerceptionState.Discovered)
                {
                    if (result.activelyPerceived)
                    {
                        _simState.MemoryTimeMs = _world.GameTimeMs + (ulong)(mobData.memoryStationaryTime * 1000);
                        _simState.VisibleTimeMs = _world.GameTimeMs + (ulong)(_world.SimData.visibleTime * 1000);
                    }
                    else
                    {
                        if (LinearVelocity.LengthSquared() > 0.01f)
                        {
                            _simState.MemoryTimeMs = (ulong)Mathf.Min(_simState.MemoryTimeMs, _world.GameTimeMs + (ulong)(mobData.memoryMovingTime * 1000));
                        }
                        if (_simState.PlayerPerception <= 0 && _world.GameTimeMs >= _simState.MemoryTimeMs)
                        {
                            _simState.DiscoveryState = EPlayerPerceptionState.Hidden;
                        }
                    }
                }
                else if (_simState.DiscoveryState == EPlayerPerceptionState.Detected
                    && _simState.PlayerPerception < mobData.detectedThreshold)
                {
                    // Detected is a transient "noticed something" state with no
                    // memory window. PlayerPerception.Tick only does monotonic
                    // forward transitions, so once perception decays back below
                    // detectedThreshold (e.g. a kunkun burrows before being fully
                    // discovered and prominence drops to 0) we have to reset to
                    // Hidden ourselves — otherwise the MobHUD discovery bar sits
                    // on screen permanently with an empty fill.
                    _simState.DiscoveryState = EPlayerPerceptionState.Hidden;
                }
            }

        }

        // Mob to player — updates PerceptionTargets[0] for the singleplayer case.
        // In multiplayer this loop would walk the array and fill a slot per player.
        // A corpse perceives nothing, so the whole sensing pass (and its
        // raycasts) is skipped when dead.
        if (alive)
        {
            ref PerceptionState target = ref _simState.PerceptionTargets[0];
            target.target = _world.player;

            // Perched fliers are an elevated lookout: vision goes omnidirectional
            // (drop the facing cone) and reaches farther (perchedVisionRangeMultiplier).
            bool perched = _perched;
            // Geometric range gate: the mob's vision REACH, deliberately NOT shrunk
            // by the player's stealth or light (those shape clarity below) so the
            // range is stable and the player can reason about it. Mob visionRange
            // (15) is shorter than the player's (25), so at distance / off-cone the
            // player spots the mob first; the cone + clarity decide the rest.
            float maxRange = perched ? mobData.visionRange * mobData.perchedVisionRangeMultiplier : mobData.visionRange;
            bool inVisionRange = distanceSqToPlayer < maxRange * maxRange;
            float closeness = inVisionRange
                ? Mathf.Clamp(1f - Mathf.Sqrt(distanceSqToPlayer) / maxRange, 0f, 1f)
                : 0f;
            // Facing cone — the mob's view angle, the player's main positional
            // stealth lever (flank it). A HARD FOV limit (peripherally blind beyond
            // ±FOV/2), then inside the cone the forward-dot is remapped 0 (edge) → 1
            // (dead ahead) and raised to visionDotPower (sqrt) for clarity.
            // Omnidirectional when perched.
            float facingFactor;
            if (perched)
            {
                facingFactor = 1f;
            }
            else
            {
                float forwardDot = toPlayer.Normalized().Dot(GlobalTransform.Basis.Z);
                float cosHalfFov = Mathf.Cos(Mathf.DegToRad(mobData.visionFovDegrees * 0.5f));
                facingFactor = forwardDot <= cosHalfFov
                    ? 0f
                    : Mathf.Pow((forwardDot - cosHalfFov) / Mathf.Max(0.0001f, 1f - cosHalfFov), mobData.visionDotPower);
            }
            bool canSee = false;
            float visionDelta = 0f;
            if (inVisionRange)
            {
                // Clarity: how clearly the mob reads the player right now — the
                // facing cone, the player's stealth (light w/ the mob's
                // dark-adaptation relief, movement, camouflage, base prominence),
                // and fog over the sightline. A triggered mob has LOCKED ON: it
                // ignores the player's stealth (but must still face them and hold a
                // clear line). Eye dilation lifts only the light term, matching the
                // player→mob relief.
                float dilationRelief = _simState.EyeDilation * mobData.eyeDilationVisionRelief;
                float playerLight = Mathf.Lerp(_world.player.visibilityLight, 1f, dilationRelief);
                float playerStealth = target.triggered
                    ? 1f
                    : Mathf.Clamp(playerLight * _world.player.visibilitySpeed * _world.player.visibilityCamouflage, 0f, 1f)
                        * _world.player.data.prominence;
                float env = PlayerPerception.VisionRangeMultiplier(_world, GlobalPosition, _world.player.GlobalPosition);
                float clarity = facingFactor * env * playerStealth;
                // Signal = closeness curve × clarity. minPerceptionDelta is the
                // floor: kept low so perception starts rising EARLY and visibly (the
                // player sees the meter climb and can duck / slow before it commits),
                // and the raycast perf cull. Stealth can pull clarity under it — a
                // slow, shadowed, camouflaged player off the cone simply isn't seen.
                visionDelta = Mathf.Pow(closeness, mobData.visionRangePower) * clarity;
                if (visionDelta > mobData.minPerceptionDelta)
                {
                    float eyeHeight = 1.5f;
                    Vector3 rayStart = GlobalPosition + new Vector3(0f, eyeHeight, 0f);
                    Vector3 rayEnd = _world.player.GlobalPosition + new Vector3(0f, eyeHeight, 0f);
                    // Grounded vision is blocked by props (Solid); perched vision
                    // sees over/through foliage (Environment only) — the elevated
                    // lookout vantage, and what keeps the bird's own perch tree from
                    // blinding it without any per-prop exclusion.
                    uint visionMask = perched ? (uint)ECollisionLayer.Environment : (uint)ECollisionLayer.Solid;
                    Godot.Collections.Dictionary result;
                    using (Profiler.Sample("Mob.PerceptionRays"))
                    {
                        using var query = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, visionMask);
                        query.CollideWithAreas = false;
                        query.CollideWithBodies = true;
                        result = GetWorld3D().DirectSpaceState.IntersectRay(query);
                    }
                    if (result.Count > 0)
                    {
                        visionDelta = 0f;
                    }
                    else
                    {
                        canSee = true;
                    }
                }
                else
                {
                    visionDelta = 0f;
                }
            }
            target.canSee = canSee;
            if (canSee)
            {
                target.lastKnownPosition = _world.player.GlobalPosition;
            }

            // Hearing contribution. Audible distance = player's current
            // decibels * this mob's hearingRange; ramps linearly from 0 at
            // the edge to 1 at the player. No LOS / light gate.
            float hearingDelta = 0f;
            float playerDecibels = _world.player.CurrentDecibels;
            if (playerDecibels > 0f && mobData.hearingRange > 0f)
            {
                // An aquatic predator shares the water with its prey: while the
                // player is wading/swimming its splashing carries much farther
                // to this mob. waterHearingMultiplier is 1 (no effect) for land
                // mobs. Wind masks sound, fog carries it — sampled at the mob.
                float waterHearing = _world.player.IsInWater ? mobData.waterHearingMultiplier : 1f;
                float maxAudibleDistance = playerDecibels * mobData.hearingRange * waterHearing
                    * PlayerPerception.HearingRangeMultiplier(_world, GlobalPosition);
                if (distanceSqToPlayer < maxAudibleDistance * maxAudibleDistance)
                {
                    hearingDelta = Mathf.Pow(1f - Mathf.Sqrt(distanceSqToPlayer) / maxAudibleDistance, mobData.hearingRangePower);
                }
            }

            // Smell contribution. Walks the player's breadcrumb list; each
            // crumb in range contributes `strength * falloff(distance)`. Wind
            // shapes smell two ways: it drifts crumb positions during advection
            // (handled in ScentEmitter), AND it biases each crumb's perceived
            // strength here — downwind sources smell stronger, upwind weaker,
            // with high wind scattering the scent and widening fog holding it
            // (SmellRangeMultiplier). LOS raycast from the mob's nose to each candidate
            // prevents smelling through walls — without it a stationary
            // crumb on the far side of a thin wall would leak through.
            // Greedy gate: skip the raycast for any crumb whose potential
            // can't beat the running best. Same alert-gate shape as hearing:
            // smell raises perception but only vision crosses the triggered
            // threshold.
            float smellDelta = 0f;
            ScentEmitter scent = _world.player.Scent;
            if (mobData.smellStrength > 0f && mobData.smellRange > 0f && scent != null)
            {
                Vector3 nose = GlobalPosition + new Vector3(0f, 1.5f, 0f);
                // Fog widens the scent radius, high wind scatters it (both
                // direction-independent). The downwind/upwind bias is applied
                // per crumb below.
                float smellRange = mobData.smellRange * PlayerPerception.SmellRangeMultiplier(_world, nose);
                float smellRangeSq = smellRange * smellRange;
                // Precompute the wind once for the per-crumb directional term
                // (wind is global, so a sample per crumb would be wasteful).
                // smellWindBias scales the dot-product deviation: 0 in dead
                // calm (no directionality), up to 1 at PerceptionWindReference.
                SimData sim = _world.SimData;
                float smellWindBias = sim != null ? PlayerPerception.WindFraction(_world, nose) : 0f;
                Vector3 windDir3 = _world.WorldState.WindDirection;
                Vector2 windDir = new Vector2(windDir3.X, windDir3.Z);
                bool hasWind = smellWindBias > 0f && windDir.LengthSquared() > 0.000001f;
                if (hasWind)
                {
                    windDir = windDir.Normalized();
                }
                System.Collections.Generic.IReadOnlyList<ScentEmitter.Breadcrumb> crumbs = scent.Crumbs;
                for (int ci = 0; ci < crumbs.Count; ci++)
                {
                    ScentEmitter.Breadcrumb c = crumbs[ci];
                    float distSq = (c.pos - GlobalPosition).LengthSquared();
                    if (distSq >= smellRangeSq)
                    {
                        continue;
                    }
                    float dist = Mathf.Sqrt(distSq);
                    float potential = c.strength * Mathf.Pow(1f - dist / smellRange, mobData.smellRangePower);
                    // Downwind sources (wind blows crumb→nose) smell stronger;
                    // upwind ones weaker. Alignment is the dot of the unit
                    // crumb→nose vector with the wind, scaled by wind strength.
                    if (hasWind)
                    {
                        Vector3 toNose3 = nose - c.pos;
                        Vector2 toNose = new Vector2(toNose3.X, toNose3.Z);
                        if (toNose.LengthSquared() > 0.000001f)
                        {
                            float alignment = toNose.Normalized().Dot(windDir);
                            float coeff = alignment >= 0f ? sim.smellDownwindBoost : sim.smellUpwindReduction;
                            potential *= Mathf.Max(0f, 1f + coeff * alignment * smellWindBias);
                        }
                    }
                    if (potential <= smellDelta)
                    {
                        continue;
                    }
                    Vector3 crumbTarget = c.pos + new Vector3(0f, 0.5f, 0f);
                    Godot.Collections.Dictionary smellHit;
                    using (Profiler.Sample("Mob.PerceptionRays"))
                    {
                        // Smell masks Environment only, so scent drifts through
                        // porous props (trees) and is blocked just by terrain/walls.
                        using var query = PhysicsRayQueryParameters3D.Create(nose, crumbTarget, (uint)ECollisionLayer.Environment);
                        query.CollideWithAreas = false;
                        query.CollideWithBodies = true;
                        smellHit = GetWorld3D().DirectSpaceState.IntersectRay(query);
                    }
                    if (smellHit.Count == 0)
                    {
                        smellDelta = potential;
                    }
                }
            }

            float visionContribution = visionDelta * mobData.visionStrength;
            float hearingContribution = hearingDelta * mobData.hearingStrength;
            float smellContribution = smellDelta * mobData.smellStrength;
            float perceptionDelta = visionContribution + hearingContribution + smellContribution;

            // A hidden player (perched in a climbable tree) is unperceivable —
            // same treatment as the invisible cheat and the symmetric burrowed-
            // mob case: zero the delta so any standing perception decays and
            // triggered resets, and drop line-of-sight so a triggered mob can't
            // hold the alert through the concealment.
            if (CVars.invisible.Value || _world.player.IsHidden)
            {
                perceptionDelta = 0f;
                canSee = false;
                target.canSee = false;
            }

            // Debug breakdown — written every perception tick for the
            // CVars.debugMobPerception HUD overlay. Facing factor mirrors
            // the dot-power gate above; distance uses the mob's raw visionRange
            // so the readout is independent of facing / player visibility.
            mobToPlayerDebug.vision = visionDelta;
            mobToPlayerDebug.hearing = hearingDelta;
            mobToPlayerDebug.smell = smellDelta;
            mobToPlayerDebug.lighting = _world.player.visibilityLight;
            mobToPlayerDebug.distance = mobData.visionRange > 0f
                ? Mathf.Clamp(1f - Mathf.Sqrt(distanceSqToPlayer) / mobData.visionRange, 0f, 1f)
                : 0f;
            mobToPlayerDebug.facing = facingFactor;
            mobToPlayerDebug.speed = _world.player.visibilitySpeed;
            mobToPlayerDebug.camouflage = _world.player.visibilityCamouflage;
            // Unchecked when out of range (no raycast ran); otherwise Clear/Blocked
            // by the raycast result. Mirrors the player→mob tri-state.
            mobToPlayerDebug.los = inVisionRange
                ? (canSee ? EPerceptionLos.Clear : EPerceptionLos.Blocked)
                : EPerceptionLos.Unchecked;

            if (perceptionDelta > mobData.minPerceptionDelta)
            {
                // Accelerating accumulation: linear when the per-tick contact is
                // faint, very fast when it's strong, and continuous (no snap). A
                // player crossing close through the cone produces a high delta and
                // is caught near-instantly; a distant / edge-of-cone / stealthed
                // contact builds slowly and visibly, giving time to react.
                float accel = perceptionDelta * (1f + (mobData.perceptionAccel - 1f) * Mathf.Clamp(perceptionDelta, 0f, 1f));
                target.perception = Mathf.Clamp(
                    target.perception + accel * mobData.perceptionIncreaseSpeed * delta,
                    0f, 1f);
                // Triggered (combat alert) requires active visual contact —
                // a hearing-only spike raises perception but can't latch the
                // mob into the alert state on its own.
                if (canSee && target.perception >= mobData.perceptionThresholdAlert)
                {
                    if (!target.triggered)
                    {
                        target.triggeredTimeMs = _world.GameTimeMs;
                    }
                    target.triggered = true;
                }
                // Once alerted, strong-enough sensory contact — sight, hearing,
                // or smell — refreshes the fix on the player to its true
                // position, not just direct line of sight. Hearing/smell aren't
                // directional, but an alerted mob that can still clearly sense
                // the player should keep turning to face it (BehaviorAttack
                // drives yaw from lastKnownPosition while canSee is false). The
                // perceptionThresholdTrack gate (higher than minPerceptionDelta)
                // means faint edge-of-range contact sustains the alert but
                // doesn't snap facing — only contact strong enough to "track"
                // turns the mob. Breaking all sensory contact below
                // minPerceptionDelta drops into the decay branch where triggered
                // eventually clears.
                if (target.triggered && perceptionDelta > mobData.perceptionThresholdTrack)
                {
                    target.lastKnownPosition = _world.player.GlobalPosition;
                }
            }
            else
            {
                target.perception = Mathf.Clamp(target.perception - mobData.perceptionRelaxationSpeed * delta, 0f, 1f);
                if (target.perception <= 0f)
                {
                    target.triggered = false;
                }
            }
        }

        // Bleed the per-enemy aggro meters down. Runs on the perception tick (so
        // `delta` is the accumulated tick interval) and only while alive — a
        // corpse holds no grudge. Decay also prunes entries whose target died or
        // was freed so the table never hands a stale node to target selection.
        if (alive)
        {
            _simState.Aggro.Decay(mobData.aggroReductionSpeed, delta);
        }

        // Cross-faction threat awareness — a second perception accumulation
        // toward the nearest mob on the opposite side of the player divide, using
        // the same vision model as the player slot above. Only two kinds of mob
        // care: a dangerous hostile (tracks the player's companions to attack
        // them) and a companion (a guard dog, aware of enemies AND harmless
        // wildlife so its brain can bark / be curious). Everyone else pays
        // nothing. No per-mob faction to author — it falls out of `dangerous` /
        // being tamed.
        if (alive && (mobData.dangerous || IsCompanion))
        {
            AccumulateThreatPerception(mobData, delta);
        }
    }

    // Receive a discrete noise impulse (see World.CreateNoiseEvent). Reuses the
    // exact hearing math the per-tick movement-noise contribution uses — audible
    // distance = decibels * hearingRange (wind/fog adjusted), falling off with
    // the authored hearingRangePower curve — but applies it as a one-shot
    // perception bump rather than a delta-scaled accumulation. Only the
    // perception slot tracking `source` rises (singleplayer: the player slot),
    // so a noise from an actor this mob doesn't track goes unheard. Hearing
    // alone never latches the combat-alert (triggered) state — that still
    // requires line of sight, matching UpdatePerception — so a noise primes the
    // meter and a follow-up sighting confirms it.
    public void HearNoise(Vector3 position, float decibels, Node3D source)
    {
        if (!alive || source == null || decibels <= 0f)
        {
            return;
        }
        MobData mobData = _simState?.MobData;
        if (mobData == null || mobData.hearingRange <= 0f || mobData.hearingStrength <= 0f)
        {
            return;
        }
        PerceptionState[] targets = _simState.PerceptionTargets;
        if (targets == null)
        {
            return;
        }
        for (int i = 0; i < targets.Length; i++)
        {
            if (targets[i].target != source)
            {
                continue;
            }
            float maxAudibleDistance = decibels * mobData.hearingRange
                * PlayerPerception.HearingRangeMultiplier(_world, GlobalPosition);
            if (maxAudibleDistance <= 0f)
            {
                return;
            }
            float distSq = (position - GlobalPosition).LengthSquared();
            if (distSq >= maxAudibleDistance * maxAudibleDistance)
            {
                return;
            }
            float falloff = Mathf.Pow(1f - Mathf.Sqrt(distSq) / maxAudibleDistance, mobData.hearingRangePower);
            targets[i].perception = Mathf.Clamp(targets[i].perception + falloff * mobData.hearingStrength, 0f, 1f);
            return;
        }
    }

    // Build perception toward the nearest opposite-side mob (ThreatScan) exactly
    // as the mob→player block does — closeness^visionRangePower over
    // visionRange, gated by line of sight, accumulated at perceptionIncreaseSpeed
    // and relaxed at perceptionRelaxationSpeed, latching `triggered` at
    // perceptionThresholdAlert. The one deliberate difference from the player
    // block is that this vision is omnidirectional (no facing cone): a vigilant
    // guard dog scans all around, like the perched-lookout case above. The
    // crossing of perceptionThresholdWary / perceptionThresholdAlert drives the
    // companion brain's Wary / Attack tiers; ThreatScan supplies the candidate
    // (already filtered to triggered, enemy-team, in range, with line of sight).
    private void AccumulateThreatPerception(MobData mobData, float delta)
    {
        ref PerceptionState slot = ref _simState.ThreatPerception;
        // A companion is a vigilant guard — its threat channel tracks dangerous
        // enemies it merely sees (harmless critters are handled by idle curiosity
        // in BehaviorWanderFollow, not here). A hostile tracks the player's
        // companions (which aren't dangerous-flagged) and only latches onto one
        // once it's in combat, so it ignores a harmlessly idling pet and keeps
        // focus on the player until the pet engages.
        Mob enemy = ThreatScan.FindNearest(this, mobData.visionRange,
            requireTriggered: !IsCompanion,
            danger: IsCompanion ? EThreatDanger.DangerousOnly : EThreatDanger.Any);

        // Target died (or was despawned) with no live replacement in range — drop
        // the engagement immediately instead of letting perception relax toward a
        // corpse, so the dog stops attacking / being wary the instant it kills.
        // (A living target that's merely out of sight leaves `enemy` null too, but
        // its corpse check fails, so the slow-relax memory below still applies.)
        if (enemy == null && slot.target is Mob prev
            && (!GodotObject.IsInstanceValid(prev) || !prev.alive))
        {
            slot.perception = 0f;
            slot.triggered = false;
            slot.canSee = false;
            slot.target = null;
            return;
        }
        slot.target = enemy;

        bool canSee = enemy != null;
        float perceptionDelta = 0f;
        if (canSee)
        {
            // Fog/rain shorten the sightline; fog averaged over both ends of the
            // mob→enemy line.
            float visionRange = mobData.visionRange
                * PlayerPerception.VisionRangeMultiplier(_world, GlobalPosition, enemy.GlobalPosition);
            if (visionRange > 0f)
            {
                float distSq = (enemy.GlobalPosition - GlobalPosition).LengthSquared();
                float closeness = Mathf.Pow(
                    Mathf.Clamp(1f - distSq / (visionRange * visionRange), 0f, 1f),
                    mobData.visionRangePower);
                perceptionDelta = closeness * mobData.visionStrength;
            }
            slot.lastKnownPosition = enemy.GlobalPosition;
        }
        slot.canSee = canSee;

        if (perceptionDelta > mobData.minPerceptionDelta)
        {
            slot.perception = Mathf.Clamp(
                slot.perception + perceptionDelta / (1.0f - mobData.minPerceptionDelta) * mobData.perceptionIncreaseSpeed * delta,
                0f, 1f);
            // Latch into combat on sight only when the perceived enemy is itself
            // flagged as triggering (enemy.mobData.canTriggerMobs) — a harmless
            // target (a tamed pet) builds awareness here but never flips this mob
            // triggered. Such a mob is then triggered toward the enemy only by
            // being attacked (Mob.Hit sets the slot triggered directly, which
            // this branch preserves since it never clears it).
            if (canSee && slot.perception >= mobData.perceptionThresholdAlert && enemy.mobData.canTriggerMobs)
            {
                slot.triggered = true;
            }
        }
        else
        {
            slot.perception = Mathf.Clamp(slot.perception - mobData.perceptionRelaxationSpeed * delta, 0f, 1f);
            if (slot.perception <= 0f)
            {
                slot.triggered = false;
            }
        }
    }

    // Throttled environment-light cache. SkyBrightness is the time-of-day /
    // storm-scaled primary intensity (the "the sun itself is dim" signal),
    // SunExposure is the BFS sunlight reaching this voxel through geometry
    // (the "I'm under a roof" signal), AmbientLight is their product — the
    // single number behaviors gate on for "should I light a torch". Block
    // lights are deliberately ignored so a mob's own torch doesn't extinguish
    // itself by raising the sample.
    private void SampleAmbientLight()
    {
        using var _profLight = Profiler.Sample("Mob.SampleAmbientLight");
        WorldState ws = _world?.WorldState;
        if (ws == null)
        {
            return;
        }
        const float eyeHeight = 1f;
        Vector3 pos = GlobalPosition + new Vector3(0f, eyeHeight, 0f);
        int wx = Mathf.FloorToInt(pos.X);
        int wy = Mathf.FloorToInt(pos.Y);
        int wz = Mathf.FloorToInt(pos.Z);
        int sunBfs = ws.GetSunlightWorld(wx, wy, wz);
        float sunExposure = (float)sunBfs / LightEngine.MAX_LIGHT;
        float skyBrightness = SkyController.Current?.CurrentPrimaryIntensity ?? ws.SimData?.dayIntensityBase ?? 2f;
        _simState.SunExposure = sunExposure;
        _simState.SkyBrightness = skyBrightness;
        _simState.AmbientLight = sunExposure * skyBrightness;
    }

    public void Investigate(Vector3 position, float range, ulong cancelTimeMs, ulong pauseTimeMs, bool lookOnly = false)
    {
        investigation = new InvestigateState
        {
            position = position,
            range = range,
            cancelTime = _world.GameTimeMs + cancelTimeMs,
            pauseTime = pauseTimeMs,
            lookOnly = lookOnly,
        };
    }
}
