using Godot;

public partial class PropInstance : Node3D
{
    public static PropInstance Create(PropGenData data)
    {
        var instance = data.Scene.Instantiate<PropInstance>();
        instance.Position = data.WorldPosition;
        return instance;
    }
}
