using Godot;

[GlobalClass]
public partial class Door : Node3D, IInteractive, IWorldEntity
{
    [Export] private Texture2D DoorTexture;
    [Export] private StaticBody3D _blockCollider;
    [Export] private Sprite3D _doorSprite;
    [Export] private HurtBox _hurtBox;
    [Export] private Node3D _hudNode;
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    private bool _open;
    private DoorSimState _interactiveState;
    private WorldState _worldData;
    private World _world;
    private Vector3I _baseWorldPos;

    public override void _Ready()
    {
        _doorSprite.Texture = DoorTexture;

        if (_hurtBox != null)
        {
            _hurtBox.OnHit = OnHurtBoxHit;
        }
    }

    private void OnHurtBoxHit(DamageData data, Node source)
    {
        GD.Print($"Door hit for {data?.healthDamage} from {source?.Name}");
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
        _world.RebuildNearbyChunkMeshes(GlobalPosition, changed);
    }

    public static Door Create(World world, DoorSimState data)
    {
        var instance = data.Scene.Instantiate<Door>();
        instance.Position = data.WorldPosition;
        instance.RotationDegrees = new Vector3(0, Mathf.RadToDeg(data.RotationY), 0);
        instance._interactiveState = data;
        instance._worldData = world.WorldState;
        instance._world = world;
        instance._baseWorldPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y),
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        world.AddChild(instance);

        instance._open = !data.Active;
        instance._blockCollider.GetNode<CollisionShape3D>("CollisionShape3D").Disabled = instance._open;
        instance._doorSprite.Visible = !instance._open;

        return instance;
    }

}
