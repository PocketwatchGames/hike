using Godot;

// A workbench. Using it opens the CraftingScreen: queue learned recipes
// (SimData.recipes) and repairs, and salvage, all paid from and delivered to the
// party stash. The queue lives on the sim state and runs on the in-world clock
// while the bench is out of reach; the node only shows it.
[GlobalClass]
public partial class CraftingBench : Node3D, IInteractive, IWorldEntity
{
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    [Export] private Node3D _hudNode;
    // Over the bench while it works: the job in progress and how far along, or
    // that the stash is full. Hidden with the queue empty.
    [Export] private Label3D _statusLabel;

    private Sim _sim;
    private CraftingBenchSimState _simState;
    // What the label shows now, so it is rebuilt only when that changes.
    private CraftJob _shownJob;
    private int _shownPercent = -1;
    private bool _shownBlocked;

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;
    public CraftingBenchSimState SimState => _simState;

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

    public void Complete(InteractiveAction action)
    {
        GameClient.Current?.OpenCraftingScreen(this);
    }

    public override void _Process(double delta)
    {
        if (_statusLabel == null)
        {
            return;
        }
        CraftQueue queue = _simState?.Queue;
        CraftJob head = queue != null && !queue.IsEmpty && _sim?.WorldState != null ? queue.Jobs[0] : null;
        bool blocked = head != null && queue.HeadBlocked;
        int percent = head != null ? Mathf.FloorToInt(queue.HeadProgress(_sim.WorldClockDays) * 100f) : -1;
        if (head == _shownJob && percent == _shownPercent && blocked == _shownBlocked)
        {
            return;
        }
        _shownJob = head;
        _shownPercent = percent;
        _shownBlocked = blocked;
        _statusLabel.Visible = head != null;
        if (head == null)
        {
            return;
        }
        if (blocked)
        {
            _statusLabel.Text = Loc.Get(Loc.Keys.craft_queue_blocked);
            return;
        }
        ItemState shown = head.Preview();
        string name = _sim.WorldState.SimState?.GetItemDisplayName(shown) ?? shown.data.displayName.ToString();
        _statusLabel.Text = Loc.Format(Loc.Keys.craft_queue_progress, name, percent);
    }

    public static CraftingBench Create(Sim sim, CraftingBenchSimState data)
    {
        var instance = data.Scene.Instantiate<CraftingBench>();
        instance._sim = sim;
        instance._simState = data;
        data.SeatTransform(instance);
        sim.AddChild(instance);
        return instance;
    }
}
