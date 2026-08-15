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
    // The kit channel is ChunkState.TerrainId, a byte — so it addresses 0..255.
    // This table used to be sized by BlockCatalog.MAX_BLOCKS, a different id
    // space that merely happened to be bigger than the kit count; past 64 kits
    // it would have silently dropped the rest to the fallback ground.
    public const int MAX_KITS = 256;

    private static int[] _byKit;

    public static void Bind(TerrainKitData[] kits)
    {
        int fallback = Blocks.GroundId;
        _byKit = new int[MAX_KITS];
        for (int i = 0; i < _byKit.Length; i++)
        {
            _byKit[i] = fallback;
        }
        if (kits != null && kits.Length > MAX_KITS)
        {
            GD.PushError($"KitBlocks: {kits.Length} kits exceeds the {MAX_KITS} the TerrainId byte can address; the excess will render as default ground.");
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
