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

        if (_hurtBox != null)
        {
            _hurtBox.OnHit = Hit;
        }
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

            if (aiOutput.pathTarget.HasValue)
            {
                LinearDamp = 0f;

                Vector3 toTarget = aiOutput.pathTarget.Value - GlobalPosition;
                toTarget.Y = 0f;
                if (toTarget.LengthSquared() > 0.01f)
                {
                    toTarget = toTarget.Normalized();
                    Vector3 desiredVelocity = toTarget * 3f * _terrainSpeed;
                    Vector3 velocityChange = desiredVelocity - new Vector3(LinearVelocity.X, 0f, LinearVelocity.Z);
                    ApplyCentralImpulse(new Vector3(velocityChange.X, 0f, velocityChange.Z) * Mass);

                    float targetYaw = Mathf.Atan2(toTarget.X, toTarget.Z);
                    float yawDelta = Mathf.Wrap(targetYaw - Rotation.Y, -Mathf.Pi, Mathf.Pi);
                    AngularVelocity = new Vector3(0f, yawDelta * 8f, 0f);
                }
            }
            else
            {
                LinearDamp = 8f;
                if (aiOutput.yaw.HasValue)
                {
                    float yawDelta = Mathf.Wrap(aiOutput.yaw.Value - Rotation.Y, -Mathf.Pi, Mathf.Pi);
                    AngularVelocity = new Vector3(0f, yawDelta * 8f, 0f);
                }
                else
                {
                    AngularVelocity = Vector3.Zero;
                }
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
        if (!alive)
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

    public void AddTerrainModifier(TallGrass tallGrass)
    {
        _tallGrassCollisions.Add(tallGrass);
    }

    public void RemoveTerrainModifier(TallGrass tallGrass)
    {
        _tallGrassCollisions.Remove(tallGrass);
    }

}
