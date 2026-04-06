using System;
using Godot;

[GlobalClass]
public partial class Chest : Node3D, IInteractive
{
    [Export] private Sprite3D _chestSprite;
    [Export] private Sprite3D _openSprite;

    private bool _open;
    private ChestSpawnState _interactiveState;
    private World _world;

    public bool CanInteract()
    {
        return !_open;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract();
    }

    private void UpdateVisuals()
    {
        _chestSprite.Visible = !_open;
        _openSprite.Visible = _open;
    }

    public void Complete()
    {
        _open = true;
        _interactiveState.Active = false;
        UpdateVisuals();

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

            _world.SpawnLoot(_interactiveState.LootScene, GlobalPosition + Vector3.Up, impulse);
        }
    }

    public void RestoreState()
    {
        _open = !_interactiveState.Active;
        UpdateVisuals();
    }

    public static Chest Create(ChestSpawnState data, World world)
    {
        var instance = data.Scene.Instantiate<Chest>();
        instance.Position = data.WorldPosition;
        instance._interactiveState = data;
        instance._world = world;
        instance.UpdateVisuals();
        return instance;
    }
}
