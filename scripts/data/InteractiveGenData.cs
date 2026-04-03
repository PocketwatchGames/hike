using Godot;

public class InteractiveGenData
{
    public readonly InteractiveType Type;
    public readonly Vector3 WorldPosition;
    public readonly float RotationY;
    public readonly PackedScene Scene;

    public InteractiveGenData(InteractiveType type, Vector3 worldPosition, float rotationY, PackedScene scene)
    {
        Type = type;
        WorldPosition = worldPosition;
        RotationY = rotationY;
        Scene = scene;
    }
}
