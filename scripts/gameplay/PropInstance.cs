using Godot;

[GlobalClass]
public partial class PropInstance : Node3D, IWorldEntity
{
    public void OnSpawned(World world) { }

    public static PropInstance Create(World world, PropSimState data)
    {
        var instance = data.Scene.Instantiate<PropInstance>();
        instance.Position = data.WorldPosition;
        world.AddChild(instance);
        return instance;
    }
}
