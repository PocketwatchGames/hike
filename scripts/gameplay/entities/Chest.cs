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
    // When true, this chest is a player stash: interaction opens the
    // StashScreen against Contents instead of ejecting LootItems, and the
    // chest stays openable across visits (Active is not cleared on
    // Complete). Authored on the stash chest scene; loot chest scenes
    // leave this false.
    [Export] private bool _isStash;
    // Optional perception slot. When wired, the chest stays invisible and
    // non-interactable until Discovered — pops to fully visible once the
    // player notices it. No HUD beat (HudScene on the Discoverable should
    // be left null for chests). Leave _discoverable unset for chests that
    // are visible from spawn.
    [Export] private Discoverable _discoverable;
    [Export] private Node3D _hudNode;
    // 3D chest lid hinge: a Node3D pivot placed at the lid's back-bottom edge
    // with the lid mesh parented under it. When set, the lid tweens open on
    // Complete instead of swapping a sprite frame. Null on the old sprite chest.
    [Export] private Node3D _lidHinge;
    [Export] private float _lidOpenAngleDeg = 100f;
    [Export] private float _lidOpenSeconds = 0.4f;
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

    public ChestSimState SimState => _interactiveState;
    public bool IsStash => _isStash;

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

    private void UpdateVisuals(bool animateLid)
    {
        // Open/closed is the only state this method gates — perception
        // visibility (Hidden vs Discovered) is now driven by the
        // Discoverable's dither fade pushed into the sprite's Visibility
        // uniform. _animator is null on the 3D (model) chest, which swings the
        // lid hinge below instead of swapping sprite frames.
        _animator?.Play(_open ? AnimOpen : AnimClosed);
        if (_lidHinge != null)
        {
            float target = _open ? Mathf.DegToRad(_lidOpenAngleDeg) : 0f;
            if (animateLid)
            {
                CreateTween().TweenProperty(_lidHinge, "rotation:x", target, _lidOpenSeconds)
                    .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            }
            else
            {
                Vector3 r = _lidHinge.Rotation;
                r.X = target;
                _lidHinge.Rotation = r;
            }
        }
    }

    public void Complete(int actionIndex)
    {
        if (_isStash)
        {
            // Stash chests don't eject loot and stay reopenable — interaction
            // just hands the chest to the StashScreen, which mutates
            // _interactiveState.Contents directly so changes persist across
            // chunk eviction and save/load.
            GameClient gc = GameClient.Current;
            Player player = gc?.Player;
            if (gc?.stashScreen != null && player != null)
            {
                gc.stashScreen.Open(player, this);
            }
            return;
        }

        _open = true;
        _interactiveState.Active = false;
        UpdateVisuals(true);

        var rng = new Random();
        const float SPEED = 5f;
        float horizontalSpeed = SPEED * Mathf.Cos(Mathf.Pi / 4f);
        float verticalSpeed = SPEED * Mathf.Sin(Mathf.Pi / 4f);

        // Contents are authored on whatever spawns the chest (ChestSpawnEntry,
        // WorldGenData, future editor placements) and arrive through the sim
        // state — the scene itself carries no loot. Each ItemCount ejects as
        // a single stacked Loot so a "5 mushrooms" entry is one pile, not
        // five pickups.
        ItemCount[] lootItems = _interactiveState.LootItems;
        if (lootItems != null)
        {
            for (int i = 0; i < lootItems.Length; i++)
            {
                ItemCount entry = lootItems[i];
                if (entry == null || entry.item == null || entry.count <= 0) { continue; }
                ItemState stack = entry.item.CreateState();
                stack.stackCount = entry.count;
                _world.DropItem(stack, GlobalPosition + Vector3.Up, RandomImpulse(rng, horizontalSpeed, verticalSpeed));
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
        world.AddChild(instance);

        instance._open = !data.Active;
        instance.UpdateVisuals(false);

        return instance;
    }
}
