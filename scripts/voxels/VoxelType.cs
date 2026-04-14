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
        { VoxelType.Stone, new Color(1f, 1f, 1f) },
        { VoxelType.Grass, new Color(1f, 1f, 1f) },
        { VoxelType.Dirt, new Color(1f, 1f, 1f) },
        { VoxelType.Sand, new Color(1f, 1f, 1f) },
        { VoxelType.Wood, new Color(1f, 1f, 1f) },
        { VoxelType.StoneSlabBottom, new Color(1f, 1f, 1f) },
        { VoxelType.StoneSlabTop, new Color(1f, 1f, 1f) },
        { VoxelType.WoodSlabBottom, new Color(1f, 1f, 1f) },
        { VoxelType.WoodSlabTop, new Color(1f, 1f, 1f) },
        { VoxelType.GrassSlabBottom, new Color(1f, 1f, 1f) },
        { VoxelType.DirtSlabBottom, new Color(1f, 1f, 1f) },
        { VoxelType.Water, new Color(0.6f, 0.85f, 1f) },
    };

    // Texture array layer indices. Must match the layer order in
    // res://assets/textures/voxels/voxel_tiles.png (top-to-bottom).
    public const int TILE_STONE = 0;
    public const int TILE_DIRT = 1;
    public const int TILE_GRASS_TOP = 2;
    public const int TILE_GRASS_SIDE = 3;
    public const int TILE_SAND = 4;
    public const int TILE_WOOD_END = 5;
    public const int TILE_WOOD_SIDE = 6;
    public const int TILE_WATER = 7;

    public readonly struct TileFaces
    {
        public readonly int Top;
        public readonly int Side;
        public readonly int Bottom;

        public TileFaces(int top, int side, int bottom)
        {
            Top = top;
            Side = side;
            Bottom = bottom;
        }

        public TileFaces(int all) : this(all, all, all) { }
    }

    public static readonly Dictionary<VoxelType, TileFaces> Tiles = new()
    {
        { VoxelType.Stone, new(TILE_STONE) },
        { VoxelType.Grass, new(TILE_GRASS_TOP, TILE_GRASS_SIDE, TILE_DIRT) },
        { VoxelType.Dirt, new(TILE_DIRT) },
        { VoxelType.Sand, new(TILE_SAND) },
        { VoxelType.Wood, new(TILE_WOOD_END, TILE_WOOD_SIDE, TILE_WOOD_END) },
        { VoxelType.StoneSlabBottom, new(TILE_STONE) },
        { VoxelType.StoneSlabTop, new(TILE_STONE) },
        { VoxelType.WoodSlabBottom, new(TILE_WOOD_END, TILE_WOOD_SIDE, TILE_WOOD_END) },
        { VoxelType.WoodSlabTop, new(TILE_WOOD_END, TILE_WOOD_SIDE, TILE_WOOD_END) },
        { VoxelType.GrassSlabBottom, new(TILE_GRASS_TOP, TILE_GRASS_SIDE, TILE_DIRT) },
        { VoxelType.DirtSlabBottom, new(TILE_DIRT) },
        { VoxelType.Water, new(TILE_WATER) },
    };

    public static int GetTileForFace(VoxelType type, int faceIndex)
    {
        if (!Tiles.TryGetValue(type, out TileFaces faces))
        {
            return 0;
        }
        // faceIndex: 0=Top, 1=Bottom, 2..5=sides
        if (faceIndex == 0)
        {
            return faces.Top;
        }
        if (faceIndex == 1)
        {
            return faces.Bottom;
        }
        return faces.Side;
    }

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
