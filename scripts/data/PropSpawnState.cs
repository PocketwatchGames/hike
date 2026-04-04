using Godot;

public class PropSpawnState
{
    public readonly PropType Type;
    public readonly Vector3 WorldPosition;
    public readonly PackedScene Scene;
    public bool PickedUp;

    public PropSpawnState(PropType type, Vector3 worldPosition, PackedScene scene)
    {
        Type = type;
        WorldPosition = worldPosition;
        Scene = scene;
    }
}
