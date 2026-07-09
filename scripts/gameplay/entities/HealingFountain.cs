using Godot;

// Healing fountain. On interact (while off cooldown) it refills the active
// player's health, then goes inert until the next in-world sunrise — a sim-clock
// deadline persisted on the sim state so the cooldown survives chunk streaming
// and save/load. While ready its basin water is visible and glows red; once used
// the water mesh is hidden until the fountain re-arms at sunrise.
//
// Mirrors Forge's ready/inert daily-cooldown pattern (sans the item-minting
// screen); the visual cue is water visibility rather than an orb material swap.
[GlobalClass]
public partial class HealingFountain : Node3D, IInteractive, IWorldEntity
{
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    [Export] private Discoverable _discoverable;
    [Export] private Node3D _hudNode;
    // Basin water surface — visible + glowing while ready, hidden once used.
    [Export] private MeshInstance3D _waterMesh;
    // Fraction of the player's MaxHealth restored on use (1 = full refill).
    [Export(PropertyHint.Range, "0,1,0.05")] private float _healFraction = 1f;

    private HealingFountainSimState _simState;
    private World _world;
    private bool _visualReady = true;

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    public void OnSpawned(World world) { }

    public override void _ExitTree()
    {
        if (_world != null)
        {
            _world.OnNewDay -= HandleNewDay;
        }
    }

    // The fountain re-arms at sunrise; re-show the water when the day rolls over.
    private void HandleNewDay(int day)
    {
        ApplyReadyVisual(CanInteract());
    }

    // Water shown while ready, hidden once used.
    private void ApplyReadyVisual(bool ready)
    {
        _visualReady = ready;
        if (_waterMesh != null)
        {
            _waterMesh.Visible = ready;
        }
    }

    public bool CanInteract()
    {
        // Inert until the world day reaches the reactivation day (stamped to the
        // next day on use, so the fountain re-arms at sunrise). 0 = ready.
        int today = World.Current?.DayNumber ?? 0;
        return _simState == null || today >= _simState.ReactivateDay;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract() && (_discoverable == null || _discoverable.IsDiscovered);
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
        if (!CanInteract())
        {
            return;
        }
        Player player = GameClient.Current?.Player;
        if (player == null)
        {
            return;
        }
        player.Heal(player.MaxHealth * _healFraction);
        BeginCooldown();
    }

    private void BeginCooldown()
    {
        if (_simState == null)
        {
            return;
        }
        _simState.ReactivateDay = (World.Current?.DayNumber ?? 0) + 1;
        // Hide the water immediately; HandleNewDay restores it at the next sunrise.
        ApplyReadyVisual(false);
    }

    public static HealingFountain Create(World world, HealingFountainSimState data)
    {
        var instance = data.Scene.Instantiate<HealingFountain>();
        instance.Position = data.WorldPosition;
        instance._simState = data;
        instance._world = world;
        world.AddChild(instance);
        // Snap the water to the spawned ready/inert state (no fade on stream-in),
        // then re-show on the sunrise rollover rather than polling each frame.
        instance.ApplyReadyVisual(instance.CanInteract());
        world.OnNewDay += instance.HandleNewDay;
        return instance;
    }
}
