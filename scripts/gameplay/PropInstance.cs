using Godot;

[GlobalClass]
public partial class PropInstance : Node3D, IWorldEntity
{
    public void OnSpawned(World world)
    {
        world.SetLightMapUniforms(this);
    }

    public static PropInstance Create(World world, PropSpawnState data)
    {
        var instance = data.Scene.Instantiate<PropInstance>();
        instance.Position = data.WorldPosition;
        world.AddChild(instance);
        return instance;
    }
}
