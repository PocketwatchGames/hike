using Godot;

[GlobalClass]
public partial class Torch : Node3D, IInteractive, IWorldEntity
{
    [Export] private Sprite3D _litSprite;
    [Export] private Sprite3D _unlitSprite;
    [Export] private Light _light;
    [Export] private Node3D _hudNode;
    // Authored interaction list. Torch interactions are toggles (light /
    // douse) — typically instant.
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    [Export] private PackedScene _lightOnEffectScene;
    [Export] private PackedScene _lightOffEffectScene;
    [Export] private PackedScene _lightLoopEffectScene;
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    private bool _active = true;
    private TorchSimState _interactiveState;
    private Fx _loopEffect;

    public override void _Ready()
    {
        UpdateVisuals();
        UpdateLoopEffect();
    }

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

    public void Complete(int actionIndex)
    {
        _active = !_active;
        _interactiveState.Active = _active;

        UpdateVisuals();
        _light.SetActive(_active);

        PackedScene oneShot = _active ? _lightOnEffectScene : _lightOffEffectScene;
        if (oneShot != null)
        {
            Fx.Create(oneShot, GetParent(), Position);
        }
        UpdateLoopEffect();
    }

    private void UpdateLoopEffect()
    {
        if (_active && _loopEffect == null && _lightLoopEffectScene != null)
        {
            _loopEffect = Fx.Create(_lightLoopEffectScene, this, Vector3.Zero);
        }
        else if (!_active && _loopEffect != null)
        {
            _loopEffect.Stop();
            _loopEffect = null;
        }
    }

    private void UpdateVisuals()
    {
        _litSprite.Visible = _active;
        _unlitSprite.Visible = !_active;
    }

    public static Torch Create(World world, TorchSimState data)
    {
        var instance = data.Scene.Instantiate<Torch>();
        instance.Position = data.WorldPosition;
        instance._interactiveState = data;
        var baseWorldPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y),
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        instance._light.Initialize(world.WorldState, world, baseWorldPos);
        world.AddChild(instance);

        instance._active = data.Active;
        instance.UpdateVisuals();
        instance._light.SetActive(instance._active);

        return instance;
    }
}
