using System;
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

    public bool alive;
    public float maxHealth;
    public float health;
    public float aggro;
    public ulong alertRelaxationTime;
    public EAggroState aggroState;

    private MobSpawnState _spawnState;
    World _world;

    public static Mob Create(World world, MobSpawnState data)
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
        TreeExiting += () => world.onMobRemoved?.Invoke(this);
        world.onMobSpawned?.Invoke(this);
    }

    public void Initialize(World world, MobSpawnState spawnState)
    {
        _world = world;
        _spawnState = spawnState;
        Position = spawnState.WorldPosition;
        Rotation = new Vector3(0, spawnState.RotationY, 0);
        alive = spawnState.Alive;
        health = spawnState.Health;
        maxHealth = spawnState.MaxHealth;
        aggro = spawnState.Aggro;
        world.AddChild(this);
    }


    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_mesh != null)
        {
            _mesh.Scale = alive ? new Vector3(1f, 1f, 1f) : new Vector3(1f, 0.25f, 1f);
        }
    }

    override public void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);
        UpdateAggro((float)delta);
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
        if (_spawnState != null)
        {
            _spawnState.Alive = false;
        }
    }

    private void UpdateAggro(float delta)
    {
        if (!alive || _world.player == null)
        {
            return;
        }

        MobData mobData = _spawnState.MobData;
        if (mobData == null)
        {
            return;
        }

        float aggroDelta = 0f;
        Vector3 toPlayer = _world.player.GlobalPosition - GlobalPosition;
        float distanceSqToPlayer = toPlayer.LengthSquared();
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
                alertRelaxationTime = Time.GetTicksMsec() + (ulong)(mobData.AlertRelaxationTime * 1000);
            }
        }
        else
        {
            if (aggroState == EAggroState.Alert)
            {
                aggroState = EAggroState.Seek;
            }

            if (aggroState == EAggroState.Seek) {
                if (Time.GetTicksMsec() >= alertRelaxationTime)
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
