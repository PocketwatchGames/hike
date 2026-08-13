using Godot;
using System.Collections.Generic;

// Authored set of all BlockSurfaceData resources used by the voxel renderer. One
// BlockSurfaceCatalog asset (block_catalog.tres) is the single source of truth that
// downstream systems consult:
//   - ChunkMesh populates the shader's tile_variants[64] uniform from the
//     catalog (one atlas layer per AtlasBaseIndex).
//   - MinimapData reads per-block per-band colors via GetByAtlasIndex.
//   - BlockData's top/side/bottom point at BlockSurfaceData refs; ChunkMesh
//     resolves their AtlasBaseIndex when uploading block_faces[].
//
// Lookup is built lazily on first call. The atlas-index slot table holds the
// block whose AtlasBaseIndex matches that slot (NOT every slot in the
// block's layer range) — this matches the existing shader contract where
// tile_variants[i] is authored only at base indices, with intermediate slots
// defaulting to (1,1). It also allows alias entries (e.g. DesertSand@28
// alongside DesertTop@27..28).
[GlobalClass]
public partial class BlockSurfaceCatalog : Resource
{
    public const string CatalogResourcePath = "res://resources/data/surfaces/surface_catalog.tres";

    // Upper bound on AtlasBaseIndex; sizes the atlas-index lookup table and the
    // shader's per-layer uniform arrays. Headroom over the current layer count.
    public const int MAX_ATLAS_LAYERS = 64;

    // Lazy-loaded canonical catalog. Every runtime consumer (ChunkMesh,
    // Minimap, MinimapData, worldgen) reads BlockSurfaceCatalog.Active rather than
    // GD.Load-ing on its own — single load, single validation pass.
    private static BlockSurfaceCatalog _active;
    public static BlockSurfaceCatalog Active
    {
        get
        {
            if (_active == null)
            {
                _active = GD.Load<BlockSurfaceCatalog>(CatalogResourcePath);
                _active.ValidateOrLog();
            }
            return _active;
        }
    }

    [Export] public BlockSurfaceData[] blocks;

    // Fallback blocks used when a TerrainData entry doesn't author a FlatTile /
    // WallTile, or when minimap surface resolution can't map the terrain. Resolved
    // to int indices once at build time so hot paths read a field instead of
    // doing a name lookup. Authored on block_catalog.tres so renaming or
    // re-indexing the underlying block doesn't silently fall back to slot 0.
    [Export] public BlockSurfaceData defaultFlatTile;
    [Export] public BlockSurfaceData defaultWallTile;

    public int DefaultFlatTileIndex { get; private set; }
    public int DefaultWallTileIndex { get; private set; }

    private BlockSurfaceData[] _byAtlasIndex;
    private Dictionary<StringName, BlockSurfaceData> _byName;

    public BlockSurfaceData GetByAtlasIndex(int atlasIndex)
    {
        EnsureBuilt();
        if (atlasIndex < 0 || atlasIndex >= _byAtlasIndex.Length)
        {
            return null;
        }
        return _byAtlasIndex[atlasIndex];
    }

    public BlockSurfaceData GetByName(StringName name)
    {
        EnsureBuilt();
        return _byName.TryGetValue(name, out BlockSurfaceData data) ? data : null;
    }

    // Resolve a block name to its AtlasBaseIndex, logging an error and falling
    // back to layer 0 if it is missing. The single name→index resolver: used by
    // the fixed-role lookups that must name a block (VoxelTypeInfo's
    // authored-override types, worldgen overlay ids). Anything resolving a
    // *fallback* rather than a role reads DefaultFlatTileIndex /
    // DefaultWallTileIndex instead, so the catalog asset owns that choice.
    public int GetAtlasIndexByName(StringName name)
    {
        BlockSurfaceData block = GetByName(name);
        if (block == null)
        {
            GD.PushError($"BlockSurfaceCatalog: no block named '{name}'.");
            return 0;
        }
        return block.atlasBaseIndex;
    }

    // Boot-time validator. Logs to GD.PushError so issues surface as red in
    // the Godot console; doesn't throw, since a partial catalog is still
    // usable for testing.
    public void ValidateOrLog()
    {
        EnsureBuilt();

        if (blocks == null || blocks.Length == 0)
        {
            GD.PushError("BlockSurfaceCatalog: Blocks array is empty.");
            return;
        }

        var seenIndices = new HashSet<int>();
        var seenNames = new HashSet<StringName>();
        foreach (var block in blocks)
        {
            if (block == null)
            {
                GD.PushError("BlockSurfaceCatalog: null entry in Blocks.");
                continue;
            }

            if (block.atlasBaseIndex < 0 || block.atlasBaseIndex >= BlockSurfaceCatalog.MAX_ATLAS_LAYERS)
            {
                GD.PushError($"BlockSurfaceCatalog: '{block.surfaceName}' AtlasBaseIndex={block.atlasBaseIndex} out of range [0, {BlockSurfaceCatalog.MAX_ATLAS_LAYERS}).");
            }

            if (!seenIndices.Add(block.atlasBaseIndex))
            {
                GD.PushError($"BlockSurfaceCatalog: duplicate AtlasBaseIndex={block.atlasBaseIndex} (block '{block.surfaceName}').");
            }

            if (block.surfaceName.IsEmpty)
            {
                GD.PushError($"BlockSurfaceCatalog: block at AtlasBaseIndex={block.atlasBaseIndex} has empty BlockName.");
            }
            else if (!seenNames.Add(block.surfaceName))
            {
                GD.PushError($"BlockSurfaceCatalog: duplicate BlockName='{block.surfaceName}'.");
            }
        }

        ValidateDefault(defaultFlatTile, nameof(defaultFlatTile));
        ValidateDefault(defaultWallTile, nameof(defaultWallTile));
    }

    // A default must be assigned AND be a member of blocks — otherwise its
    // AtlasBaseIndex names a slot the catalog never indexed, and every terrain
    // that doesn't author its own tile silently renders whatever else landed
    // there (or layer 0).
    private void ValidateDefault(BlockSurfaceData block, string field)
    {
        if (block == null)
        {
            GD.PushError($"BlockSurfaceCatalog: {field} is not assigned.");
            return;
        }
        if (GetByAtlasIndex(block.atlasBaseIndex) != block)
        {
            GD.PushError($"BlockSurfaceCatalog: {field} '{block.surfaceName}' is not in the Blocks array.");
        }
    }

    private void EnsureBuilt()
    {
        if (_byAtlasIndex != null)
        {
            return;
        }
        _byAtlasIndex = new BlockSurfaceData[BlockSurfaceCatalog.MAX_ATLAS_LAYERS];
        _byName = new Dictionary<StringName, BlockSurfaceData>();
        if (blocks == null)
        {
            return;
        }
        foreach (var block in blocks)
        {
            if (block == null)
            {
                continue;
            }
            if (block.atlasBaseIndex >= 0 && block.atlasBaseIndex < _byAtlasIndex.Length)
            {
                _byAtlasIndex[block.atlasBaseIndex] = block;
            }
            if (!block.surfaceName.IsEmpty)
            {
                _byName[block.surfaceName] = block;
            }
        }
        DefaultFlatTileIndex = defaultFlatTile != null ? defaultFlatTile.atlasBaseIndex : 0;
        DefaultWallTileIndex = defaultWallTile != null ? defaultWallTile.atlasBaseIndex : 0;
    }
}
