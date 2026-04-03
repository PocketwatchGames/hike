using Godot;

public class InteractiveData
{
    public readonly InteractiveType Type;
    public readonly Vector3 WorldPosition;
    public readonly float RotationY;
    public readonly PackedScene Scene;
    public bool Active = true;

    public InteractiveData(InteractiveType type, Vector3 worldPosition, float rotationY, PackedScene scene)
    {
        Type = type;
        WorldPosition = worldPosition;
        RotationY = rotationY;
        Scene = scene;
    }
}
