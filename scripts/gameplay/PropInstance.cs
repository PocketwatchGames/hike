using Godot;

[GlobalClass]
public partial class PropInstance : Node3D
{
    public static PropInstance Create(PropSpawnState data)
    {
        var instance = data.Scene.Instantiate<PropInstance>();
        instance.Position = data.WorldPosition;
        return instance;
    }
}
