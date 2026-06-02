using System;
using Godot;

[GlobalClass]
public partial class BerryTree : Node3D, IInteractive, IWorldEntity
{
    [Export] private Node3D _berries;
    [Export] private HurtBox _hurtBox;
    [Export] private Node3D _hudNode;
    // Authored interaction list. Berry trees ship with a single "Pick"
    // action; the array shape stays for parity with other interactives so
    // future variants (e.g. shake-tree alt action) can append entries.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    // Item spawned for each berry. Tied to the tree species (apple tree →
    // apples, blackberry bush → blackberries) so it lives on the .tscn rather
    // than the per-instance sim state. Typed ItemData (not LootData) so any
    // droppable item works here; berries author themselves as LootData for
    // future spoilage/decay behavior.
    [Export] private ItemData _berryItem;
    [Export] private float _lootSpeed = 10;
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    private bool _picked;
    private BerryTreeSimState _interactiveState;
    private World _world;

    public override void _Ready()
    {
        if (_hurtBox != null)
        {
            _hurtBox.OnHit = OnHurtBoxHit;
            _hurtBox.GetHitType = _ => EHitResult.Object;
        }
    }

    private void OnHurtBoxHit(HitInfo hit)
    {
        if (_picked)
        {
            return;
        }
        Pick();
    }

    public void OnSpawned(World world) { }

    public bool CanInteract()
    {
        return !_picked;
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
        _picked = true;
        _interactiveState.Picked = true;
        UpdateVisuals();

        // Hurtbox off so subsequent sword swings pass through the bare tree.
        if (_hurtBox != null)
        {
            _hurtBox.Monitorable = false;
            _hurtBox.Monitoring = false;
        }

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

    private void UpdateVisuals()
    {
        _berries.Visible = !_picked;
    }

    public static BerryTree Create(World world, BerryTreeSimState data)
    {
        var instance = data.Scene.Instantiate<BerryTree>();
        instance.Position = data.WorldPosition;
        instance._interactiveState = data;
        instance._world = world;
        world.AddChild(instance);

        instance._picked = data.Picked;
        instance.UpdateVisuals();

        if (instance._picked && instance._hurtBox != null)
        {
            instance._hurtBox.Monitorable = false;
            instance._hurtBox.Monitoring = false;
        }

        return instance;
    }
}
