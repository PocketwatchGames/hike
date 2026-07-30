using Godot;

// Simple lighting interactive. Tap-interact toggles the flame on/off, flipping
// the animator, light, damage zone, warmth zone, and fx together. Wall torches
// use this class; campfires have their own runtime entity (Campfire).
[GlobalClass]
public partial class Torch : Node3D, IInteractive, IWorldEntity
{
    // Emissive ball sitting in the brazier, shown only while lit. The mesh
    // itself is the whole "is it burning" read on the model — the body
    // material never changes.
    [Export] private Node3D _flame;
    // Where the flame fx sit. Null = this node's origin, which is ground level —
    // right for a campfire, wrong for anything whose flame is up on a head.
    [Export] private Node3D _flameAnchor;
    [Export] private StationaryLight _light;
    [Export] private Node3D _hudNode;
    // Optional burn zone — campfires reuse this scene layout in the editor
    // even though they're spawned as Campfire instances; on a pure wall torch
    // _damageZone is null. Tracks _active so a doused light stops dealing
    // damage.
    [Export] private DamageZone _damageZone;
    // Optional warmth zone — same shape as _damageZone. Tracks _active so
    // a doused light stops drying nearby players.
    [Export] private WarmthZone _warmthZone;
    // Actions shown while lit (Douse) and unlit (Light). Mirrors Campfire's
    // split so the InteractHUD icon flips with the flame.
    [Export] private Godot.Collections.Array<InteractiveAction> _litActions = new();
    [Export] private Godot.Collections.Array<InteractiveAction> _unlitActions = new();
    [Export] private PackedScene _lightOnEffectScene;
    [Export] private PackedScene _lightOffEffectScene;
    [Export] private PackedScene _lightLoopEffectScene;
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    private bool _active = true;
    private TorchSimState _interactiveState;
    private Fx _loopEffect;

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
        Godot.Collections.Array<InteractiveAction> active = _active ? _litActions : _unlitActions;
        return active != null && active.Count > 0 ? active : null;
    }

    public void Complete(int actionIndex)
    {
        _active = !_active;
        _interactiveState.Active = _active;

        UpdateVisuals();
        _light.SetActive(_active);
        _damageZone?.SetActive(_active);
        _warmthZone?.SetActive(_active);

        PackedScene oneShot = _active ? _lightOnEffectScene : _lightOffEffectScene;
        if (oneShot != null)
        {
            Fx.Create(oneShot, GetParent(), Position + (_flameAnchor?.Position ?? Vector3.Zero));
        }
        UpdateLoopEffect();
    }

    private void UpdateLoopEffect()
    {
        if (_active && _loopEffect == null && _lightLoopEffectScene != null)
        {
            _loopEffect = Fx.Create(_lightLoopEffectScene, _flameAnchor ?? this, Vector3.Zero);
        }
        else if (!_active && _loopEffect != null)
        {
            _loopEffect.Stop();
            _loopEffect = null;
        }
    }

    private void UpdateVisuals()
    {
        if (_flame == null)
        {
            GD.PushError($"Torch '{Name}' has no _flame wired");
            return;
        }
        _flame.Visible = _active;
    }

    public static Torch Create(Sim sim, TorchSimState data)
    {
        var instance = data.Scene.Instantiate<Torch>();
        data.SeatTransform(instance);
        instance._interactiveState = data;
        var baseWorldPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y),
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        instance._light.Initialize(sim.WorldState, sim, baseWorldPos);
        sim.AddChild(instance);

        if (data.AutoLightAtNight)
        {
            data.Active = WorldState.IsNight(sim.WorldState.TimeOfDay01);
        }
        instance._active = data.Active;
        instance.UpdateVisuals();
        // Snap to the spawned state — a streaming-in torch shouldn't fade up.
        instance._light.SetActive(instance._active, fade: false);
        instance._damageZone?.SetActive(instance._active);
        instance._warmthZone?.SetActive(instance._active);
        instance.UpdateLoopEffect();

        return instance;
    }
}
