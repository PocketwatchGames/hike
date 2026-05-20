using System;
using Godot;

[GlobalClass]
public partial class Chest : Node3D, IInteractive, IWorldEntity
{
    [Export] private LitSpriteAnimator _animator;
    [Export] private HurtBox _hurtBox;
    // Authored interaction list. The first entry is the default action the
    // player runs on press; lockpick / break can be authored as additional
    // entries for the radial UI.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    // Optional perception slot. When wired, the chest stays invisible and
    // non-interactable until Discovered — pops to fully visible once the
    // player notices it. No HUD beat (HudScene on the Discoverable should
    // be left null for chests). Leave _discoverable unset for chests that
    // are visible from spawn.
    [Export] private Discoverable _discoverable;
    [Export] private Node3D _hudNode;
    // Item the chest drops. Authored on the chest .tscn so each chest variant
    // can drop a different item without touching the sim state. The Loot
    // scene decides at run time whether the player auto-picks up (existing
    // same-kind stack with room) or has to press Interact.
    [Export] private ItemData _lootItem;
    // Additional one-of-each loot. Drops alongside the LootCount × _lootItem
    // stack so a chest can carry both bulk items (mushrooms, coins) and
    // unique pickups (scrolls, key items). Authored on the .tscn rather
    // than on the sim state — sim state still controls the LootCount stack
    // size for _lootItem, but a chest's identity (a "scroll chest") is a
    // scene-level decision. Leave empty for legacy single-item chests.
    [Export] private Godot.Collections.Array<ItemData> _lootItems = new();
    // Optional ITriggerable nodes pinged when the chest finishes opening.
    // Lets a chest fire a poison-cloud deployer, an upstream
    // TriggerSource (e.g. a nearby spike trap's pad chained off the
    // chest open), or any other ITriggerable target. Same-scene
    // NodePath references only.
    [Export] private Godot.Collections.Array<Node> _onOpenTargets = new();
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    private static readonly StringName AnimOpen = "open";
    private static readonly StringName AnimClosed = "closed";

    private bool _open;
    private ChestSimState _interactiveState;
    private World _world;

    public override void _Ready()
    {
        if (_hurtBox != null)
        {
            _hurtBox.OnHit = OnHurtBoxHit;
            _hurtBox.GetHitType = _ => EHitResult.Object;
        }
    }

    private void OnHurtBoxHit(HitInfo hit)
    {
    }

    public void OnSpawned(World world) { }

    public bool CanInteract()
    {
        return !_open;
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

    private void UpdateVisuals()
    {
        // Open/closed is the only state this method gates — perception
        // visibility (Hidden vs Discovered) is now driven by the
        // Discoverable's dither fade pushed into the sprite's Visibility
        // uniform.
        _animator.Play(_open ? AnimOpen : AnimClosed);
    }

    public void Complete(int actionIndex)
    {
        _open = true;
        _interactiveState.Active = false;
        UpdateVisuals();

        var rng = new Random();
        const float SPEED = 5f;
        float horizontalSpeed = SPEED * Mathf.Cos(Mathf.Pi / 4f);
        float verticalSpeed = SPEED * Mathf.Sin(Mathf.Pi / 4f);

        if (_lootItem != null)
        {
            for (int i = 0; i < _interactiveState.LootCount; i++)
            {
                _world.SpawnLoot(GlobalPosition + Vector3.Up, RandomImpulse(rng, horizontalSpeed, verticalSpeed), _lootItem);
            }
        }
        if (_lootItems != null)
        {
            for (int i = 0; i < _lootItems.Count; i++)
            {
                ItemData item = _lootItems[i];
                if (item == null) { continue; }
                _world.SpawnLoot(GlobalPosition + Vector3.Up, RandomImpulse(rng, horizontalSpeed, verticalSpeed), item);
            }
        }

        // Fire any wired traps/effects. The chest itself is the source —
        // ITriggerables that need body-area context (a SpikeDeployer)
        // should be wired indirectly through a TriggerSource (the chain
        // chest → TriggerSource → SpikeDeployer). Targets that don't
        // (a poison cloud, a mob spawner) consume the chest directly.
        if (_onOpenTargets != null)
        {
            for (int i = 0; i < _onOpenTargets.Count; i++)
            {
                if (_onOpenTargets[i] is ITriggerable t)
                {
                    t.Trigger(this);
                }
            }
        }
    }

    private static Vector3 RandomImpulse(Random rng, float horizontalSpeed, float verticalSpeed)
    {
        float angle = (float)(rng.NextDouble() * Mathf.Pi * 2f);
        return new Vector3(
            horizontalSpeed * Mathf.Cos(angle),
            verticalSpeed,
            horizontalSpeed * Mathf.Sin(angle)
        );
    }

    public static Chest Create(World world, ChestSimState data)
    {
        var instance = data.Scene.Instantiate<Chest>();
        instance.Position = data.WorldPosition;
        instance._interactiveState = data;
        instance._world = world;
        // Apply SimState's per-instance loot override (worldgen-authored
        // drop list). Replaces the scene's _lootItems entirely so the
        // override is the authoritative list — partial appending would
        // surprise placement-driven setups whose intent is "this chest
        // drops exactly these items, not these plus whatever the scene
        // had."
        if (data.LootItems != null && data.LootItems.Length > 0)
        {
            instance._lootItems = new Godot.Collections.Array<ItemData>();
            for (int i = 0; i < data.LootItems.Length; i++)
            {
                instance._lootItems.Add(data.LootItems[i]);
            }
        }
        world.AddChild(instance);

        instance._open = !data.Active;
        instance.UpdateVisuals();

        return instance;
    }
}
