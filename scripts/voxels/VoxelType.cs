using System.Collections.Generic;
using Godot;

public enum VoxelType : byte
{
    Air = 0,
    Stone = 1,
    Barrier = 6,
    Water = 7,
    // "Auto" terrain: shader picks tile from per-voxel TerrainId + surface
    // normal.y. Use this for natural land — Stone is the only authored solid
    // override that remains (used for explicit cliff geometry / future
    // building blocks).
    Terrain = 8,
    // Authored sand/dune terrain. Top face cycles desert_elevation0grass +
    // desert_level1 by elevation band.
    Desert = 10,
    // Authored wetland tile (single-tile, no bands/variants).
    Marsh = 11,
}

public static class VoxelTypeInfo
{
    public static readonly Dictionary<VoxelType, Color> Colors = new()
    {
        { VoxelType.Stone, new Color(1f, 1f, 1f) },
        { VoxelType.Water, new Color(0.6f, 0.85f, 1f) },
        { VoxelType.Terrain, new Color(1f, 1f, 1f) },
        { VoxelType.Desert, new Color(1f, 1f, 1f) },
        { VoxelType.Marsh, new Color(1f, 1f, 1f) },
    };

    // Per-block atlas-layer indices come from BlockCatalog now. Use
    // ResolveBlockIndex(name) below to look up a block's AtlasBaseIndex by
    // its authored name. The shader's tile_array layer order in
    // res://assets/textures/voxels/voxel_tiles.png must agree with the
    // AtlasBaseIndex authored on each BlockData; the catalog asserts the
    // two named slots the shader hardcodes ("Stone" at 0, "GrassTop" at 1).
    //
    // Sentinel id passed through CUSTOM0 to the shader. The shader detects
    // values >= TILE_AUTO_THRESHOLD and picks the real tile by surface slope.
    // Not a BlockData entry — kits resolve to a real block at draw time.
    public const int TILE_AUTO = 255;

    private static int ResolveBlockIndex(StringName name)
    {
        BlockData block = BlockCatalog.Active.GetByName(name);
        if (block == null)
        {
            GD.PushError($"VoxelTypeInfo: BlockCatalog missing '{name}'.");
            return 0;
        }
        return block.atlasBaseIndex;
    }

    // Upper bound on BlockData.AtlasBaseIndex; sizes the catalog's
    // atlas-index lookup table. Headroom over the current layer count.
    public const int MAX_ATLAS_LAYERS = 64;

    // Atlas slot resolution in pixels. Must match SLOT in
    // tools/stitch_voxel_atlas.py — the stitcher normalizes every authored
    // PNG to this size before packing, so the shader can assume every
    // tile_array layer is exactly this wide.
    public const int ATLAS_SLOT_PIXELS = 256;

    // Default world-to-UV scale (tiling frequency) for the shader. One authored
    // PNG spans 1/TILE_UV_SCALE world units on each axis — at 0.3 that's ~3.3
    // voxels per repeat. Hand-tuned, not derived from texel density; seeds the
    // terrain_tile_scale CVar, which overrides it live.
    public const float TILE_UV_SCALE = 0.3f;

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
        { VoxelType.Stone,   new(ResolveBlockIndex("Stone")) },
        { VoxelType.Water,   new(ResolveBlockIndex("Water")) },
        { VoxelType.Terrain, new(TILE_AUTO) },
        { VoxelType.Desert,  new(ResolveBlockIndex("DesertTop"), ResolveBlockIndex("DesertWall"), ResolveBlockIndex("DesertWall")) },
        { VoxelType.Marsh,   new(ResolveBlockIndex("Marsh")) },
    };

    // Per-voxel-type "noisiness" of texture-tile borders. 0 = crisp boundary
    // along the triangle bisector (good for man-made walls). Higher = more
    // jagged/irregular border between this tile and a neighbouring tile (good
    // for organic terrain). Sampled per-vertex and interpolated, then used in
    // voxel_clip.gdshader to perturb the barycentric argmax with 3D noise.
    public static readonly Dictionary<VoxelType, float> BlendNoise = new()
    {
        { VoxelType.Stone, 0.0f },
        { VoxelType.Water, 0.0f },
        { VoxelType.Terrain, 0.55f },
        { VoxelType.Desert, 0.55f },
        { VoxelType.Marsh, 0.55f },
    };

    public static float GetBlendNoise(VoxelType type)
    {
        return BlendNoise.TryGetValue(type, out float v) ? v : 0f;
    }

    // Per-block geometric edge roughness, resolved once from the catalog and
    // keyed by voxel type so the mesher's per-cell read is a dictionary hit
    // rather than a catalog walk. Keyed off the SIDE tile: roughness is a
    // property of the wall material, and Terrain's side is TILE_AUTO (no single
    // block), which resolves to null and therefore to zero — natural terrain is
    // already organic via the surface-nets path and must not be carved.
    public readonly struct EdgeRoughness
    {
        public readonly float Amount;
        public readonly float VerticalScale;

        public EdgeRoughness(float amount, float verticalScale)
        {
            Amount = amount;
            VerticalScale = verticalScale;
        }
    }

    private static readonly Dictionary<VoxelType, EdgeRoughness> EdgeRoughnessByType = BuildEdgeRoughness();

    private static Dictionary<VoxelType, EdgeRoughness> BuildEdgeRoughness()
    {
        var map = new Dictionary<VoxelType, EdgeRoughness>();
        foreach (var pair in Tiles)
        {
            BlockData block = BlockCatalog.Active.GetByAtlasIndex(pair.Value.Side);
            map[pair.Key] = block == null
                ? new EdgeRoughness(0f, 0f)
                : new EdgeRoughness(block.edgeRoughness, block.edgeRoughnessVerticalScale);
        }
        return map;
    }

    public static EdgeRoughness GetEdgeRoughness(VoxelType type)
    {
        return EdgeRoughnessByType.TryGetValue(type, out EdgeRoughness r) ? r : new EdgeRoughness(0f, 0f);
    }

    // Representative "ground color" per voxel type, used to tint the bottom of
    // detail sprites so blades visually root into the surface they sit on.
    // Biased ~40% darker than the authored ground tones so the tint doubles
    // as a fake contact-AO — the darkened base reads as self-shadowing where
    // the blade meets the ground rather than a flat color match.
    public static readonly Dictionary<VoxelType, Color> GroundTint = new()
    {
        { VoxelType.Stone,       new Color(0.22f, 0.22f, 0.22f) },
        // Terrain resolves to grass_top on the slopes where detail grass
        // actually spawns (gentle/flat ground). Treat as grass.
        { VoxelType.Terrain,     new Color(0.16f, 0.22f, 0.09f) },
        // Warm dune sand.
        { VoxelType.Desert,      new Color(0.34f, 0.26f, 0.16f) },
        // Wet, organic — leans dark green/brown.
        { VoxelType.Marsh,       new Color(0.12f, 0.16f, 0.10f) },
    };

    public static Color GetGroundTint(VoxelType type)
    {
        return GroundTint.TryGetValue(type, out Color c) ? c : new Color(0.4f, 0.4f, 0.4f);
    }



    // Per-axis opt-in to the DC mesher's sharp-corner path. Each flagged
    // axis: (1) snaps the cell's vertex coord on that axis to 0/0.5/1 via the
    // majority-side rule, and (2) for X|Y|Z together, flat-shades quads (so
    // floor <-> wall transitions read as creases). Mask axes independently:
    //   SharpAxes.Y alone  → flat floors/ceilings, walls keep organic curve.
    //   SharpAxes.All      → fully blocky, square building edges in all axes.
    // The Y snap is a hard step — 1-voxel height differentials stay crisp,
    // not smoothed. Intentional slopes (ramps, authored terrain blends)
    // author SharpAxes.None so the mesher averages the cell via the normal
    // surface-nets path and produces a smooth slope.
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
    // per-voxel. Keep this table authoritative for the "default intent" of a
    // material: buildings fully blocky, natural ground snaps on Y only,
    // ramps stay smooth.
    public static readonly Dictionary<VoxelType, SharpAxes> DefaultShape = new()
    {
        { VoxelType.Stone,       SharpAxes.All },
        { VoxelType.Terrain,     SharpAxes.Y },
        { VoxelType.Desert,      SharpAxes.Y },
        { VoxelType.Marsh,       SharpAxes.Y },
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
