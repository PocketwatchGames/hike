using System;
using Godot;

[GlobalClass]
public partial class BerryTree : Node3D, IInteractive, IWorldEntity
{
    [Export] private Node3D _berries;
    // Struck-to-destroy is the component's job (hurtbox, effect, removal); the
    // bush only supplies what falls out of it, since the berry count is
    // per-instance sim state rather than something the scene can author.
    [Export] private Destructible _destructible;
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
    private Sim _world;

    // Bare while the world day is below the regrow deadline; ripe once reached.
    private bool IsRipe => _interactiveState == null
        || _interactiveState.IsRegrown(_world?.DayNumber ?? 0);

    public override void _Ready()
    {
        if (_destructible != null)
        {
            _destructible.Destroyed += OnDestroyed;
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

    // Smashed rather than picked: whatever fruit is on the bush comes off as it
    // goes. A bare bush still breaks, it just yields nothing — the payload is
    // gated on ripeness, the destruction isn't.
    private void OnDestroyed()
    {
        if (IsRipe)
        {
            EjectBerries();
        }
    }

    public void OnSpawned(Sim sim) { }

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
        EjectBerries();
    }

    // Pop this bush's fruit onto the ground. Shared by picking (which leaves the
    // bush standing to regrow) and by being smashed (which doesn't).
    private void EjectBerries()
    {
        if (_interactiveState == null || _world == null)
        {
            return;
        }
        var rng = new Random();
        for (int i = 0; i < _interactiveState.BerryCount; i++)
        {
            _world.SpawnLoot(GlobalPosition + Vector3.Up,
                Destructible.RandomEjectImpulse(rng, _lootSpeed), _berryItem);
        }
    }

    // Show/hide the fruit. The hurtbox is NOT gated on ripeness — a picked bush
    // is still a bush you can cut down, it just has nothing left to drop.
    private void ApplyRipeState(bool ripe)
    {
        if (_berries != null)
        {
            _berries.Visible = ripe;
        }
    }

    public static BerryTree Create(Sim sim, BerryTreeSimState data)
    {
        var instance = data.Scene.Instantiate<BerryTree>();
        data.SeatTransform(instance);
        instance._interactiveState = data;
        instance._world = sim;
        sim.AddChild(instance);

        instance.ApplyRipeState(instance.IsRipe);
        sim.OnNewDay += instance.HandleNewDay;

        return instance;
    }
}
