using Godot;

public partial class Torch : Node3D, IInteractive
{
    [Export] private Texture2D LitTexture;
    [Export] private Texture2D UnlitTexture;
    [Export] private Sprite3D _torchSprite;
    [Export] private Light _light;

    private bool _active = true;
    private InteractiveSpawnState _interactiveState;
    private float _spriteYScale = 1.0f;

    public override void _Ready()
    {
        _torchSprite.Texture = LitTexture;
        _torchSprite.Scale = new Vector3(1, _spriteYScale, 1);
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

        _torchSprite.Texture = _active ? LitTexture : UnlitTexture;
        _light.SetActive(_active);
    }

    public void RestoreState()
    {
        _active = _interactiveState.Active;
        _torchSprite.Texture = _active ? LitTexture : UnlitTexture;
        _light.SetActive(_active);
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
