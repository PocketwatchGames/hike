using System.Collections.Generic;
using Godot;

public class PropDefinition
{
    public enum CollisionShapeType
    {
        Cylinder,
        Box,
    }

    public CollisionShapeType ShapeType;
    public Vector3 CollisionSize;
    public Vector3 CollisionOffset;
    public Vector3 SpriteSize;
    public Color SpriteColor;

    public static readonly Dictionary<PropType, PropDefinition> Definitions = new()
    {
        [PropType.Tree] = new PropDefinition
        {
            ShapeType = CollisionShapeType.Cylinder,
            CollisionSize = new Vector3(1f, 5f, 1f),
            CollisionOffset = new Vector3(0f, 2.5f, 0f),
            SpriteSize = new Vector3(4f, 6f, 1f),
            SpriteColor = new Color(0.15f, 0.5f, 0.1f),
        },
    };
}
