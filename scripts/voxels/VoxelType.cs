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
    //
    // The value stored in each constant is the BASE layer — the first layer
    // of a contiguous block belonging to that tile. Tiles that ship only one
    // layer occupy exactly one slot (their base layer). Tiles with variants
    // occupy Bands * VariantsPerBand layers starting at their base; the
    // shader resolves the concrete layer per-fragment from world Y (band) and
    // a hash of the voxel position (variant). See TileVariants below and
    // `resolve_layer` in voxel_clip.gdshader.
    //
    // When adding variants to an existing tile, expand its block and shift
    // all tiles with higher base indices forward by the added count. Both
    // this table and the .png layer count must change together.
    public const int TILE_STONE = 0;
    public const int TILE_DIRT = 1;
    // Grass top occupies layers 2..17 — 4 elevation bands × 4 variants each
    // (level1_1..level4_4 in assets/textures/voxels/). Layout is band-major:
    // layer 2+band*4+variant.
    public const int TILE_GRASS_TOP = 2;
    public const int TILE_GRASS_SIDE = 18;
    public const int TILE_SAND = 19;
    public const int TILE_WOOD_END = 20;
    public const int TILE_WOOD_SIDE = 21;
    public const int TILE_WATER = 22;
    // Sentinel id passed through CUSTOM0 to the shader. The shader detects
    // values >= TILE_AUTO_THRESHOLD and picks the real tile by surface slope.
    public const int TILE_AUTO = 255;
    // Path-band sentinel: same idea but shader uses tighter slope rules
    // (never grass; dirt by default; stone only on steep faces).
    public const int TILE_AUTO_PATH = 254;

    // Size of the `tile_variants` uniform array in voxel_clip.gdshader.
    // Must be >= 1 + max base layer in use. Keep modest — every entry is a
    // vec2 shipped with the material.
    public const int TILE_VARIANT_TABLE_SIZE = 32;

    public readonly struct TileVariantInfo
    {
        // Number of elevation bands. The shader picks a band from world Y
        // via floor((y - BandOriginY) / BandHeight), clamped to [0, Bands-1].
        // Each band occupies `VariantsPerBand` contiguous layers.
        public readonly int Bands;
        // Random variants within each band. Picked from a hash of the voxel
        // integer position, so all fragments within one voxel pick the same
        // variant but neighbouring voxels vary.
        public readonly int VariantsPerBand;

        public TileVariantInfo(int bands, int variantsPerBand)
        {
            Bands = bands;
            VariantsPerBand = variantsPerBand;
        }

        public int LayerCount => Bands * VariantsPerBand;
    }

    // Band quantization for the shader. BandOriginY is the world Y where
    // band 0 starts; BandHeight is how tall each band is in world units.
    // Matches corresponding uniforms in voxel_clip.gdshader.
    public const float TILE_BAND_ORIGIN_Y = 0f;
    public const float TILE_BAND_HEIGHT = 32f;

    // Per-tile variant config. Defaults are (1,1) for every tile — a tile
    // that hasn't had variant art authored yet behaves exactly as it did
    // before this system existed (samples its base layer directly). Expand
    // an entry when you add variant/band art for that tile and grow the
    // .png's layer count to match.
    public static readonly Dictionary<int, TileVariantInfo> TileVariants = new()
    {
        { TILE_STONE,      new(1, 1) },
        { TILE_DIRT,       new(1, 1) },
        { TILE_GRASS_TOP,  new(4, 4) },
        { TILE_GRASS_SIDE, new(1, 1) },
        { TILE_SAND,       new(1, 1) },
        { TILE_WOOD_END,   new(1, 1) },
        { TILE_WOOD_SIDE,  new(1, 1) },
        { TILE_WATER,      new(1, 1) },
    };

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
