using Godot;

public class MobSpawnState
{
    public readonly Vector3 WorldPosition;
    public readonly float RotationY;
    public bool Alive;
    public readonly PackedScene Scene;

    public MobSpawnState(Vector3 worldPosition, float rotationY, PackedScene scene)
    {
        WorldPosition = worldPosition;
        RotationY = rotationY;
        Scene = scene;
        Alive = true;

    }

}
