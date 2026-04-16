using System.Collections.Generic;
using Godot;

public enum VoxelType : byte
{
    Air = 0,
    Stone = 1,
    Grass = 2,
    Dirt = 3,
    Sand = 4,
    Wood = 5,
    Barrier = 6,
    Water = 7,
    // "Auto" terrain: shader picks tile from surface slope (grass/dirt/stone)
    // and the mesher overrides to sand near water. Use this for natural land
    // instead of Grass/Dirt/Stone/Sand, which are kept as authored overrides.
    Terrain = 8,
    // "Auto" terrain variant for path bands. Same water→sand override, but
    // shader never picks grass: any slope reads as dirt; only steep cliffs
    // become stone. Used for the smooth slopes WorldGen carves between
    // quantized plateaus.
    TerrainPath = 9,
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
        { VoxelType.Water, new Color(0.6f, 0.85f, 1f) },
        { VoxelType.Terrain, new Color(1f, 1f, 1f) },
        { VoxelType.TerrainPath, new Color(1f, 1f, 1f) },
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
    // Sentinel id passed through CUSTOM0 to the shader. The shader detects
    // values >= TILE_AUTO_THRESHOLD and picks the real tile by surface slope.
    public const int TILE_AUTO = 255;
    // Path-band sentinel: same idea but shader uses tighter slope rules
    // (never grass; dirt by default; stone only on steep faces).
    public const int TILE_AUTO_PATH = 254;

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
        { VoxelType.Water, new(TILE_WATER) },
        { VoxelType.Terrain, new(TILE_AUTO) },
        { VoxelType.TerrainPath, new(TILE_AUTO_PATH) },
    };

    // Per-voxel-type "noisiness" of texture-tile borders. 0 = crisp boundary
    // along the triangle bisector (good for man-made walls). Higher = more
    // jagged/irregular border between this tile and a neighbouring tile (good
    // for organic terrain). Sampled per-vertex and interpolated, then used in
    // voxel_clip.gdshader to perturb the barycentric argmax with 3D noise.
    public static readonly Dictionary<VoxelType, float> BlendNoise = new()
    {
        { VoxelType.Stone, 0.0f },
        { VoxelType.Grass, 0.55f },
        { VoxelType.Dirt,  0.55f },
        { VoxelType.Sand,  0.55f },
        { VoxelType.Wood,  0.0f },
        { VoxelType.Water, 0.0f },
        { VoxelType.Terrain, 0.55f },
        { VoxelType.TerrainPath, 0.55f },
    };

    public static float GetBlendNoise(VoxelType type)
    {
        return BlendNoise.TryGetValue(type, out float v) ? v : 0f;
    }

    // Per-axis opt-in to the DC mesher's sharp-corner path. Each flagged
    // axis: (1) snaps the cell's vertex coord on that axis to 0/0.5/1 via the
    // majority-side rule, and (2) for X|Y|Z together, flat-shades quads (so
    // floor <-> wall transitions read as creases). Mask axes independently:
    //   SharpAxes.Y alone  → flat floors/ceilings, walls keep organic curve.
    //   SharpAxes.All      → fully blocky, square building edges in all axes.
    [System.Flags]
    public enum SharpAxes
    {
        None = 0,
        X = 1,
        Y = 2,
        Z = 4,
        All = X | Y | Z,
    }

    // Default shape flag to stamp at each voxel when its material is written
    // and the caller doesn't override. The mesher reads the stamped per-voxel
    // shape (ChunkState.Shape) — not this table — so worldgen can override
    // per-voxel (e.g. tag path-band ramp columns as None while the rest of
    // the Terrain surface stays Y). Keep this table authoritative for the
    // "default intent" of a material: buildings fully blocky, natural ground
    // snaps on Y only, ramps stay smooth.
    public static readonly Dictionary<VoxelType, SharpAxes> DefaultShape = new()
    {
        { VoxelType.Stone,       SharpAxes.All },
        { VoxelType.Wood,        SharpAxes.All },
        { VoxelType.Grass,       SharpAxes.Y },
        { VoxelType.Dirt,        SharpAxes.Y },
        { VoxelType.Sand,        SharpAxes.Y },
        { VoxelType.Terrain,     SharpAxes.Y },
        { VoxelType.TerrainPath, SharpAxes.None },
    };

    public static SharpAxes GetDefaultShape(VoxelType type)
    {
        return DefaultShape.TryGetValue(type, out SharpAxes v) ? v : SharpAxes.None;
    }

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
    /// Added on top of the normal LightEngine.FALLOFF_PER_VOXEL decay.
    /// </summary>
    public static int LightAttenuation(VoxelType type)
    {
        if (type == VoxelType.Water)
        {
            return 8;
        }
        return 0;
    }

}
