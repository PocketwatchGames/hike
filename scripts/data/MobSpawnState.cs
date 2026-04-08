using Godot;

public class MobSpawnState
{
    public readonly Vector3 WorldPosition;
    public readonly float RotationY;
    public bool Alive;
    public float MaxHealth;
    public float Health;
    public float Aggro;
    public readonly PackedScene Scene;

    public MobSpawnState(Vector3 worldPosition, float rotationY, PackedScene scene)
    {
        WorldPosition = worldPosition;
        RotationY = rotationY;
        Scene = scene;
        Alive = true;
        MaxHealth = 1f;
        Health = 1f;
        Aggro = 0f;
    }

}
