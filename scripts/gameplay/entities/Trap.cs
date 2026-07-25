using Godot;

// Generic IInteractive + IWorldEntity wrapper for a "find it, disarm it"
// world object backed by one or more ITriggerable behaviors. Trap holds the
// perception (Discoverable) + interact (InteractiveBox + disarm actions)
// policy; the actual trap effect is whatever ITriggerable nodes the .tscn
// wires up. What makes a "spike trap" vs a "poison gas trap" is the .tscn
// composition — pick a TriggerSource (or omit it for event-driven traps
// fired by chest opens / mob deaths), wire its _targets at one or more
// ITriggerable behaviors, and set them as _triggerSource / _deployers.
[GlobalClass]
public partial class Trap : Node3D, IInteractive, IWorldEntity
{
    // Optional: the body-driven source that fires the trap. Null for
    // event-driven traps (a chest's onOpen pings deployers directly).
    // Disarming the trap calls SetEnabled(false) on the source.
    [Export] private TriggerSource _triggerSource;
    // ITriggerable behaviors the disarm should silence. Stored as Node so
    // authors can wire any class implementing the interface.
    [Export] private Godot.Collections.Array<Node> _deployers = new();
    [Export] private Area3D _interactBox;
    [Export] private Discoverable _discoverable;
    [Export] private Node3D _hudNode;
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    private TrapSimState _simState;
    private Sim _world;
    private bool _disarmed;

    public override void _Ready()
    {
        if (_discoverable != null)
        {
            _discoverable.OnStateChanged += OnDiscoveryStateChanged;
        }
        if (_disarmed)
        {
            ApplyDisarm();
        }
        UpdateInteractEnabled();
    }

    public void OnSpawned(Sim sim)
    {
        _world = sim;
    }

    private void OnDiscoveryStateChanged(EPlayerPerceptionState state)
    {
        UpdateInteractEnabled();
    }

    public bool CanInteract()
    {
        return !_disarmed;
    }

    public bool CanActorInteract(Player player)
    {
        bool discovered = _discoverable == null || _discoverable.IsDiscovered;
        return CanInteract() && discovered;
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
        _disarmed = true;
        if (_simState != null)
        {
            _simState.Disarmed = true;
        }
        ApplyDisarm();
        UpdateInteractEnabled();
    }

    private void ApplyDisarm()
    {
        _triggerSource?.SetEnabled(false);
        if (_deployers != null)
        {
            for (int i = 0; i < _deployers.Count; i++)
            {
                if (_deployers[i] is IDisarmable d)
                {
                    d.Disarm();
                }
            }
        }
    }

    private void UpdateInteractEnabled()
    {
        if (_interactBox == null)
        {
            return;
        }
        bool discovered = _discoverable == null || _discoverable.IsDiscovered;
        bool enabled = !_disarmed && discovered;
        _interactBox.Monitorable = enabled;
        _interactBox.Monitoring = enabled;
    }

    public static Trap Create(Sim sim, TrapSimState data)
    {
        var instance = data.Scene.Instantiate<Trap>();
        instance.Position = data.WorldPosition;
        instance._simState = data;
        instance._world = sim;
        instance._disarmed = data.Disarmed;
        // Scale any deployer that supports it (spike field) to the trap's baked
        // environment tier, so a spike trap is as dangerous as its zone.
        float levelScale = sim.SimData?.LevelOutgoingScale(data.Level) ?? 1f;
        if (levelScale != 1f && instance._deployers != null)
        {
            foreach (Node deployer in instance._deployers)
            {
                if (deployer is SpikeDeployer spikes)
                {
                    spikes.SetLevelScale(levelScale);
                }
            }
        }
        sim.AddChild(instance);
        return instance;
    }
}
