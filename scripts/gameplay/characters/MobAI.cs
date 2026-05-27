using Godot;

public struct AIOutput
{
    public Vector3? pathTarget;
    public float? yaw;
    public float speed;
    // When non-null, Mob's tick will TryStart this action through its
    // ActionRunner (subject to the runner's busy / cooldown checks).
    // attackContext supplies target / supportingItems / etc.; primaryItem
    // is left null since mobs aren't backed by a WeaponState yet.
    public ItemActionProfile attackProfile;
    public ActionContext attackContext;
    public bool yell;
    public Vector3 targetPos;
//    public Actor target;
    public float pathSuccessDistance;
    public bool inCombat;
    public bool burrow;
    // Flying mobs (MobData.canFly) only: when true, the mob is airborne this
    // tick — physics disables gravity and runs ApplyFlightPhysics (hover +
    // wind + steering) instead of ground locomotion. Flight is travel-only:
    // behaviors set this while moving between points and clear it to land, so
    // a bird is never left hovering in place by the behavior layer. flyAltitude
    // (when set) overrides MobData.hoverHeight as the target height above
    // terrain, so future low/medium/high cruise tiers just vary this value.
    public bool airborne;
    public float? flyAltitude;
    public bool useTorch;
    // True when TickAI early-returned because the mob is AI-suspended
    // (BehaviorIdle latches a 100ms suspend window once it's standing at
    // spawn so idle mobs can be physics-frozen). Downstream consumers of
    // AIOutput must treat all other fields as undefined and skip any
    // edge-style processing — e.g. the torch block would otherwise tear
    // down and re-instantiate _torch every suspend cycle since useTorch
    // defaults to false on a fresh AIOutput.
    public bool suspended;
    public InvestigateState? investigation;
    public bool resetInvestigation;
    public ulong? suspendTimeMs;
    // When set, Mob latches this as a one-shot animation through PlayOneShot
    // for the next tick. Looping animations are state-driven (alive/moving)
    // and chosen each tick by Mob.UpdateAnimation; behaviors only need to
    // emit one-shots (attack swing, yell, burrow stab) here. Nullable so
    // a behavior can leave it unset most ticks.
    public EAnimation? oneShotAnim;
}
public struct BehaviorOutput
{
    public BehaviorOutput(EBehaviorResult r, StringName newB = null)
    {
        result = r;
        newBehavior = newB;
    }
    public EBehaviorResult result;
    public StringName newBehavior;
}
public enum EBehaviorResult
{
    Running,
    RunNewBehavior,
    Complete,
}

public struct PerceptionState
{
    public float perception;
    public float aggro;
    public bool triggered;
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
    // True when the perceiving entity has unobstructed visual contact with
    // the target this tick (within range AND raycast unblocked). False if
    // out of range or the LOS check failed.
    public bool los;
}


public partial class Mob
{
//    private Vector3? _fleePosition;
    private StringName _curBehavior;
    private readonly System.Collections.Generic.Dictionary<StringName, BehaviorBase> _behaviors = new();

    // Called from Mob.Initialize after _simState is set. Walks the mob's BrainData,
    // creates a runtime BehaviorBase per node, validates that every transition
    // target names a real node, and sets the current behavior to idleBehavior.
    private void InitBehaviors()
    {
        _behaviors.Clear();
        _curBehavior = default;

        BrainData brain = mobData?.brain;
        if (brain == null || brain.behaviors == null)
        {
            return;
        }

        foreach (BehaviorNode node in brain.behaviors)
        {
            if (node == null || node.data == null)
            {
                GD.PushError($"Brain '{brain.ResourcePath}' contains a null node or node with null data");
                continue;
            }
            if (_behaviors.ContainsKey(node.name))
            {
                GD.PushError($"Brain '{brain.ResourcePath}' has duplicate behavior name '{node.name}'");
                continue;
            }
            BehaviorBase runtime = node.data.CreateRuntime();
            if (runtime == null)
            {
                continue;
            }
            runtime.Init(node);
            _behaviors[node.name] = runtime;
        }

        // Validate that every transition destination names a known node.
        foreach (BehaviorNode node in brain.behaviors)
        {
            if (node?.transitions == null)
            {
                continue;
            }
            foreach (BehaviorNodeTransition t in node.transitions)
            {
                if (t == null || t.destination == null)
                {
                    continue;
                }
                if (!_behaviors.ContainsKey(t.destination))
                {
                    GD.PushError($"Brain '{brain.ResourcePath}' node '{node.name}' has transition to unknown destination '{t.destination}'");
                }
            }
        }

        StringName initial = _simState.InitialBehavior;
        if (initial != null && _behaviors.ContainsKey(initial))
        {
            _curBehavior = initial;
        }
        else
        {
            _curBehavior = brain.idleBehavior;
        }
        // Fire OnEnter for the starting behavior so its first tick sees the
        // same fresh-state guarantees that every later re-entry will. World
        // time isn't always meaningful at Initialize (the sim clock starts
        // ticking once GameClient runs), so 0 is fine — behaviors that need
        // a real timestamp can read me.World.GameTimeMs themselves.
        if (_curBehavior != null && _behaviors.TryGetValue(_curBehavior, out BehaviorBase startB) && startB != null)
        {
            startB.OnEnter(this, 0);
        }
    }

    private void TickAI(float deltaTime, out AIOutput output)
    {
        using var _profTickAI = Profiler.Sample("Mob.TickAI");
        output = new AIOutput();
        if (!alive)
        {
            output.suspended = true;
            return;
        }

        ulong time = _world.GameTimeMs;

        // Scan perception BEFORE honoring SuspendAITimeMs — a perception
        // trigger has to wake the mob immediately so the distance-LOD
        // suspend extension in Mob.PhysicsProcess doesn't strand a mob
        // through a player approach.
        PerceptionState targetPerception = default;
        PerceptionState[] targets = _simState.PerceptionTargets;
        bool triggered = false;
        float maxPerception = 0f;
        for (int idx = 0; idx < targets.Length; idx++)
        {
            ref PerceptionState s = ref targets[idx];
            triggered |= s.triggered;
            if (s.perception >= maxPerception)
            {
                maxPerception = s.perception;
                if (s.triggered && s.aggro >= targetPerception.aggro)
                {
                    targetPerception = s;
                }
            }
        }

        if (!triggered && _simState.SuspendAITimeMs > time)
        {
            output.suspended = true;
            return;
        }

        if (!triggered)
        {
            _simState.Yelled = false;
        }
        if (targetPerception.pawnTarget != null)
        {
            investigation = null;
        }

        int maxAttempts = 5;
        for (int i = 0; i < maxAttempts; i++)
        {
            if (_curBehavior != null && _behaviors.TryGetValue(_curBehavior, out BehaviorBase b) && b != null)
            {
                BehaviorOutput behaviorOutput;
                using (Profiler.Sample("Mob.BehaviorRun"))
                {
                    behaviorOutput = b.Run(this, time, ref targetPerception, ref output);
                }
                if (behaviorOutput.newBehavior != null)
                {
                    StartBehavior(behaviorOutput.newBehavior);
                    if (behaviorOutput.result == EBehaviorResult.RunNewBehavior)
                    {
                        continue;
                    }
                }
                else if (behaviorOutput.result == EBehaviorResult.Complete)
                {
                    StartBehavior(defaultBehavior);
                }
            }
            break;
        }

        // Navigator runs after the behavior so behaviors can set high-level
        // intent via Navigator.Goto/Wander and have the navigator translate
        // it into a pathTarget for the impulse layer. Behaviors that already
        // wrote pathTarget directly (legacy) win — the navigator only fills
        // it in when it's still null. See MobNavigator.WriteSteering.
        if (_navigator != null && !output.pathTarget.HasValue)
        {
            using (Profiler.Sample("Mob.NavigatorWriteSteering"))
            {
                _navigator.WriteSteering(deltaTime, ref output);
            }
        }

        output.inCombat = maxPerception > 0 && mobData != null && mobData.dangerous;
    }

    private void StartBehavior(StringName behaviorName)
    {
        if (behaviorName == _curBehavior)
        {
            return;
        }
        if (!_behaviors.TryGetValue(behaviorName, out BehaviorBase b) || b == null)
        {
            GD.PushError($"Mob attempted to start unknown behavior '{behaviorName}'");
            return;
        }
        _curBehavior = behaviorName;
        b.OnEnter(this, _world?.GameTimeMs ?? 0);
    }

    private void UpdatePerception(float delta)
    {
        using var _profPerception = Profiler.Sample("Mob.UpdatePerception");
        if (!alive || _world.player == null)
        {
            return;
        }

        MobData mobData = _simState.MobData;
        if (mobData == null)
        {
            return;
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
            _simState.PlayerPerception = perception.perception;
            EPlayerPerceptionState prevDiscoveryState = _simState.DiscoveryState;
            _simState.DiscoveryState = perception.state;

            if (prevDiscoveryState != EPlayerPerceptionState.Discovered
                && _simState.DiscoveryState == EPlayerPerceptionState.Discovered)
            {
                _world.WorldState?.SimState?.DiscoverMob(mobData);
            }

            if (_simState.DiscoveryState == EPlayerPerceptionState.Discovered)
            {
                if (result.activelyPerceived)
                {
                    _simState.MemoryTimeMs = _world.GameTimeMs + (ulong)(mobData.MemoryStationaryTime * 1000);
                    _simState.VisibleTimeMs = _world.GameTimeMs + (ulong)(_world.SimData.VisibleTime * 1000);
                }
                else
                {
                    if (LinearVelocity.LengthSquared() > 0.01f)
                    {
                        _simState.MemoryTimeMs = (ulong)Mathf.Min(_simState.MemoryTimeMs, _world.GameTimeMs + (ulong)(mobData.MemoryMovingTime * 1000));
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

        // Mob to player — updates PerceptionTargets[0] for the singleplayer case.
        // In multiplayer this loop would walk the array and fill a slot per player.
        {
            ref PerceptionState target = ref _simState.PerceptionTargets[0];
            target.target = _world.player;

            // Perched fliers are an elevated lookout: vision goes omnidirectional
            // (drop the facing cone) and reaches farther (perchedVisionRangeMultiplier).
            bool perched = _perched;
            float facingFactor = perched
                ? 1f
                : Mathf.Pow(Mathf.Max(0, toPlayer.Normalized().Dot(GlobalTransform.Basis.Z)), mobData.VisionDotPower);
            float visionRange = perched ? mobData.VisionRange * mobData.perchedVisionRangeMultiplier : mobData.VisionRange;
            // Fog/rain shorten the mob's sight of the player; sampled at the
            // player (the target here).
            float visibilityDistance = visionRange * facingFactor
                * PlayerPerception.VisionRangeMultiplier(_world, _world.player.GlobalPosition);
            if (!target.triggered)
            {
                visibilityDistance *= _world.player.visibility;
            }
            bool canSee = false;
            float visionDelta = 0;
            if (distanceSqToPlayer < visibilityDistance * visibilityDistance)
            {
                visionDelta = Mathf.Pow(Mathf.Clamp(1f - (distanceSqToPlayer / (visibilityDistance * visibilityDistance)), 0, 1), mobData.VisionRangePower);
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
                // Wind masks sound, fog carries it — sampled at the mob (listener).
                float maxAudibleDistance = playerDecibels * mobData.hearingRange
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
            if (mobData.SmellStrength > 0f && mobData.smellRange > 0f && scent != null)
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
                            float coeff = alignment >= 0f ? sim.SmellDownwindBoost : sim.SmellUpwindReduction;
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

            float visionContribution = visionDelta * mobData.VisionStrength;
            float hearingContribution = hearingDelta * mobData.HearingStrength;
            float smellContribution = smellDelta * mobData.SmellStrength;
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
            // the dot-power gate above; distance uses the mob's raw VisionRange
            // so the readout is independent of facing / player visibility.
            mobToPlayerDebug.vision = visionDelta;
            mobToPlayerDebug.hearing = hearingDelta;
            mobToPlayerDebug.smell = smellDelta;
            mobToPlayerDebug.lighting = _world.player.visibilityLight;
            mobToPlayerDebug.distance = mobData.VisionRange > 0f
                ? Mathf.Clamp(1f - Mathf.Sqrt(distanceSqToPlayer) / mobData.VisionRange, 0f, 1f)
                : 0f;
            mobToPlayerDebug.facing = Mathf.Pow(Mathf.Max(0f, toPlayer.Normalized().Dot(GlobalTransform.Basis.Z)), mobData.VisionDotPower);
            mobToPlayerDebug.speed = _world.player.visibilitySpeed;
            mobToPlayerDebug.camouflage = _world.player.visibilityCamouflage;
            mobToPlayerDebug.los = canSee;

            if (perceptionDelta > mobData.MinPerceptionDelta)
            {
                target.perception = Mathf.Clamp(
                    target.perception + perceptionDelta / (1.0f - mobData.MinPerceptionDelta) * mobData.PerceptionIncreaseSpeed * delta,
                    0f, 1f);
                // Triggered (combat alert) requires active visual contact —
                // a hearing-only spike raises perception but can't latch the
                // mob into the alert state on its own.
                if (canSee && target.perception >= mobData.PerceptionThresholdAlert)
                {
                    target.triggered = true;
                }
            }
            else
            {
                target.perception = Mathf.Clamp(target.perception - mobData.PerceptionRelaxationSpeed * delta, 0f, 1f);
                if (target.perception <= 0f)
                {
                    target.triggered = false;
                }
            }
            // Mirror perception into aggro for multi-target selection in TickAI.
            target.aggro = target.perception;
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
        float skyBrightness = SkyController.Current?.CurrentPrimaryIntensity ?? ws.SimData?.DayIntensityBase ?? 2f;
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
