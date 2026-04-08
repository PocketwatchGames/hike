using System;
using Godot;

[GlobalClass]
public partial class Mob : RigidBody3D
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

}
