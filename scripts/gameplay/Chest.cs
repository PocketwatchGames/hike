using System;
using Godot;

[GlobalClass]
public partial class Chest : Node3D, IInteractive, IWorldEntity
{
    [Export] private Sprite3D _chestSprite;
    [Export] private Sprite3D _openSprite;
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
    // Optional ITriggerable nodes pinged when the chest finishes opening.
    // Lets a chest fire a poison-cloud deployer, an upstream
    // TriggerSource (e.g. a nearby spike trap's pad chained off the
    // chest open), or any other ITriggerable target. Same-scene
    // NodePath references only.
    [Export] private Godot.Collections.Array<Node> _onOpenTargets = new();
    public Vector3 hudPosition => _hudNode.GlobalPosition;

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
        GD.Print($"Chest hit for {hit.healthDamage} from {hit.source?.Name}");
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
        // Discoverable's dither fade pushed into the sprites' Visibility
        // uniform. Authors who want the fade swap _chestSprite and
        // _openSprite to LitSprite and wire them into the Discoverable's
        // _fadeSprites array.
        _chestSprite.Visible = !_open;
        _openSprite.Visible = _open;
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

        for (int i = 0; i < _interactiveState.LootCount; i++)
        {
            float angle = (float)(rng.NextDouble() * Mathf.Pi * 2f);
            var impulse = new Vector3(
                horizontalSpeed * Mathf.Cos(angle),
                verticalSpeed,
                horizontalSpeed * Mathf.Sin(angle)
            );

            _world.SpawnLoot(_interactiveState.LootScene, GlobalPosition + Vector3.Up, impulse);
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

    public static Chest Create(World world, ChestSimState data)
    {
        var instance = data.Scene.Instantiate<Chest>();
        instance.Position = data.WorldPosition;
        instance._interactiveState = data;
        instance._world = world;
        world.AddChild(instance);

        instance._open = !data.Active;
        instance.UpdateVisuals();

        return instance;
    }
}
