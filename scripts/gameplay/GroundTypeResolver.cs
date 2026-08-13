using Godot;

// Resolves the BlockData under a world-space position — the material a body is
// standing on. Footsteps read its GroundType, locomotion its SpeedMultiplier,
// the shovel its DigItem.
//
// Most-specific wins: an OverlayId painted on the voxel (dirt path, cobbled
// road) is what you are actually standing on, so it beats the voxel's own
// block. Overlays name an atlas LAYER rather than a block, so they resolve
// through the catalog's top-surface reverse lookup.
//
// The query samples the voxel just below `pos` (a tiny epsilon under the feet)
// so a body whose origin sits right on the surface picks up the floor, not the
// air column it is standing in. If that voxel is empty, it falls through one
// cell lower as a safety net for float drift on slopes.
public static class GroundTypeResolver
{
    public static EGroundType Resolve(WorldState ws, Vector3 worldPos)
    {
        BlockData block = ResolveBlock(ws, worldPos);
        return block != null ? block.groundType : EGroundType.Stone;
    }

    // Null when the world is unavailable or the column under `worldPos` is empty.
    public static BlockData ResolveBlock(WorldState ws, Vector3 worldPos)
    {
        if (ws == null)
        {
            return null;
        }

        int fx = Mathf.FloorToInt(worldPos.X);
        int fz = Mathf.FloorToInt(worldPos.Z);
        int fy = Mathf.FloorToInt(worldPos.Y - 0.05f);

        int v = ws.GetBlockWorld(fx, fy, fz);
        if (Blocks.IsEmpty(v))
        {
            fy -= 1;
            v = ws.GetBlockWorld(fx, fy, fz);
        }

        BlockCatalog catalog = BlockCatalog.Active;

        int overlayId = ws.GetOverlayIdWorld(fx, fy, fz);
        if (overlayId != 0)
        {
            BlockData overlay = catalog.GetByTopSurfaceLayer(overlayId);
            if (overlay != null)
            {
                return overlay;
            }
        }

        return catalog.GetById(v);
    }
}
