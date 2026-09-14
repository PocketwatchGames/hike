using Godot;

// Runtime node for a buried-item spot. Renders the optional surface hint (or
// dirt mound once dug) under a model anchor and exposes Dig(), called by
// Sim.TryDig when the player's shovel reaches it. Digging sprays the buried
// item out, spawns any buried entity through the normal spawn path (reused
// verbatim), fires the dig effect, and swaps the visual to the dirt mound. Unlike Chest /
// Loot this is not an IInteractive: there is no walk-up prompt because a
// no-hint treasure spot is invisible — the shovel consumable drives the dig
// and locates spots by proximity.
[GlobalClass]
public partial class BuriedSpot : Node3D, IWorldEntity
{
    // Where the hint / dirt-mound visual is instanced. Authored on
    // buried_spot.tscn so the spot's transform owns any ground offset.
    [Export] private Node3D _modelAnchor;

    private BuriedSpotSimState _state;
    private Sim _world;
    private Node _visual;

    public BuriedSpotStyleData Style => _state?.Style;
    public bool Excavated => _state != null && _state.Excavated;
    public EDigResult ResultClass => Style != null ? Style.resultClass : EDigResult.Common;

    public void OnSpawned(Sim sim) { }

    public static BuriedSpot Create(Sim sim, BuriedSpotSimState state)
    {
        var instance = state.Scene.Instantiate<BuriedSpot>();
        state.SeatTransform(instance);
        instance._state = state;
        instance._world = sim;
        sim.AddChild(instance);
        instance.UpdateVisual();
        return instance;
    }

    private void UpdateVisual()
    {
        if (_modelAnchor == null)
        {
            return;
        }
        if (_visual != null)
        {
            _visual.QueueFree();
            _visual = null;
        }
        PackedScene scene = _state.Excavated ? Style.dirtPileScene : Style.surfaceHintScene;
        if (scene != null)
        {
            _visual = scene.Instantiate();
            _modelAnchor.AddChild(_visual);
        }
    }

    // Excavate this spot. Returns false if already dug. `digger` is alerted /
    // emerged if the payload is (or reveals) a mob.
    public bool Dig(Player digger)
    {
        if (_state == null || _state.Excavated)
        {
            return false;
        }

        // Roll + spawn the payload at this spot, materialized into the live
        // scene immediately (the player is standing here). SpawnEntryImmediate
        // forwards `digger` so a dug-up mob emerges and aggros.
        if (_state.Payload != null)
        {
            _world.SpawnEntryImmediate(_state.Payload, GlobalPosition, digger);
        }

        // Pop the buried item out of the hole exactly like a chest ejects contents.
        if (_state.Item != null)
        {
            ItemState stack = _state.Item.CreateState();
            stack.SetCount(_state.Count);
            _world.EjectLoot(stack, GlobalPosition + Vector3.Up);
        }

        if (Style.digEffect != null)
        {
            Fx.Create(Style.digEffect, GetParent(), GlobalPosition);
        }

        _state.Excavated = true;
        UpdateVisual();

        // A treasure spot: drop its registry anchor and any map pointing here, so
        // the map self-destructs once its treasure is unearthed (dug with or
        // without a map in hand).
        if (!string.IsNullOrEmpty(_state.TreasureName))
        {
            _world.WorldState?.TreasureSpots.Remove(_state.TreasureName);
        }
        _world.WorldState?.SimState?.RemoveTreasureMapAt(GlobalPosition);
        return true;
    }
}
