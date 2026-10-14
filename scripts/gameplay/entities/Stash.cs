using Godot;

// The party stash, reachable away from camp. Using it opens the StashScreen over
// SimState.PartyStash — the same shared store the camp screen's Stash tab edits,
// so every Stash in the world is one storage.
[GlobalClass]
public partial class Stash : Node3D, IInteractive, IWorldEntity
{
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    [Export] private Node3D _hudNode;

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
        GameClient.Current?.OpenStashScreen();
    }

    public static Stash Create(Sim sim, StashSimState data)
    {
        var instance = data.Scene.Instantiate<Stash>();
        data.SeatTransform(instance);
        sim.AddChild(instance);
        return instance;
    }
}
