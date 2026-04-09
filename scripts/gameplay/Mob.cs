using System.Collections.Generic;
using Godot;

public enum EAggroState
{
    Idle,
    Seek,
    Alert
}

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
    public float aggro { get => _simState.Aggro; set => _simState.Aggro = value; }
    public EAggroState aggroState { get => _simState.AggroState; set => _simState.AggroState = value; }
    public EPlayerPerceptionState playerPerceptionState { get => _simState.PlayerPerceptionState; set => _simState.PlayerPerceptionState = value; }

    private MobSimState _simState;
    World _world;

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

        UpdateVisibility();
        UpdateAggro((float)delta);

        UpdateTerrainSpeed();
        if (alive)
        {
            if (aggroState == EAggroState.Alert && _world.player != null)
            {
                Vector3 toPlayer = _world.player.GlobalPosition - GlobalPosition;
                toPlayer.Y = 0f;
                if (toPlayer.LengthSquared() > 0.01f)
                {
                    toPlayer = toPlayer.Normalized();
                    Vector3 desiredVelocity = toPlayer * 3f * _terrainSpeed;
                    Vector3 velocityChange = desiredVelocity - new Vector3(LinearVelocity.X, 0f, LinearVelocity.Z);
                    ApplyCentralImpulse(new Vector3(velocityChange.X, 0f, velocityChange.Z) * Mass);

                    float targetYaw = Mathf.Atan2(toPlayer.X, toPlayer.Z);
                    float yawDelta = Mathf.Wrap(targetYaw - Rotation.Y, -Mathf.Pi, Mathf.Pi);
                    AngularVelocity = new Vector3(0f, yawDelta * 8f, 0f);
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

    private void UpdateAggro(float delta)
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
            float visibilityDistance = _world.player.visionRange * visibility;
            float aggroDelta = Mathf.Clamp(1f - (distanceSqToPlayer / (visibilityDistance * visibilityDistance)), 0, 1);
            if (aggroDelta > _world.player.perceptionMinimum)
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
                    aggroDelta = 0f;
                }
            }
            else
            {
                aggroDelta = 0f;
            }
            if (aggroDelta > 0)
            {
                if (aggroDelta >= _world.player.perceptionInstant)
                {
                    _simState.PlayerPerception = 1;
                }
                else
                {
                    _simState.PlayerPerception = Mathf.Min(1.0f, _simState.PlayerPerception + aggroDelta * delta * mobData.PlayerPerceptionSpeed);
                }
                if (_simState.PlayerPerception >= 1)
                {
                    _simState.PlayerPerceptionState = EPlayerPerceptionState.Seen;
                }
                else if (_simState.PlayerPerception >= _world.player.perceptionDetectedThreshold && _simState.PlayerPerceptionState == EPlayerPerceptionState.Hidden)
                {
                    _simState.PlayerPerceptionState = EPlayerPerceptionState.Detected;
                }

                if (_simState.PlayerPerceptionState == EPlayerPerceptionState.Seen)
                {
                    _simState.PlayerPerceptionRelaxationTimeMs = _world.GameTimeMs + (ulong)(mobData.PlayerSeenRelaxationTime * 1000);
                    perceptionProgress = 1;
                } else if (_simState.PlayerPerceptionState == EPlayerPerceptionState.Detected)
                {
                    perceptionProgress = Mathf.Clamp((_simState.PlayerPerception - _world.player.perceptionDetectedThreshold) / (1.0f - _world.player.perceptionDetectedThreshold), 0f, 1f);
                }
            }
            else if (_simState.PlayerPerceptionState == EPlayerPerceptionState.Seen)
            {
                if (_world.GameTimeMs >= _simState.PlayerPerceptionRelaxationTimeMs)
                {
                    if (_simState.PlayerPerceptionState == EPlayerPerceptionState.Seen)
                    {
                        _simState.PlayerPerceptionState = EPlayerPerceptionState.Hidden;
                    }
                    _simState.PlayerPerception = 0;
                }
            }
            else
            {
                _simState.PlayerPerception = Mathf.Max(0f, _simState.PlayerPerception - mobData.PlayerPerceptionRelaxationSpeed * delta);
            }
        }

        // Mob to player
        {
            float aggroDelta = 0f;
            float visibilityDistance = mobData.VisionRange * Mathf.Pow(Mathf.Max(0, toPlayer.Normalized().Dot(GlobalTransform.Basis.Z)), mobData.VisionDotPower);
            if (aggroState == EAggroState.Idle || aggroState == EAggroState.Seek)
            {
                visibilityDistance *= _world.player.visibility;
            }
            aggroDelta = Mathf.Clamp(1f - (distanceSqToPlayer / (visibilityDistance * visibilityDistance)), 0, 1);
            if (aggroDelta > 0f)
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
                    aggroDelta = 0f;
                }
            }
            if (aggroDelta > mobData.MinAggroDelta)
            {
                aggro = Mathf.Clamp(aggro + aggroDelta / (1.0f - mobData.MinAggroDelta) * mobData.AggroIncreaseSpeed * delta, 0f, 1f);
                if (aggro >= mobData.AggroThresholdAlert)
                {
                    aggroState = EAggroState.Alert;
                }

                if (aggroState == EAggroState.Alert || aggroState == EAggroState.Seek)
                {
                    _simState.AlertRelaxationTimeMs = _world.GameTimeMs + (ulong)(mobData.AlertRelaxationTime * 1000);
                }
            }
            else
            {
                if (aggroState == EAggroState.Alert)
                {
                    aggroState = EAggroState.Seek;
                }

                if (aggroState == EAggroState.Seek)
                {
                    if (_world.GameTimeMs >= _simState.AlertRelaxationTimeMs)
                    {
                        aggroState = EAggroState.Idle;
                    }
                }
                else
                {
                    aggro = Mathf.Clamp(aggro - mobData.AggroRelaxationSpeed * delta, 0f, 1f);
                }
            }
        }
    }

}
