using Godot;

// The sack a fallen party member's gear is left in (DeathSackSimState). Any member
// opens it; the contents spill out as ordinary pickups and the sack — and the grave
// marker the map draws for it — is gone.
[GlobalClass]
public partial class DeathSack : Node3D, IInteractive, IWorldEntity
{
	[Export] private Node3D _hudNode;
	// Authored interaction list; the first entry is the Open action.
	[Export] private Godot.Collections.Array<InteractiveAction> _actions = new();

	private DeathSackSimState _simState;
	private Sim _world;
	private bool _opened;

	public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

	public void OnSpawned(Sim sim) { }

	public bool CanInteract() => !_opened;

	public bool CanActorInteract(Player player) => CanInteract();

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
		if (_opened)
		{
			return;
		}
		_opened = true;
		_world?.OpenDeathSack(this, _simState);
	}

	public static DeathSack Create(Sim sim, DeathSackSimState state)
	{
		var instance = state.Scene.Instantiate<DeathSack>();
		state.SeatTransform(instance);
		instance._simState = state;
		instance._world = sim;
		sim.AddChild(instance);
		return instance;
	}
}
