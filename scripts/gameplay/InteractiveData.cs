using Godot;

public class InteractiveData
{
    public readonly InteractiveType Type;
    public readonly Vector3 WorldPosition;
    public readonly float RotationY;

    public InteractiveData(InteractiveType type, Vector3 worldPosition, float rotationY)
    {
        Type = type;
        WorldPosition = worldPosition;
        RotationY = rotationY;
    }
}
