using Godot;

[GlobalClass]
public partial class Well : Node3D, IInteractive, IWorldEntity
{
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    [Export] private Node3D _hudNode;

    private WellSimState _interactiveState;
    private Sim _world;

    public Vector3 hudPosition => _hudNode.GlobalPosition;

    public void OnSpawned(Sim sim) { }

    public bool CanInteract()
    {
        return true;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract();
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    // Wells are repeat-drinkable — completion fires the ApplyStatusEffect event
    // on the InteractiveAction itself, so the well has no per-instance state to
    // mutate here.
    public void Complete(int actionIndex) { }

    public static Well Create(Sim sim, WellSimState data)
    {
        var instance = data.Scene.Instantiate<Well>();
        data.SeatTransform(instance);
        instance._interactiveState = data;
        instance._world = sim;
        sim.AddChild(instance);
        return instance;
    }
}
