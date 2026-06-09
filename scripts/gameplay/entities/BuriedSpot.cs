using Godot;

// Runtime node for a buried-item spot. Renders the optional surface hint (or
// dirt mound once dug) under a model anchor and exposes Dig(), called by
// World.TryDig when the player's shovel reaches it. Digging rolls and spawns
// the spot's payload through the normal worldgen spawn path (reused verbatim),
// fires the dig effect, and swaps the visual to the dirt mound. Unlike Chest /
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
    private World _world;
    private Node _visual;

    public BuriedSpotData Data => _state?.Data;
    public bool Excavated => _state != null && _state.Excavated;
    public EDigResult ResultClass => Data != null ? Data.resultClass : EDigResult.Common;

    public void OnSpawned(World world) { }

    public static BuriedSpot Create(World world, BuriedSpotSimState state)
    {
        var instance = state.Scene.Instantiate<BuriedSpot>();
        instance.Position = state.WorldPosition;
        instance._state = state;
        instance._world = world;
        world.AddChild(instance);
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
        PackedScene scene = _state.Excavated ? Data.dirtPileScene : Data.surfaceHintScene;
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
        if (Data.payload != null)
        {
            _world.SpawnEntryImmediate(Data.payload, GlobalPosition, digger);
        }

        if (Data.digEffect != null)
        {
            Fx.Create(Data.digEffect, GetParent(), GlobalPosition);
        }

        _state.Excavated = true;
        UpdateVisual();
        return true;
    }
}
