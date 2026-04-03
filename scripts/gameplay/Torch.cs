using Godot;

public partial class Torch : Node3D, IInteractive
{
    private const int TORCH_LIGHT_EMISSION = 14;

    [Export] private Texture2D LitTexture;
    [Export] private Texture2D UnlitTexture;
    [Export] private Sprite3D _torchSprite;
    [Export] private OmniLight3D _light;

    private bool _active = true;
    private InteractiveData _interactiveData;
    private WorldState _worldData;
    private VoxelWorld _voxelWorld;
    private Vector3I _baseWorldPos;
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
        _interactiveData.Active = _active;

        // Toggle visual flame appearance
        _torchSprite.Texture = _active ? LitTexture : UnlitTexture;

        // Toggle light
        _light.Visible = _active;

        // Update voxel light data and propagate the change
        var positions = new System.Collections.Generic.List<Vector3I> { _baseWorldPos };
        if (_active)
        {
            _worldData.SetBlockLightWorld(_baseWorldPos.X, _baseWorldPos.Y, _baseWorldPos.Z, TORCH_LIGHT_EMISSION);
            _voxelWorld.PropagateLighting(positions);
        }
        else
        {
            // Old light value is still present so removal BFS can read it
            _voxelWorld.UpdateLighting(positions);
        }
    }

    public void RestoreState()
    {
        _active = _interactiveData.Active;
        _torchSprite.Texture = _active ? LitTexture : UnlitTexture;
        _light.Visible = _active;
    }

    public static Torch Create(InteractiveData data, WorldState worldData, VoxelWorld voxelWorld, float spriteYScale)
    {
        var instance = data.Scene.Instantiate<Torch>();
        instance.Position = data.WorldPosition;
        instance._interactiveData = data;
        instance._worldData = worldData;
        instance._voxelWorld = voxelWorld;
        instance._spriteYScale = spriteYScale;
        instance._baseWorldPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y),
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        return instance;
    }

}
