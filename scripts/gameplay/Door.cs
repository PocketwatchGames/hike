using Godot;

public partial class Door : Node3D, IInteractive
{
    private const int PIXELS_PER_UNIT = 20;
    private const int DOOR_WIDTH_UNITS = 1;
    private const int DOOR_HEIGHT_UNITS = 2;

    private bool _open;
    private WorldData _worldData;
    private VoxelWorld _voxelWorld;
    private Vector3I _baseWorldPos;
    private StaticBody3D _blockCollider;
    private Area3D _interactArea;
    private Sprite3D _doorSprite;

    public override void _Ready()
    {
        _blockCollider = GetNode<StaticBody3D>("BlockCollider");
        _interactArea = GetNode<Area3D>("InteractArea");
        _doorSprite = GetNode<Sprite3D>("DoorSprite");
        _doorSprite.Texture = CreateDoorTexture();
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

    public static Door Create(InteractiveData data, WorldData worldData, VoxelWorld voxelWorld)
    {
        var scene = GD.Load<PackedScene>("res://scenes/game/door.tscn");
        var instance = scene.Instantiate<Door>();
        instance.Position = data.WorldPosition;
        instance.RotationDegrees = new Vector3(0, Mathf.RadToDeg(data.RotationY), 0);
        instance._worldData = worldData;
        instance._voxelWorld = voxelWorld;
        instance._baseWorldPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y),
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        return instance;
    }

    private static ImageTexture CreateDoorTexture()
    {
        int width = DOOR_WIDTH_UNITS * PIXELS_PER_UNIT;
        int height = DOOR_HEIGHT_UNITS * PIXELS_PER_UNIT;

        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);

        Color plank = new Color(0.55f, 0.35f, 0.18f);
        Color plankDark = new Color(0.45f, 0.28f, 0.12f);
        Color handle = new Color(0.3f, 0.3f, 0.3f);

        // Fill with planks
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                // Vertical plank lines every 5 pixels
                if (x % 5 == 0)
                {
                    image.SetPixel(x, y, plankDark);
                }
                else
                {
                    image.SetPixel(x, y, plank);
                }
            }
        }

        // Draw horizontal cross beams
        int beamY1 = height / 4;
        int beamY2 = height * 3 / 4;
        for (int x = 0; x < width; x++)
        {
            image.SetPixel(x, beamY1, plankDark);
            image.SetPixel(x, beamY1 + 1, plankDark);
            image.SetPixel(x, beamY2, plankDark);
            image.SetPixel(x, beamY2 + 1, plankDark);
        }

        // Draw handle
        int handleX = width * 3 / 4;
        int handleY = height / 2;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                int px = handleX + dx;
                int py = handleY + dy;
                if (px >= 0 && px < width && py >= 0 && py < height)
                {
                    image.SetPixel(px, py, handle);
                }
            }
        }

        return ImageTexture.CreateFromImage(image);
    }
}
