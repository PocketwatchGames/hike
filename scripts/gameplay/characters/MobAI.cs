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
        if (!alive || _simState.SuspendAITimeMs > _world.GameTimeMs)
        {
            output.suspended = true;
            return;
        }

        ulong time = _world.GameTimeMs;

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
            foreach (TallGrass grass in _tallGrassCollisions)
            {
                camouflage = Mathf.Max(camouflage, grass.camouflage);
            }
            // Fold the transient mob-side visibility (movement / camouflage)
            // into prominence at the call site. Discoverables don't have a
            // transient term, so PerceptionInputs only carries one scalar
            // and mob composes its per-frame modulation into it here.
            float effectiveProminence = mobData.prominence * speedFactor * Mathf.Max(0f, 1f - camouflage);

            // Mob's own movement noise — sampled here so PlayerPerception
            // can add a hearing contribution. Sneak threshold is half max
            // speed; mobs don't have an authored sneakSpeed of their own.
            float mobSpeed = LinearVelocity.Length();
            float mobSneakSpeed = mobData.maxSpeed * 0.5f;
            float mobDecibels = PlayerPerception.ComputeMovementDecibels(mobSpeed, mobSneakSpeed, mobData.maxSpeed, mobData.sneakDecibels, mobData.runDecibels);

            var inputs = new PerceptionInputs
            {
                prominence = effectiveProminence,
                detectedThreshold = mobData.detectedThreshold,
                discoveredThreshold = mobData.discoveredThreshold,
                lightSampleHeight = 1f,
                losRayHeight = 1.5f,
                decibels = mobDecibels,
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
            using (Profiler.Sample("Mob.PerceptionRays"))
            {
                result = PlayerPerception.Tick(_world, GlobalPosition, in inputs, ref perception, delta);
            }
            _simState.PlayerPerception = perception.perception;
            _simState.DiscoveryState = perception.state;

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

        }

        // Mob to player — updates PerceptionTargets[0] for the singleplayer case.
        // In multiplayer this loop would walk the array and fill a slot per player.
        {
            ref PerceptionState target = ref _simState.PerceptionTargets[0];
            target.target = _world.player;

            float visibilityDistance = mobData.VisionRange * Mathf.Pow(Mathf.Max(0, toPlayer.Normalized().Dot(GlobalTransform.Basis.Z)), mobData.VisionDotPower);
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
                Godot.Collections.Dictionary result;
                using (Profiler.Sample("Mob.PerceptionRays"))
                {
                    using var query = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, (uint)ECollisionLayer.Environment);
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
                float maxAudibleDistance = playerDecibels * mobData.hearingRange;
                if (distanceSqToPlayer < maxAudibleDistance * maxAudibleDistance)
                {
                    hearingDelta = Mathf.Pow(1f - Mathf.Sqrt(distanceSqToPlayer) / maxAudibleDistance, mobData.hearingRangePower);
                }
            }

            float visionContribution = visionDelta * mobData.VisionStrength;
            float hearingContribution = hearingDelta * mobData.HearingStrength;
            float perceptionDelta = visionContribution + hearingContribution;

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

    public void Investigate(Vector3 position, float range, ulong cancelTimeMs, ulong pauseTimeMs)
    {
        investigation = new InvestigateState
        {
            position = position,
            range = range,
            cancelTime = _world.GameTimeMs + cancelTimeMs,
            pauseTime = pauseTimeMs,
        };
    }
}
