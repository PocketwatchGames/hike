using Godot;

// Kit palette slot -> block id.
//
// Worldgen thinks in KITS (a zone's surface / cave / submerged / shore
// entries), while storage and rendering think in BLOCKS. This is the one place
// that maps between them, so a worldgen pass holding a TerrainId can write the
// right block without reaching through the palette itself.
//
// The kit channel survives alongside the block channel because worldgen needs
// it after the fact: EKitPurpose ("is this voxel the zone's SURFACE ground?")
// and the per-kit scatter tunings are both keyed by it, and a block can't
// answer either — several kits legitimately share one block.
public static class KitBlocks
{
    private static int[] _byKit;

    public static void Bind(TerrainKitData[] kits)
    {
        int fallback = Blocks.GroundId;
        _byKit = new int[BlockCatalog.MAX_BLOCKS];
        for (int i = 0; i < _byKit.Length; i++)
        {
            _byKit[i] = fallback;
        }
        for (int i = 0; kits != null && i < kits.Length && i < _byKit.Length; i++)
        {
            BlockData block = kits[i]?.block;
            if (block == null)
            {
                GD.PushWarning($"KitBlocks: kit palette slot {i} ('{kits[i]?.ResourcePath}') names no block; using the default ground.");
                continue;
            }
            _byKit[i] = block.blockId;
        }
    }

    public static int ForKit(int terrainId)
    {
        if (_byKit == null)
        {
            return 0;
        }
        return (uint)terrainId < (uint)_byKit.Length ? _byKit[terrainId] : _byKit[0];
    }
}
