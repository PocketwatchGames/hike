using Godot;

public partial class Door : Node3D, IInteractive
{
    [Export] private Texture2D DoorTexture;
    [Export] private StaticBody3D _blockCollider;
    [Export] private Sprite3D _doorSprite;

    private bool _open;
    private InteractiveSpawnState _interactiveState;
    private WorldState _worldData;
    private VoxelWorld _voxelWorld;
    private Vector3I _baseWorldPos;
    private float _spriteYScale = 1.0f;

    public override void _Ready()
    {
        _doorSprite.Texture = DoorTexture;
        _doorSprite.Scale = new Vector3(1, _spriteYScale, 1);
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
        _open = !_open;
        _interactiveState.Active = !_open;

        // Toggle movement blocker
        _blockCollider.GetNode<CollisionShape3D>("CollisionShape3D").Disabled = _open;

        // Toggle visual
        _doorSprite.Visible = !_open;

        // Update voxel data for light blocking
        VoxelType voxel = _open ? VoxelType.Air : VoxelType.Barrier;
        _worldData.SetVoxelWorld(_baseWorldPos.X, _baseWorldPos.Y, _baseWorldPos.Z, voxel);
        _worldData.SetVoxelWorld(_baseWorldPos.X, _baseWorldPos.Y + 1, _baseWorldPos.Z, voxel);

        // Incremental light update and rebuild nearby chunk meshes
        var changed = new System.Collections.Generic.List<Vector3I>
        {
            _baseWorldPos,
            _baseWorldPos + Vector3I.Up,
        };
        _voxelWorld.RebuildNearbyChunkMeshes(GlobalPosition, changed);
    }

    public void RestoreState()
    {
        _open = !_interactiveState.Active;
        _blockCollider.GetNode<CollisionShape3D>("CollisionShape3D").Disabled = _open;
        _doorSprite.Visible = !_open;
    }

    public static Door Create(InteractiveSpawnState data, WorldState worldData, VoxelWorld voxelWorld, float spriteYScale)
    {
        var instance = data.Scene.Instantiate<Door>();
        instance.Position = data.WorldPosition;
        instance.RotationDegrees = new Vector3(0, Mathf.RadToDeg(data.RotationY), 0);
        instance._interactiveState = data;
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
