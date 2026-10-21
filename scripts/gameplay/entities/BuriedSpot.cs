using Godot;

// Runtime node for a buried-item spot. Renders the optional surface hint (or
// dirt mound once dug) under a model anchor. Digging sprays the buried item out,
// spawns any buried entity through the normal spawn path (reused verbatim), fires
// the style's dig effect, and swaps the visual to the dirt mound.
//
// Digging is ONLY this interaction: a member carrying a Digging tool within
// `_digReach` of the spot gets a Dig prompt. A treasure spot (one with a
// TreasureName) prompts only while the party holds its map, so it stays
// invisible until then.
[GlobalClass]
public partial class BuriedSpot : Node3D, IWorldEntity, IInteractive
{
    // Where the hint / dirt-mound visual is instanced. Authored on
    // buried_spot.tscn so the spot's transform owns any ground offset.
    [Export] private Node3D _modelAnchor;
    // The Dig action (needs a Digging tool carried).
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    // Horizontal metres from the spot within which it prompts. The
    // InteractiveBox must reach at least this far past the player's interact area.
    [Export(PropertyHint.Range, "0.25,4,0.05")] private float _digReach = 1f;
    [Export(PropertyHint.Range, "0,2,0.05")] private float _hudHeight = 0.5f;

    private BuriedSpotSimState _state;
    private Sim _world;
    private Node _visual;

    public BuriedSpotStyleData Style => _state?.Style;
    public bool Excavated => _state != null && _state.Excavated;

    public void OnSpawned(Sim sim) { }

    public Vector3 hudPosition => GlobalPosition + Vector3.Up * _hudHeight;

    public bool CanInteract()
    {
        if (_state == null || _state.Excavated)
        {
            return false;
        }
        return string.IsNullOrEmpty(_state.TreasureName)
            || (_world?.WorldState?.SimState?.HasTreasureMapAt(GlobalPosition) ?? false);
    }

    public bool CanActorInteract(Player player)
    {
        if (player == null || !CanInteract())
        {
            return false;
        }
        Vector3 offset = player.GlobalPosition - GlobalPosition;
        offset.Y = 0f;
        return offset.LengthSquared() <= _digReach * _digReach
            && _actions.Count > 0 && _actions[0].IsOfferedTo(player);
    }

    // A spot is found by walking onto it, not by spotting it through walls.
    public bool ShouldShowXray() => false;

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        return CanActorInteract(player) ? _actions : null;
    }

    public void Complete(InteractiveAction action)
    {
        Player player = GameClient.Current?.Player;
        if (player != null && !Excavated && player.UseTool(EToolKind.Digging))
        {
            Dig(player);
        }
    }

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
            _world.EjectLoot(_state.Item, _state.Count, GlobalPosition + Vector3.Up);
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
