using System;
using Godot;

[GlobalClass]
public partial class BerryTree : Node3D, IInteractive, IWorldEntity
{
    [Export] private Node3D _berries;
    [Export] private HurtBox _hurtBox;
    [Export] private Node3D _hudNode;
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    // Item spawned for each berry. Tied to the tree species (apple tree →
    // apples, blackberry bush → blackberries) so it lives on the .tscn rather
    // than the per-instance sim state.
    [Export] private ItemData _berryItem;
    // In-world days a picked bush takes to bear fruit again. Species-tied, so it
    // lives on the scene alongside _berryItem.
    [Export(PropertyHint.Range, "1,60,1,or_greater")] private int _regrowDays = 3;
    [Export] private float _lootSpeed = 10;
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    private BerryTreeSimState _interactiveState;
    private World _world;

    // Bare while the world day is below the regrow deadline; ripe once reached.
    private bool IsRipe => _interactiveState == null
        || _interactiveState.IsRegrown(_world?.DayNumber ?? 0);

    public override void _Ready()
    {
        if (_hurtBox != null)
        {
            _hurtBox.OnHit = OnHurtBoxHit;
            _hurtBox.PredictHit = _ => new HitPrediction(EHitResult.Object, EDamageTriggerFlags.None);
        }
    }

    public override void _ExitTree()
    {
        if (_world != null)
        {
            _world.OnNewDay -= HandleNewDay;
        }
    }

    // Bushes regrow at sunrise: re-show the fruit + re-arm the hurtbox once the
    // day rolls past the regrow deadline.
    private void HandleNewDay(int day)
    {
        ApplyRipeState(IsRipe);
    }

    private void OnHurtBoxHit(HitInfo hit)
    {
        if (!IsRipe)
        {
            return;
        }
        Pick();
    }

    public void OnSpawned(World world) { }

    public bool CanInteract()
    {
        return IsRipe;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract();
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        if (!CanActorInteract(player))
        {
            return null;
        }
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    public void Complete(int actionIndex)
    {
        Pick();
    }

    private void Pick()
    {
        if (!IsRipe || _interactiveState == null || _world == null)
        {
            return;
        }
        _interactiveState.RegrowDay = _world.DayNumber + Mathf.Max(1, _regrowDays);
        ApplyRipeState(false);

        var rng = new Random();
        float horizontalSpeed = _lootSpeed * Mathf.Cos(Mathf.Pi / 4f);
        float verticalSpeed = _lootSpeed * Mathf.Sin(Mathf.Pi / 4f);

        for (int i = 0; i < _interactiveState.BerryCount; i++)
        {
            float angle = (float)(rng.NextDouble() * Mathf.Pi * 2f);
            var impulse = new Vector3(
                horizontalSpeed * Mathf.Cos(angle),
                verticalSpeed,
                horizontalSpeed * Mathf.Sin(angle)
            );

            _world.SpawnLoot(GlobalPosition + Vector3.Up, impulse, _berryItem);
        }
    }

    // Show/hide the fruit and gate the hurtbox so sword swings only knock berries
    // off a ripe bush.
    private void ApplyRipeState(bool ripe)
    {
        if (_berries != null)
        {
            _berries.Visible = ripe;
        }
        if (_hurtBox != null)
        {
            _hurtBox.Monitorable = ripe;
            _hurtBox.Monitoring = ripe;
        }
    }

    public static BerryTree Create(World world, BerryTreeSimState data)
    {
        var instance = data.Scene.Instantiate<BerryTree>();
        instance.Position = data.WorldPosition;
        instance._interactiveState = data;
        instance._world = world;
        world.AddChild(instance);

        instance.ApplyRipeState(instance.IsRipe);
        world.OnNewDay += instance.HandleNewDay;

        return instance;
    }
}
