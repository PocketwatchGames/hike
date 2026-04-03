using Godot;

public partial class PropInstance : Node3D
{
    public static PropInstance Create(PropGenData data, float spriteYScale)
    {
        var instance = data.Scene.Instantiate<PropInstance>();
        instance.Position = data.WorldPosition;
        foreach (Node child in instance.GetChildren())
        {
            if (child is Sprite3D sprite)
            {
                sprite.Scale = new Vector3(1, spriteYScale, 1);
                break;
            }
        }
        return instance;
    }
}
