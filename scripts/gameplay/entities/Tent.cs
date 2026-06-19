using Godot;

// Rest interactive. Interacting ("Sleep") fades the screen out, advances world
// time by `_sleepHours`, processes status effects over the skipped span (DoT,
// timed/"till sunrise" effects, buildup decay) on the player and loaded mobs,
// then fades back in. The whole sequence is driven by GameClient.BeginSleep /
// SleepOverlay; the tent itself just kicks it off and carries the tuning.
[GlobalClass]
public partial class Tent : Node3D, IInteractive, IWorldEntity
{
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    [Export] private Node3D _hudNode;
    // In-world hours a single rest advances. 6 by default per the design.
    [Export(PropertyHint.Range, "1,24,1")] private float _sleepHours = 6f;
    // Fraction of max health restored per in-world hour slept. 0.2 → a full
    // 6-hour rest heals to full.
    [Export(PropertyHint.Range, "0,1,0.01")] private float _healFractionPerHour = 0.2f;

    private TentSimState _interactiveState;
    private World _world;

    public Vector3 hudPosition => _hudNode.GlobalPosition;

    public void OnSpawned(World world) { }

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

    // Completion fires OpenInteractive, routing here. The tent has no per-instance
    // state to mutate — it just starts the fade / time-skip on the GameClient.
    public void Complete(int actionIndex)
    {
        GameClient.Current?.BeginSleep(_sleepHours, _healFractionPerHour);
    }

    public static Tent Create(World world, TentSimState data)
    {
        var instance = data.Scene.Instantiate<Tent>();
        instance.Position = data.WorldPosition;
        instance._interactiveState = data;
        instance._world = world;
        world.AddChild(instance);
        return instance;
    }
}
