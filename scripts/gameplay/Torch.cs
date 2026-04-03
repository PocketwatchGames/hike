using Godot;

public partial class Torch : Node3D, IInteractive
{
    private const int PIXELS_PER_UNIT = 20;
    private const int TORCH_LIGHT_EMISSION = 14;

    private bool _active = true;
    private WorldData _worldData;
    private VoxelWorld _voxelWorld;
    private Vector3I _baseWorldPos;
    private Sprite3D _torchSprite;
    private OmniLight3D _light;

    public override void _Ready()
    {
        _torchSprite = GetNode<Sprite3D>("TorchSprite");
        _torchSprite.Texture = CreateTorchTexture();

        _light = GetNode<OmniLight3D>("TorchLight");
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

        // Toggle visual flame appearance
        _torchSprite.Texture = _active ? CreateTorchTexture() : CreateUnlitTorchTexture();

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

    public static Torch Create(InteractiveData data, WorldData worldData, VoxelWorld voxelWorld)
    {
        var scene = GD.Load<PackedScene>("res://scenes/game/torch.tscn");
        var instance = scene.Instantiate<Torch>();
        instance.Position = data.WorldPosition;
        instance._worldData = worldData;
        instance._voxelWorld = voxelWorld;
        instance._baseWorldPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y),
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        return instance;
    }

    private static ImageTexture CreateTorchTexture()
    {
        int width = 1 * PIXELS_PER_UNIT;
        int height = 2 * PIXELS_PER_UNIT;

        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);

        Color stick = new Color(0.5f, 0.35f, 0.15f);
        Color flameCore = new Color(1.0f, 0.9f, 0.3f);
        Color flameOuter = new Color(1.0f, 0.5f, 0.1f);

        int stickWidth = Mathf.Max(width / 5, 2);
        int stickStartX = (width - stickWidth) / 2;
        int stickTopY = height / 3;

        // Draw stick
        for (int x = stickStartX; x < stickStartX + stickWidth; x++)
        {
            for (int y = stickTopY; y < height; y++)
            {
                image.SetPixel(x, y, stick);
            }
        }

        // Draw flame (ellipse at top of stick)
        int flameCenterX = width / 2;
        int flameCenterY = stickTopY - height / 8;
        int flameRadiusX = Mathf.Max(width / 4, 3);
        int flameRadiusY = Mathf.Max(height / 5, 4);
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < stickTopY + 2; y++)
            {
                float dx = (float)(x - flameCenterX) / flameRadiusX;
                float dy = (float)(y - flameCenterY) / flameRadiusY;
                float dist = dx * dx + dy * dy;
                if (dist <= 0.3f)
                {
                    image.SetPixel(x, y, flameCore);
                }
                else if (dist <= 1f)
                {
                    image.SetPixel(x, y, flameOuter);
                }
            }
        }

        return ImageTexture.CreateFromImage(image);
    }

    private static ImageTexture CreateUnlitTorchTexture()
    {
        int width = 1 * PIXELS_PER_UNIT;
        int height = 2 * PIXELS_PER_UNIT;

        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);

        Color stick = new Color(0.5f, 0.35f, 0.15f);
        Color ash = new Color(0.3f, 0.25f, 0.2f);

        int stickWidth = Mathf.Max(width / 5, 2);
        int stickStartX = (width - stickWidth) / 2;
        int stickTopY = height / 3;

        // Draw stick
        for (int x = stickStartX; x < stickStartX + stickWidth; x++)
        {
            for (int y = stickTopY; y < height; y++)
            {
                image.SetPixel(x, y, stick);
            }
        }

        // Draw unlit top (small dark nub)
        for (int x = stickStartX - 1; x <= stickStartX + stickWidth; x++)
        {
            for (int y = stickTopY - 3; y < stickTopY; y++)
            {
                if (x >= 0 && x < width && y >= 0 && y < height)
                {
                    image.SetPixel(x, y, ash);
                }
            }
        }

        return ImageTexture.CreateFromImage(image);
    }
}
