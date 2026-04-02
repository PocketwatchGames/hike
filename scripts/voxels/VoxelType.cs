using System.Collections.Generic;
using Godot;

public enum VoxelType : byte
{
    Air = 0,
    Stone,
    Grass,
    Dirt,
    Sand,
    Wood,
}

public static class VoxelTypeInfo
{
    public static readonly Dictionary<VoxelType, Color> Colors = new()
    {
        { VoxelType.Stone, new Color(0.5f, 0.5f, 0.5f) },
        { VoxelType.Grass, new Color(0.3f, 0.65f, 0.2f) },
        { VoxelType.Dirt, new Color(0.55f, 0.35f, 0.15f) },
        { VoxelType.Sand, new Color(0.85f, 0.78f, 0.55f) },
        { VoxelType.Wood, new Color(0.6f, 0.4f, 0.2f) },
    };

    public static bool IsSolid(VoxelType type)
    {
        return type != VoxelType.Air;
    }
}
