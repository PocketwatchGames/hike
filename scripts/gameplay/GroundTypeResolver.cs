using Godot;

// Resolves the EGroundType under a world-space position. Looks up the voxel
// just below the feet, picks its BlockData, and returns block.GroundType.
//
// Resolution order for the BlockData (most-specific wins):
//   1) OverlayId on the voxel — if non-zero, the overlay block (e.g.
//      flowers, dirt patch) is what the player is actually standing on.
//   2) For VoxelType.Terrain — the kit's FlatTile, falling back to the
//      catalog's DefaultFlatTile if the kit doesn't author one.
//   3) For other authored types (Stone / Desert / Marsh / Water) — the
//      block at the top-face atlas index from VoxelTypeInfo.Tiles.
//
// The query samples the voxel just below `pos` (a tinyEpsilon under the
// feet) so a body whose origin sits right on the surface picks up the floor,
// not the air column it's standing in. If that voxel is Air, falls through
// to one cell lower as a safety net for slight float drift on slopes.
public static class GroundTypeResolver
{
    public static EGroundType Resolve(WorldState ws, Vector3 worldPos)
    {
        BlockData block = ResolveBlock(ws, worldPos);
        return block != null ? block.GroundType : EGroundType.Stone;
    }

    // Resolves the BlockData under a world-space position, most-specific wins
    // (overlay → terrain flat tile → base type top face). Returns null when the
    // world is unavailable or the column under `worldPos` is empty. Shared by
    // the footstep ground-type query above and the shovel's bare-ground dig
    // yield (World.TryDig reads block.DigItem).
    public static BlockData ResolveBlock(WorldState ws, Vector3 worldPos)
    {
        if (ws == null)
        {
            return null;
        }

        int fx = Mathf.FloorToInt(worldPos.X);
        int fz = Mathf.FloorToInt(worldPos.Z);
        int fy = Mathf.FloorToInt(worldPos.Y - 0.05f);

        VoxelType v = ws.GetVoxelWorld(fx, fy, fz);
        if (v == VoxelType.Air)
        {
            fy -= 1;
            v = ws.GetVoxelWorld(fx, fy, fz);
        }

        BlockCatalog catalog = BlockCatalog.Active;

        int overlayId = ws.GetOverlayIdWorld(fx, fy, fz);
        if (overlayId != 0)
        {
            BlockData overlay = catalog.GetByAtlasIndex(overlayId);
            if (overlay != null)
            {
                return overlay;
            }
        }

        return ResolveBaseBlock(ws, catalog, v, fx, fy, fz);
    }

    private static BlockData ResolveBaseBlock(WorldState ws, BlockCatalog catalog, VoxelType v, int fx, int fy, int fz)
    {
        if (v == VoxelType.Terrain)
        {
            int terrainId = ws.GetTerrainIdWorld(fx, fy, fz);
            TerrainData[] terrains = ChunkMesh.ActiveTerrains;
            if (terrains != null && terrainId >= 0 && terrainId < terrains.Length && terrains[terrainId] != null)
            {
                BlockData flat = terrains[terrainId].FlatTile;
                if (flat != null)
                {
                    return flat;
                }
            }
            return catalog.DefaultFlatTile;
        }

        int atlasIndex = VoxelTypeInfo.GetTileForFace(v, 0);
        return catalog.GetByAtlasIndex(atlasIndex);
    }
}
