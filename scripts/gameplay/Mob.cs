using System;
using Godot;

[GlobalClass]
public partial class Mob : RigidBody3D
{
    [Export] private CollisionShape3D _collisionShape;
    [Export] private AnimationPlayer _animationPlayer;
    [Export] private bool _alive = true;

    private MobSpawnState _spawnData;

    public override void _Ready()
    {
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
