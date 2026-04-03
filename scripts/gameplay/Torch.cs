using Godot;

public partial class Torch : Node3D, IInteractive
{
    [Export] private Sprite3D _litSprite;
    [Export] private Sprite3D _unlitSprite;
    [Export] private Light _light;

    private bool _active = true;
    private InteractiveSpawnState _interactiveState;
    private float _spriteYScale = 1.0f;

    public override void _Ready()
    {
        _litSprite.Scale = new Vector3(1, _spriteYScale, 1);
        _unlitSprite.Scale = new Vector3(1, _spriteYScale, 1);
        UpdateVisuals();
    }

    public bool CanInteract()
    {
        return true;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract();
    }

    public void Complete()
    {
        _active = !_active;
        _interactiveState.Active = _active;

        UpdateVisuals();
        _light.SetActive(_active);
    }

    public void RestoreState()
    {
        _active = _interactiveState.Active;
        UpdateVisuals();
        _light.SetActive(_active);
    }

    private void UpdateVisuals()
    {
        _litSprite.Visible = _active;
        _unlitSprite.Visible = !_active;
    }

    public static Torch Create(InteractiveSpawnState data, WorldState worldData, VoxelWorld voxelWorld, float spriteYScale)
    {
        var instance = data.Scene.Instantiate<Torch>();
        instance.Position = data.WorldPosition;
        instance._interactiveState = data;
        instance._spriteYScale = spriteYScale;
        var baseWorldPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y),
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        instance._light.Initialize(worldData, voxelWorld, baseWorldPos);
        return instance;
    }
}
