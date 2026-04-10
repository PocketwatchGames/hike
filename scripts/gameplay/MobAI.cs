using Godot;

public struct AIOutput
{
    public Vector3? pathTarget;
    public float? yaw;
    public float speed;
    public int? fireWeapon;
    public bool yell;
    public Vector3 targetPos;
//    public Actor target;
    public float pathSuccessDistance;
    public bool inCombat;
    public bool burrow;
    public InvestigateState? investigation;
    public bool resetInvestigation;
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
    private Vector3? _fleePosition;
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
    }

    private void TickAI(float deltaTime, out AIOutput output)
    {
        output = new AIOutput();
        if (!alive)
        {
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
                BehaviorOutput behaviorOutput = b.Run(this, time, ref targetPerception, ref output);
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

        output.inCombat = maxPerception > 0 && mobData != null && mobData.dangerous;
    }

    private void StartBehavior(StringName behaviorName)
    {
        if (behaviorName == _curBehavior)
        {
            return;
        }
        if (!_behaviors.ContainsKey(behaviorName))
        {
            GD.PushError($"Mob attempted to start unknown behavior '{behaviorName}'");
            return;
        }
        _curBehavior = behaviorName;
    }

    private void UpdatePerception(float delta)
    {
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

        // Player to mob
        {
            float visibilityDistance = _world.player.data.visionRange * visibility;
            float perceptionDelta = Mathf.Clamp(1f - (distanceSqToPlayer / (visibilityDistance * visibilityDistance)), 0, 1);
            if (perceptionDelta > _world.player.data.perceptionMinimum)
            {
                float eyeHeight = 1.5f;
                Vector3 rayStart = GlobalPosition + new Vector3(0f, eyeHeight, 0f);
                Vector3 rayEnd = _world.player.GlobalPosition + new Vector3(0f, eyeHeight, 0f);
                var query = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, (uint)ECollisionLayer.Environment);
                query.CollideWithAreas = false;
                query.CollideWithBodies = true;
                var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
                if (result.Count > 0)
                {
                    perceptionDelta = 0f;
                }
            }
            else
            {
                perceptionDelta = 0f;
            }

            if (perceptionDelta > 0)
            {
                if (perceptionDelta >= _world.player.data.perceptionInstant)
                {
                    _simState.PlayerPerception = 1;
                }
                else
                {
                    _simState.PlayerPerception = Mathf.Min(1.0f, _simState.PlayerPerception + perceptionDelta * delta * mobData.PlayerPerceptionSpeed);
                }
            }
            else
            {
                _simState.PlayerPerception = Mathf.Max(0f, _simState.PlayerPerception - mobData.PlayerPerceptionRelaxationSpeed * delta);
            }


            if (_simState.PlayerPerception >= 1)
            {
                _simState.PlayerPerceptionState = EPlayerPerceptionState.Discovered;
            }
            else if (_simState.PlayerPerception >= _world.player.data.perceptionDetectedThreshold && _simState.PlayerPerceptionState == EPlayerPerceptionState.Hidden)
            {
                _simState.PlayerPerceptionState = EPlayerPerceptionState.Detected;
            }

            if (_simState.PlayerPerceptionState == EPlayerPerceptionState.Discovered)
            {
                if (perceptionDelta > 0)
                {
                    _simState.MemoryTimeMs = _world.GameTimeMs + (ulong)(mobData.MemoryStationaryTime * 1000);
                    _simState.VisibleTimeMs = _world.GameTimeMs + (ulong)(mobData.VisibleTime * 1000);
                }
                else 
                {
                    if (LinearVelocity.LengthSquared() > 0.01f)
                    {
                        _simState.MemoryTimeMs = (ulong)Mathf.Min(_simState.MemoryTimeMs, _world.GameTimeMs + (ulong)(mobData.MemoryMovingTime * 1000));
                    }
                    if (_simState.PlayerPerception <= 0 && _world.GameTimeMs >= _simState.MemoryTimeMs)
                    {
                        _simState.PlayerPerceptionState = EPlayerPerceptionState.Hidden;
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
            float perceptionDelta = Mathf.Clamp(1f - (distanceSqToPlayer / (visibilityDistance * visibilityDistance)), 0, 1);

            bool canSee = false;
            if (perceptionDelta > 0f)
            {
                float eyeHeight = 1.5f;
                Vector3 rayStart = GlobalPosition + new Vector3(0f, eyeHeight, 0f);
                Vector3 rayEnd = _world.player.GlobalPosition + new Vector3(0f, eyeHeight, 0f);
                var query = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, (uint)ECollisionLayer.Environment);
                query.CollideWithAreas = false;
                query.CollideWithBodies = true;
                var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
                if (result.Count > 0)
                {
                    perceptionDelta = 0f;
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

            if (perceptionDelta > mobData.MinPerceptionDelta)
            {
                target.perception = Mathf.Clamp(
                    target.perception + perceptionDelta / (1.0f - mobData.MinPerceptionDelta) * mobData.PerceptionIncreaseSpeed * delta,
                    0f, 1f);
                if (target.perception >= mobData.PerceptionThresholdAlert)
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
