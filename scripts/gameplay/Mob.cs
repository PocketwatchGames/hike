using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class Mob : RigidBody3D, IWorldEntity
{
    [Export] private CollisionShape3D _collisionShape;
    [Export] private AnimationPlayer _animationPlayer;
    [Export] private Node3D _mesh;
    [Export] private Sprite3D _sprite;
    [Export] private HurtBox _hurtBox;
    [Export] public Node3D HudAnchor;
    [Export] public PackedScene HudScene;
    [Export] private Material _silhouetteMaterial;

    public float perceptionProgress;

    // Canonical sim state lives in MobSimState. The Mob node is a view; these
    // properties forward through so call sites and animations stay terse.
    public bool alive { get => _simState.Alive; set => _simState.Alive = value; }
    public float maxHealth => _simState.MaxHealth;
    public float health { get => _simState.Health; set => _simState.Health = value; }
    public EPlayerPerceptionState playerPerceptionState { get => _simState.PlayerPerceptionState; set => _simState.PlayerPerceptionState = value; }
    public MobData mobData => _simState.MobData;
    public StringName defaultBehavior => _simState?.InitialBehavior ?? (mobData != null ? mobData.defaultBehavior : (StringName)"Idle");
    public Vector3 weaponPosition => GlobalPosition;
    public Vector3 spawnPosition => _simState.SpawnPosition;
    public float spawnRotationY => _simState.SpawnRotationY;
    public InvestigateState? investigation { get => _simState.Investigation; set => _simState.Investigation = value; }
    public bool yelled { get => _simState.Yelled; set => _simState.Yelled = value; }
    public bool burrowed { get => _simState.Burrowed; set => _simState.Burrowed = value; }
    public bool burrowing { get => _simState.Burrowing; set => _simState.Burrowing = value; }
    public ulong burrowTimeMs { get => _simState.BurrowTimeMs; set => _simState.BurrowTimeMs = value; }
    // Perception/triggered forward through the first perception slot (the player
    // in singleplayer). Multi-target logic operates directly on PerceptionTargets
    // in TickAI; these accessors exist for the HUD and inline Mob logic that only
    // cares about the primary target.
    public float perception
    {
        get => _simState.PerceptionTargets[0].perception;
        set => _simState.PerceptionTargets[0].perception = value;
    }
    public bool triggered
    {
        get => _simState.PerceptionTargets[0].triggered;
        set => _simState.PerceptionTargets[0].triggered = value;
    }

    private MobSimState _simState;
    World _world;
    public World World => _world;

    readonly List<TallGrass> _tallGrassCollisions = new();
    float _terrainSpeed = 1f;
    public float visibility = 1f;

    // Captured in _Ready so burrow can drop the mesh and restore it. The drop
    // is sized from the collision capsule so kun_kun (short) and goblin (tall)
    // both end up about 3/4 of their body underground.
    private Vector3 _meshRestPosition;
    private float _meshBurrowDrop;
    private bool _silhouetteApplied;

    public static Mob Create(World world, MobSimState data)
    {
        var instance = data.Scene.Instantiate<Mob>();
        instance.Initialize(world, data);
        return instance;
    }

    public override void _Ready()
    {
        CollisionLayer = (uint)ECollisionLayer.Mob;
        CollisionMask = (uint)(ECollisionLayer.Environment | ECollisionLayer.Player);
        // Angular damping brakes yaw torque so the mob settles on its target
        // facing without overshooting into an oscillation.
        AngularDamp = 10f;

        if (_hurtBox != null)
        {
            _hurtBox.OnHit = Hit;
        }

        if (_mesh != null)
        {
            _meshRestPosition = _mesh.Position;
        }
        float capsuleHeight = 1.5f;
        if (_collisionShape?.Shape is CapsuleShape3D capsule)
        {
            capsuleHeight = capsule.Height;
        }
        _meshBurrowDrop = capsuleHeight * 0.75f;
    }

    public void OnSpawned(World world)
    {
        TreeExiting += () =>
        {
            SyncToSimState();
            world.onMobRemoved?.Invoke(this);
        };
        world.onMobSpawned?.Invoke(this);
    }

    public void Initialize(World world, MobSimState simState)
    {
        _world = world;
        _simState = simState;
        Position = simState.WorldPosition;
        Rotation = new Vector3(0, simState.RotationY, 0);
        InitBehaviors();
        world.AddChild(this);
    }

    // Writes the node's current transform back into the persistent sim state so
    // that when this Mob is freed (chunk unload, save), the saved position is
    // current rather than the original spawn position.
    private void SyncToSimState()
    {
        if (_simState == null)
        {
            return;
        }
        _simState.WorldPosition = Position;
        _simState.RotationY = Rotation.Y;
    }


    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_mesh != null)
        {
            _mesh.Scale = alive ? new Vector3(1f, 1f, 1f) : new Vector3(1f, 0.25f, 1f);
            _mesh.Visible = _simState.PlayerPerceptionState != EPlayerPerceptionState.Hidden;

            // Silhouette: mob is Discovered but no longer directly visible,
            // yet still within memory window — render as a black shape.
            bool shouldSilhouette = _silhouetteMaterial != null
                && _simState.PlayerPerceptionState == EPlayerPerceptionState.Discovered
                && _world.GameTimeMs >= _simState.VisibleTimeMs
                && _world.GameTimeMs < _simState.MemoryTimeMs;
            if (shouldSilhouette != _silhouetteApplied)
            {
                ApplySilhouette(_mesh, shouldSilhouette);
                _silhouetteApplied = shouldSilhouette;
            }

            // Burrow visual: fully burrowed mobs sit 3/4 underground, and
            // burrowing mobs lerp smoothly from rest to that depth over the
            // mob's burrowTime window. Dropping the mesh in local space (not
            // the rigid body) keeps physics and collision put — the mob is
            // still standing, just hiding.
            float burrowT = 0f;
            if (burrowed)
            {
                burrowT = 1f;
            }
            else if (burrowing)
            {
                float totalMs = _simState.MobData.burrowTime * 1000f;
                if (totalMs > 0f)
                {
                    ulong now = _world.GameTimeMs;
                    float remaining = burrowTimeMs > now ? burrowTimeMs - now : 0f;
                    burrowT = Mathf.Clamp(1f - remaining / totalMs, 0f, 1f);
                }
                else
                {
                    burrowT = 1f;
                }
            }
            Vector3 meshPos = _meshRestPosition;
            meshPos.Y -= _meshBurrowDrop * burrowT;
            _mesh.Position = meshPos;
        }
    }

    override public void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);

        UpdateTerrainSpeed();
        UpdateVisibility();

        // Perception is throttled — accumulate delta and only run when the
        // interval is reached, so per-mob raycast cost stays low at density.
        // The accumulated delta is passed in so the perception integrator
        // ramps over the same total time regardless of tick rate.
        _simState.PerceptionTickAccumulator += (float)delta;
        if (_simState.PerceptionTickAccumulator >= MobSimState.PerceptionTickInterval)
        {
            UpdatePerception(_simState.PerceptionTickAccumulator);
            _simState.PerceptionTickAccumulator = 0f;
        }

        if (alive)
        {
            TickAI((float)delta, out AIOutput aiOutput);

            // An explicit aiOutput.yaw always wins so behaviors like BehaviorAttack
            // can keep facing the player while circling to a reposition point.
            // Otherwise, if we're walking toward a path target, face that direction.
            float? targetYaw = aiOutput.yaw;

            if (aiOutput.pathTarget.HasValue)
            {
                Vector3 toTarget = aiOutput.pathTarget.Value - GlobalPosition;
                toTarget.Y = 0f;
                if (toTarget.LengthSquared() > 0.01f)
                {
                    LinearDamp = 0f;
                    toTarget = toTarget.Normalized();
                    Vector3 desiredVelocity = toTarget * 3f * _terrainSpeed;
                    Vector3 velocityChange = desiredVelocity - new Vector3(LinearVelocity.X, 0f, LinearVelocity.Z);
                    ApplyCentralImpulse(new Vector3(velocityChange.X, 0f, velocityChange.Z) * Mass);

                    if (!targetYaw.HasValue)
                    {
                        targetYaw = Mathf.Atan2(toTarget.X, toTarget.Z);
                    }
                }
                else
                {
                    // Arrived at the path target — brake so we don't drift past it.
                    LinearDamp = 8f;
                }
            }
            else
            {
                LinearDamp = 8f;
            }

            if (targetYaw.HasValue)
            {
                float yawDelta = Mathf.Wrap(targetYaw.Value - Rotation.Y, -Mathf.Pi, Mathf.Pi);
                // Torque toward the target yaw; AngularDamp (set in _Ready) provides
                // the braking term so the mob doesn't overshoot and oscillate.
                ApplyTorqueImpulse(new Vector3(0f, yawDelta * 20f * Mass, 0f));
            }

            if (aiOutput.resetInvestigation)
            {
                _simState.Investigation = default;
            }
            else if (aiOutput.investigation.HasValue)
            {
                _simState.Investigation = aiOutput.investigation.Value;
            }
            if (aiOutput.yell)
            {
                _simState.PlayerPerception = 1;
                _simState.PlayerPerceptionState = EPlayerPerceptionState.Discovered;
                _simState.MemoryTimeMs = _world.GameTimeMs + (ulong)(_simState.MobData.MemoryStationaryTime * 1000);
                float yellVolumeSq = _simState.MobData.yellVolume * _simState.MobData.yellVolume;
                foreach (Mob mob in _world.GetEntities<Mob>())
                {
                    if (mob == this)
                    {
                        continue;
                    }
                    if (GlobalPosition.DistanceSquaredTo(mob.GlobalPosition) < yellVolumeSq)
                    {
                        mob.Investigate(aiOutput.targetPos, 8, 30000, 3000);
                    }
                }
                _simState.Yelled = true;
            }
            // Two-phase burrow. When aiOutput.burrow first goes true we start
            // a Burrowing descent and arm burrowTime. Once the timer elapses
            // we flip to fully Burrowed. As soon as the behavior stops
            // requesting burrow we clear both flags and the mesh pops back up.
            if (aiOutput.burrow)
            {
                if (!burrowing && !burrowed)
                {
                    burrowing = true;
                    burrowTimeMs = _world.GameTimeMs + (ulong)(_simState.MobData.burrowTime * 1000f);
                }
                else if (burrowing && _world.GameTimeMs >= burrowTimeMs)
                {
                    burrowing = false;
                    burrowed = true;
                }
            }
            else
            {
                burrowing = false;
                burrowed = false;
            }
        }
        else
        {
            AngularDamp = 0.25f;
            LinearDamp = 0.25f;
        }
    }

    public void Hit(DamageData data, Node damageSource)
    {
        if (!alive || burrowed)
        {
            return;
        }

        Damage(data);

    }

    public void Damage(DamageData data)
    {
        if (data == null)
        {
            return;
        }

        health -= data.healthDamage;
        if (health <= 0f)
        {
            Die();
        }
    }

    private void Die()
    {
        if (!alive)
        {
            return;
        }

        alive = false;
    }

    private void UpdateTerrainSpeed()
    {
        _terrainSpeed = 1f;
        foreach (TallGrass grass in _tallGrassCollisions)
        {
            _terrainSpeed = Mathf.Min(_terrainSpeed, grass.speed);
        }
    }

    private void UpdateVisibility()
    {
        float lightFactor = Mathf.Clamp(_world.WorldState.GetLightLevelWorld(GlobalPosition) / ((float)LightEngine.MAX_LIGHT * _simState.MobData.visibilityLightMax), 0, 1);

        float speedFactor = _simState.MobData.maxVisibilitySpeed > 0f
            ? Mathf.Clamp(Mathf.Pow(LinearVelocity.Length() / _simState.MobData.maxVisibilitySpeed, _simState.MobData.visibilityMovementPower), _simState.MobData.visibilityMovementMin, 1f)
            : 1f;

        float camouflage = 0f;
        foreach (TallGrass grass in _tallGrassCollisions)
        {
            camouflage = Mathf.Max(camouflage, grass.camouflage);
        }

        visibility = Mathf.Clamp(lightFactor * speedFactor * (1.0f - camouflage), 0f, 1f);
    }

    private void ApplySilhouette(Node node, bool on)
    {
        if (node is MeshInstance3D mesh)
        {
            mesh.MaterialOverride = on ? _silhouetteMaterial : null;
        }
        foreach (Node child in node.GetChildren())
        {
            ApplySilhouette(child, on);
        }
    }

    public void AddTerrainModifier(TallGrass tallGrass)
    {
        _tallGrassCollisions.Add(tallGrass);
    }

    public void RemoveTerrainModifier(TallGrass tallGrass)
    {
        _tallGrassCollisions.Remove(tallGrass);
    }

}
