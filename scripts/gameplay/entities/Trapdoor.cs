using Godot;

// Player-operated trapdoor: a floor leaf the player toggles by interacting, and
// that a linked Lever can toggle from across the room. All the physical work
// lives on the TrapdoorPanel; this host is the IInteractive/serialization shell
// around it, mirroring Door (a horizontal leaf instead of a swinging wall).
[GlobalClass]
public partial class Trapdoor : Node3D, IInteractive, IWorldEntity, ITriggerable
{
    [Export] private TrapdoorPanel _panel;
    [Export] private Node3D _hudNode;
    // Authored interaction list — typically one instant Open/Close toggle.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    // Shared key that links this trapdoor to a Lever. Empty = not lever-driven
    // (player-operated only). A lever pulls every loaded Trapdoor whose LinkTag
    // matches its target.
    public string LinkTag => _simState?.LinkTag ?? "";

    private TrapdoorSimState _simState;

    public void OnSpawned(Sim sim) { }

    public bool CanInteract()
    {
        return true;
    }

    public bool CanActorInteract(Player player)
    {
        return true;
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    public void Complete(int actionIndex)
    {
        Toggle();
    }

    // Lever-driven toggle. Same effect as a player interaction, so one lever can
    // both open and close its trapdoor.
    public void Trigger(Node source)
    {
        Toggle();
    }

    private void Toggle()
    {
        if (_panel == null)
        {
            return;
        }
        _panel.Toggle();
        if (_simState != null)
        {
            _simState.Open = _panel.IsOpen;
        }
    }

    public static Trapdoor Create(Sim sim, TrapdoorSimState data)
    {
        var instance = data.Scene.Instantiate<Trapdoor>();
        data.SeatTransform(instance);
        instance._simState = data;
        sim.AddChild(instance);
        // Seat the persisted open/closed state without animating (a reloaded
        // world snaps to how the player left it).
        instance._panel?.SetOpen(data.Open, animate: false);
        return instance;
    }
}
