using Godot;
using System.Collections.Generic;

// Authored set of all BlockData resources used by the voxel renderer. One
// BlockCatalog asset (block_catalog.tres) is the single source of truth that
// downstream systems consult:
//   - ChunkMesh populates the shader's tile_variants[64] uniform from the
//     catalog (one atlas layer per AtlasBaseIndex).
//   - MinimapData reads per-block per-band colors via GetByAtlasIndex.
//   - TerrainData.FlatTile/WallTile point at BlockData refs; the kit
//     resolves block.AtlasBaseIndex when uploading kit_tiles[] to the shader.
//
// Lookup is built lazily on first call. The atlas-index slot table holds the
// block whose AtlasBaseIndex matches that slot (NOT every slot in the
// block's layer range) — this matches the existing shader contract where
// tile_variants[i] is authored only at base indices, with intermediate slots
// defaulting to (1,1). It also allows alias entries (e.g. DesertSand@28
// alongside DesertTop@27..28).
[GlobalClass]
public partial class BlockCatalog : Resource
{
    public const string CatalogResourcePath = "res://resources/data/blocks/block_catalog.tres";

    // Lazy-loaded canonical catalog. Every runtime consumer (ChunkMesh,
    // Minimap, MinimapData, worldgen) reads BlockCatalog.Active rather than
    // GD.Load-ing on its own — single load, single validation pass.
    private static BlockCatalog _active;
    public static BlockCatalog Active
    {
        get
        {
            if (_active == null)
            {
                _active = GD.Load<BlockCatalog>(CatalogResourcePath);
                _active.ValidateOrLog();
            }
            return _active;
        }
    }

    [Export] public BlockData[] Blocks;

    // Fallback blocks used when a TerrainData entry doesn't author a FlatTile /
    // WallTile, or when minimap surface resolution can't map the terrain. Resolved
    // to int indices once at build time so hot paths read a field instead of
    // doing a name lookup. Authored on block_catalog.tres so renaming or
    // re-indexing the underlying block doesn't silently fall back to slot 0.
    [Export] public BlockData DefaultFlatTile;
    [Export] public BlockData DefaultWallTile;

    public int DefaultFlatTileIndex { get; private set; }
    public int DefaultWallTileIndex { get; private set; }

    private BlockData[] _byAtlasIndex;
    private Dictionary<StringName, BlockData> _byName;

    public BlockData GetByAtlasIndex(int atlasIndex)
    {
        EnsureBuilt();
        if (atlasIndex < 0 || atlasIndex >= _byAtlasIndex.Length)
        {
            return null;
        }
        return _byAtlasIndex[atlasIndex];
    }

    public BlockData GetByName(StringName name)
    {
        EnsureBuilt();
        return _byName.TryGetValue(name, out BlockData data) ? data : null;
    }

    // Convenience: resolve a block name straight to its AtlasBaseIndex,
    // logging an error and returning 0 (Stone in the canonical catalog) if
    // the block is missing. Used by worldgen overlay-id authoring that still
    // refers to blocks by name; runtime hot paths read DefaultFlatTileIndex /
    // DefaultWallTileIndex (cached at build time) instead.
    public int GetAtlasIndexByName(StringName name)
    {
        BlockData block = GetByName(name);
        if (block == null)
        {
            GD.PushError($"BlockCatalog: no block named '{name}'.");
            return 0;
        }
        return block.AtlasBaseIndex;
    }

    // Boot-time validator. Logs to GD.PushError so issues surface as red in
    // the Godot console; doesn't throw, since a partial catalog is still
    // usable for testing.
    public void ValidateOrLog()
    {
        EnsureBuilt();

        if (Blocks == null || Blocks.Length == 0)
        {
            GD.PushError("BlockCatalog: Blocks array is empty.");
            return;
        }

        var seenIndices = new HashSet<int>();
        var seenNames = new HashSet<StringName>();
        foreach (var block in Blocks)
        {
            if (block == null)
            {
                GD.PushError("BlockCatalog: null entry in Blocks.");
                continue;
            }

            if (block.AtlasBaseIndex < 0 || block.AtlasBaseIndex >= VoxelTypeInfo.MAX_ATLAS_LAYERS)
            {
                GD.PushError($"BlockCatalog: '{block.BlockName}' AtlasBaseIndex={block.AtlasBaseIndex} out of range [0, {VoxelTypeInfo.MAX_ATLAS_LAYERS}).");
            }

            if (!seenIndices.Add(block.AtlasBaseIndex))
            {
                GD.PushError($"BlockCatalog: duplicate AtlasBaseIndex={block.AtlasBaseIndex} (block '{block.BlockName}').");
            }

            if (block.BlockName.IsEmpty)
            {
                GD.PushError($"BlockCatalog: block at AtlasBaseIndex={block.AtlasBaseIndex} has empty BlockName.");
            }
            else if (!seenNames.Add(block.BlockName))
            {
                GD.PushError($"BlockCatalog: duplicate BlockName='{block.BlockName}'.");
            }
        }

        // Shader-side hardcoded constants in voxel_clip.gdshader assume
        // Stone=0 and GrassTop=1. If the catalog drifts from that, fragment
        // shading silently picks the wrong tile.
        AssertNamedAtIndex("Stone", 0);
        AssertNamedAtIndex("GrassTop", 1);

        if (DefaultFlatTile == null)
        {
            GD.PushError("BlockCatalog: DefaultFlatTile is not assigned.");
        }
        if (DefaultWallTile == null)
        {
            GD.PushError("BlockCatalog: DefaultWallTile is not assigned.");
        }
    }

    private void AssertNamedAtIndex(StringName name, int expectedIndex)
    {
        var block = GetByName(name);
        if (block == null)
        {
            GD.PushError($"BlockCatalog: required block '{name}' is missing.");
            return;
        }
        if (block.AtlasBaseIndex != expectedIndex)
        {
            GD.PushError($"BlockCatalog: block '{name}' AtlasBaseIndex={block.AtlasBaseIndex}, shader expects {expectedIndex}.");
        }
    }

    private void EnsureBuilt()
    {
        if (_byAtlasIndex != null)
        {
            return;
        }
        _byAtlasIndex = new BlockData[VoxelTypeInfo.MAX_ATLAS_LAYERS];
        _byName = new Dictionary<StringName, BlockData>();
        if (Blocks == null)
        {
            return;
        }
        foreach (var block in Blocks)
        {
            if (block == null)
            {
                continue;
            }
            if (block.AtlasBaseIndex >= 0 && block.AtlasBaseIndex < _byAtlasIndex.Length)
            {
                _byAtlasIndex[block.AtlasBaseIndex] = block;
            }
            if (!block.BlockName.IsEmpty)
            {
                _byName[block.BlockName] = block;
            }
        }
        DefaultFlatTileIndex = DefaultFlatTile != null ? DefaultFlatTile.AtlasBaseIndex : 0;
        DefaultWallTileIndex = DefaultWallTile != null ? DefaultWallTile.AtlasBaseIndex : 0;
    }
}
