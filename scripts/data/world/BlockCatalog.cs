using Godot;
using System.Collections.Generic;

// The world's set of placeable blocks — the single source of truth for the
// per-voxel byte. Unlike the old terrain palette this is GLOBAL and not
// per-world, so a stored block id means the same thing in every file and can't
// silently re-point when a world's zone list is reordered.
//
// Lookup tables are built lazily and never invalidated: block resources are
// immutable after load.
[GlobalClass]
public partial class BlockCatalog : Resource
{
    public const string CatalogResourcePath = "res://resources/data/blocks/block_catalog.tres";

    // Upper bound on BlockId; sizes the id lookup table and the shader's
    // per-block uniform arrays. Must match MAX_BLOCKS in voxel_clip.gdshader.
    public const int MAX_BLOCKS = 64;

    // Upper bound on BlockSurfaceData.atlasBaseIndex; sizes the per-layer
    // lookups and the shader's per-layer uniform arrays.
    public const int MAX_ATLAS_LAYERS = 64;

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

    [Export] public BlockData[] blocks;

    // The block a voxel holds when nothing has been written — empty space.
    // Named rather than assumed at id 0 so the catalog asset owns the choice.
    [Export] public BlockData airBlock;

    public int AirBlockId { get; private set; }

    private BlockData[] _byId;
    private Dictionary<StringName, BlockData> _byName;
    // Atlas layer -> the block wearing it on top. Lets the overlay channel,
    // which names a LAYER rather than a block, resolve to a block's material
    // properties. First block wins where several share a top surface.
    private BlockData[] _byTopSurfaceLayer;
    // Atlas layer -> the surface baked there, gathered from every face of every
    // block. There is no separate surface catalog: a surface no block wears is
    // unreachable at runtime by definition.
    private BlockSurfaceData[] _surfaceByLayer;

    public BlockData GetById(int blockId)
    {
        EnsureBuilt();
        if (blockId < 0 || blockId >= _byId.Length)
        {
            return null;
        }
        return _byId[blockId];
    }

    public BlockData GetByTopSurfaceLayer(int atlasLayer)
    {
        EnsureBuilt();
        if (atlasLayer < 0 || atlasLayer >= _byTopSurfaceLayer.Length)
        {
            return null;
        }
        return _byTopSurfaceLayer[atlasLayer];
    }

    // The surface baked at an atlas layer, or null if no block wears it.
    public BlockSurfaceData GetSurfaceByLayer(int atlasLayer)
    {
        EnsureBuilt();
        if (atlasLayer < 0 || atlasLayer >= _surfaceByLayer.Length)
        {
            return null;
        }
        return _surfaceByLayer[atlasLayer];
    }

    public BlockData GetByName(StringName name)
    {
        EnsureBuilt();
        return _byName.TryGetValue(name, out BlockData data) ? data : null;
    }

    // Name -> wire id, for the fixed-role lookups that must name a block
    // (worldgen road treads, overlay ids). Logs and falls back to air rather
    // than throwing, so a partial catalog is still testable.
    public int GetIdByName(StringName name)
    {
        BlockData block = GetByName(name);
        if (block == null)
        {
            GD.PushError($"BlockCatalog: no block named '{name}'.");
            return AirBlockId;
        }
        return block.blockId;
    }

    public void ValidateOrLog()
    {
        EnsureBuilt();

        if (blocks == null || blocks.Length == 0)
        {
            GD.PushError("BlockCatalog: blocks array is empty.");
            return;
        }

        var seenIds = new HashSet<int>();
        var seenNames = new HashSet<StringName>();
        foreach (BlockData block in blocks)
        {
            if (block == null)
            {
                GD.PushError("BlockCatalog: null entry in blocks.");
                continue;
            }
            if (block.blockId < 0 || block.blockId >= MAX_BLOCKS)
            {
                GD.PushError($"BlockCatalog: '{block.blockName}' blockId={block.blockId} out of range [0, {MAX_BLOCKS}).");
            }
            if (!seenIds.Add(block.blockId))
            {
                GD.PushError($"BlockCatalog: duplicate blockId={block.blockId} (block '{block.blockName}').");
            }
            if (block.blockName.IsEmpty)
            {
                GD.PushError($"BlockCatalog: block at blockId={block.blockId} has an empty blockName.");
            }
            else if (!seenNames.Add(block.blockName))
            {
                GD.PushError($"BlockCatalog: duplicate blockName='{block.blockName}'.");
            }
            // A visible block with no Top has nothing to fall back to —
            // SurfaceFor resolves every face through Top last.
            if (!block.IsInvisible() && block.top == null)
            {
                GD.PushError($"BlockCatalog: '{block.blockName}' authors a side/bottom surface but no top.");
            }
            if (block.render == EBlockRender.Water && block.IsInvisible())
            {
                GD.PushError($"BlockCatalog: '{block.blockName}' renders as Water but has no surfaces.");
            }
        }

        if (airBlock == null)
        {
            GD.PushError("BlockCatalog: airBlock is not assigned.");
        }
        else if (GetById(airBlock.blockId) != airBlock)
        {
            GD.PushError($"BlockCatalog: airBlock '{airBlock.blockName}' is not in the blocks array.");
        }
        else if (airBlock.solid || !airBlock.IsInvisible())
        {
            GD.PushError($"BlockCatalog: airBlock '{airBlock.blockName}' must be non-solid and invisible.");
        }
    }

    private void EnsureBuilt()
    {
        if (_byId != null)
        {
            return;
        }
        _byId = new BlockData[MAX_BLOCKS];
        _byName = new Dictionary<StringName, BlockData>();
        _byTopSurfaceLayer = new BlockData[MAX_ATLAS_LAYERS];
        _surfaceByLayer = new BlockSurfaceData[MAX_ATLAS_LAYERS];
        if (blocks != null)
        {
            foreach (BlockData block in blocks)
            {
                if (block == null)
                {
                    continue;
                }
                if (block.blockId >= 0 && block.blockId < _byId.Length)
                {
                    _byId[block.blockId] = block;
                }
                if (!block.blockName.IsEmpty)
                {
                    _byName[block.blockName] = block;
                }
                int layer = block.top != null ? block.top.atlasBaseIndex : -1;
                if (layer >= 0 && layer < _byTopSurfaceLayer.Length && _byTopSurfaceLayer[layer] == null)
                {
                    _byTopSurfaceLayer[layer] = block;
                }
                foreach (BlockSurfaceData surface in new[] { block.top, block.side, block.bottom })
                {
                    if (surface == null) { continue; }
                    int sl = surface.atlasBaseIndex;
                    if (sl >= 0 && sl < _surfaceByLayer.Length)
                    {
                        _surfaceByLayer[sl] = surface;
                    }
                }
            }
        }
        AirBlockId = airBlock != null ? airBlock.blockId : 0;
    }
}
