using System;
using Godot;

public partial class Chest : Node3D, IInteractive
{
    [Export] private Sprite3D _chestSprite;

    private bool _open;
    private InteractiveSpawnState _interactiveState;
    private VoxelWorld _voxelWorld;

    public bool CanInteract()
    {
        return !_open;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract();
    }

    public void Complete()
    {
        _open = true;
        _interactiveState.Active = false;
        _chestSprite.Visible = false;

        var rng = new Random();
        const float SPEED = 5f;
        float horizontalSpeed = SPEED * Mathf.Cos(Mathf.Pi / 4f);
        float verticalSpeed = SPEED * Mathf.Sin(Mathf.Pi / 4f);

        for (int i = 0; i < _interactiveState.LootCount; i++)
        {
            float angle = (float)(rng.NextDouble() * Mathf.Pi * 2f);
            var impulse = new Vector3(
                horizontalSpeed * Mathf.Cos(angle),
                verticalSpeed,
                horizontalSpeed * Mathf.Sin(angle)
            );

            Loot loot = Loot.Create(_interactiveState.LootScene, GlobalPosition + Vector3.Up, impulse);
            GetParent().AddChild(loot);
            _voxelWorld.SetLightMapUniforms(loot);
        }
    }

    public void RestoreState()
    {
        _open = !_interactiveState.Active;
        _chestSprite.Visible = !_open;
    }

    public static Chest Create(InteractiveSpawnState data, VoxelWorld voxelWorld)
    {
        var instance = data.Scene.Instantiate<Chest>();
        instance.Position = data.WorldPosition;
        instance._interactiveState = data;
        instance._voxelWorld = voxelWorld;
        return instance;
    }
}
