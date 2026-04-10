using System;
using Godot;

[GlobalClass]
public partial class Chest : Node3D, IInteractive, IWorldEntity
{
    [Export] private Sprite3D _chestSprite;
    [Export] private Sprite3D _openSprite;
    [Export] private HurtBox _hurtBox;
    [Export] private float _interactTime = 3;

    private bool _open;
    private ChestSimState _interactiveState;
    private World _world;

    public override void _Ready()
    {
        if (_hurtBox != null)
        {
            _hurtBox.OnHit = OnHurtBoxHit;
        }
    }

    private void OnHurtBoxHit(DamageData data, Node source)
    {
        GD.Print($"Chest hit for {data?.healthDamage} from {source?.Name}");
    }

    public void OnSpawned(World world)
    {
        world.SetLightMapUniforms(this);
    }

    public bool CanInteract()
    {
        return !_open;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract();
    }

    public ulong GetInteractTime(Player player)
    {
        return (ulong)(_interactTime * 1000);
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

    public static Chest Create(World world, ChestSimState data)
    {
        var instance = data.Scene.Instantiate<Chest>();
        instance.Position = data.WorldPosition;
        instance._interactiveState = data;
        instance._world = world;
        world.AddChild(instance);

        instance._open = !data.Active;
        instance.UpdateVisuals();

        return instance;
    }
}
