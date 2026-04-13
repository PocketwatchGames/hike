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
    StoneSlabBottom,
    StoneSlabTop,
    WoodSlabBottom,
    WoodSlabTop,
    GrassSlabBottom,
    DirtSlabBottom,
    Barrier,
    Water,
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
        { VoxelType.StoneSlabBottom, new Color(0.5f, 0.5f, 0.5f) },
        { VoxelType.StoneSlabTop, new Color(0.5f, 0.5f, 0.5f) },
        { VoxelType.WoodSlabBottom, new Color(0.6f, 0.4f, 0.2f) },
        { VoxelType.WoodSlabTop, new Color(0.6f, 0.4f, 0.2f) },
        { VoxelType.GrassSlabBottom, new Color(0.3f, 0.65f, 0.2f) },
        { VoxelType.DirtSlabBottom, new Color(0.55f, 0.35f, 0.15f) },
        { VoxelType.Water, new Color(0.15f, 0.5f, 0.95f) },
    };

    public static bool IsSolid(VoxelType type)
    {
        return type != VoxelType.Air && type != VoxelType.Water;
    }

    public static bool IsTransparent(VoxelType type)
    {
        return type == VoxelType.Water;
    }

    /// <summary>
    /// Extra light attenuation when light passes through a transparent voxel.
    /// Returns 0 for air (no extra cost), positive for water etc.
    /// Added on top of the normal 1-per-block decay.
    /// </summary>
    public static int LightAttenuation(VoxelType type)
    {
        if (type == VoxelType.Water)
        {
            return 2;
        }
        return 0;
    }

    public static bool IsSlab(VoxelType type)
    {
        return IsBottomSlab(type) || IsTopSlab(type);
    }

    public static bool IsBottomSlab(VoxelType type)
    {
        return type is VoxelType.StoneSlabBottom or VoxelType.WoodSlabBottom
            or VoxelType.GrassSlabBottom or VoxelType.DirtSlabBottom;
    }

    public static bool IsTopSlab(VoxelType type)
    {
        return type is VoxelType.StoneSlabTop or VoxelType.WoodSlabTop;
    }
}
