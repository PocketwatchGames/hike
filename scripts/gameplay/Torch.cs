using Godot;

[GlobalClass]
public partial class Torch : Node3D, IInteractive, IWorldEntity
{
    [Export] private Sprite3D _litSprite;
    [Export] private Sprite3D _unlitSprite;
    [Export] private Light _light;
    [Export] private Node3D _hudNode;
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    private bool _active = true;
    private TorchSimState _interactiveState;

    public override void _Ready()
    {
        UpdateVisuals();
    }

    public void OnSpawned(World world)
    {
        world.SetLightMapUniforms(this);
    }

    public bool CanInteract()
    {
        return true;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract();
    }

    public ulong GetInteractTime(Player player)
    {
        return 0;
    }

    public void Complete()
    {
        _active = !_active;
        _interactiveState.Active = _active;

        UpdateVisuals();
        _light.SetActive(_active);
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
