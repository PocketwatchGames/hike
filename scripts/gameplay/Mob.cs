using System;
using Godot;

[GlobalClass]
public partial class Mob : RigidBody3D
{
    [Export] private CollisionShape3D _collisionShape;
    [Export] private AnimationPlayer _animationPlayer;
    [Export] private Node3D _mesh;
    [Export] private Sprite3D _sprite;
    [Export] private bool _alive = true;

    private MobSpawnState _spawnData;

    public override void _Ready()
    {
        CollisionLayer = (uint)ECollisionLayer.Mob;
        CollisionMask = (uint)(ECollisionLayer.Environment | ECollisionLayer.Player);

        if (!_alive)
        {
            ApplyDeadVisual();
        }
    }

    public void Hit()
    {
        if (!_alive)
        {
            return;
        }

        _alive = false;
        if (_spawnData != null)
        {
            _spawnData.Alive = false;
        }

        ApplyDeadVisual();
    }

    private void ApplyDeadVisual()
    {
        if (_mesh != null)
        {
            _mesh.Scale = new Vector3(1f, 0.25f, 1f);
        }
    }

    public static Mob Create(MobSpawnState data)
    {
        var instance = data.Scene.Instantiate<Mob>();
        instance.Position = data.WorldPosition;
        instance.Rotation = new Vector3(0, data.RotationY, 0);
        instance._alive = data.Alive;
        instance._spawnData = data;
        return instance;
    }

}
