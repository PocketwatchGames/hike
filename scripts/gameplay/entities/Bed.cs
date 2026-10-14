using Godot;

// Anything slept in away from a campfire — a bed, a tent. Using it opens the
// SleepScreen (sleep until sunrise, or rest an hour). Whether a sleep is a rest
// is Sim.Sleep's call — it is one exactly when it reaches the sunrise, and a rest
// here makes this spot the party's home (GameClient._home).
[GlobalClass]
public partial class Bed : Node3D, IInteractive, IWorldEntity
{
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    [Export] private Node3D _hudNode;
    // Fraction of max health restored per in-world hour slept.
    [Export(PropertyHint.Range, "0,1,0.01")] private float _healFractionPerHour = 0.2f;

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

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

    public void Complete(int actionIndex)
    {
        GameClient.Current?.OpenSleepScreen(GlobalPosition, _healFractionPerHour);
    }

    public static Bed Create(Sim sim, BedSimState data)
    {
        var instance = data.Scene.Instantiate<Bed>();
        data.SeatTransform(instance);
        sim.AddChild(instance);
        return instance;
    }
}
