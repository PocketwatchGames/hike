using Godot;

public class PropGenData
{
    public readonly PropType Type;
    public readonly Vector3 WorldPosition;
    public readonly PackedScene Scene;

    public PropGenData(PropType type, Vector3 worldPosition, PackedScene scene)
    {
        Type = type;
        WorldPosition = worldPosition;
        Scene = scene;
    }
}
