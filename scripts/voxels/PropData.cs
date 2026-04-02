using Godot;

public class PropData
{
    public readonly PropType Type;
    public readonly Vector3 WorldPosition;

    public PropData(PropType type, Vector3 worldPosition)
    {
        Type = type;
        WorldPosition = worldPosition;
    }
}
