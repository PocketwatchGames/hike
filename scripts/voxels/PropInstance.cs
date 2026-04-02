using Godot;

public partial class PropInstance : Node3D
{
    public static PropInstance Create(PropData data)
    {
        var instance = new PropInstance();
        instance.Position = data.WorldPosition;

        PropDefinition def = PropDefinition.Definitions[data.Type];

        // Static body with collision
        var body = new StaticBody3D();
        body.Position = def.CollisionOffset;

        Shape3D shape = CreateShape(def);
        var collisionShape = new CollisionShape3D();
        collisionShape.Shape = shape;
        body.AddChild(collisionShape);
        instance.AddChild(body);

        // Billboarded sprite
        var sprite = new Sprite3D();
        sprite.Billboard = BaseMaterial3D.BillboardModeEnum.FixedY;
        sprite.PixelSize = 0.05f;
        sprite.Position = new Vector3(0f, def.SpriteSize.Y / 2f, 0f);
        sprite.Texture = CreatePlaceholderTexture(def);
        instance.AddChild(sprite);

        return instance;
    }

    private static Shape3D CreateShape(PropDefinition def)
    {
        switch (def.ShapeType)
        {
            case PropDefinition.CollisionShapeType.Cylinder:
                var cylinder = new CylinderShape3D();
                cylinder.Radius = def.CollisionSize.X / 2f;
                cylinder.Height = def.CollisionSize.Y;
                return cylinder;
            case PropDefinition.CollisionShapeType.Box:
                var box = new BoxShape3D();
                box.Size = def.CollisionSize;
                return box;
            default:
                var fallback = new BoxShape3D();
                fallback.Size = def.CollisionSize;
                return fallback;
        }
    }

    private static ImageTexture CreatePlaceholderTexture(PropDefinition def)
    {
        const int PIXELS_PER_UNIT = 20;
        int width = (int)(def.SpriteSize.X * PIXELS_PER_UNIT);
        int height = (int)(def.SpriteSize.Y * PIXELS_PER_UNIT);

        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);

        Color trunk = new Color(0.45f, 0.3f, 0.15f);
        Color leaves = def.SpriteColor;

        int trunkWidth = width / 6;
        int trunkStartX = (width - trunkWidth) / 2;
        int trunkStartY = height / 2;

        // Draw trunk
        for (int x = trunkStartX; x < trunkStartX + trunkWidth; x++)
        {
            for (int y = trunkStartY; y < height; y++)
            {
                image.SetPixel(x, y, trunk);
            }
        }

        // Draw canopy (ellipse in upper half)
        int canopyCenterX = width / 2;
        int canopyCenterY = height / 3;
        int canopyRadiusX = width / 2 - 2;
        int canopyRadiusY = height / 3;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < trunkStartY + 4; y++)
            {
                float dx = (float)(x - canopyCenterX) / canopyRadiusX;
                float dy = (float)(y - canopyCenterY) / canopyRadiusY;
                if (dx * dx + dy * dy <= 1f)
                {
                    image.SetPixel(x, y, leaves);
                }
            }
        }

        return ImageTexture.CreateFromImage(image);
    }
}
