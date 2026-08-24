using Godot;

// WHERE THE WATER ACTUALLY IS, asked of the finished voxels.
//
// Named for the voxels on purpose: the world-map painter has its own
// WaterSurface(px, pz), which is the DOCUMENT's painted water layer and a
// different question — what an author asked for, versus what got baked.
//
// One rule, shared by everything that needs the answer: find the water voxel
// NEAREST a reference point, then walk to the top of the run it belongs to. The
// value returned is the SURFACE — the boundary above the topmost water voxel,
// i.e. `topWaterVoxelY + 1` — matching the convention WaterfallSimState and the
// water mesher use.
//
// This lives here because four call sites had grown their own copy: sprite
// reflections, prop-sprite reflections, the boat's buoyancy, and the boat spawn
// pass. The first three had drifted in which directions they searched; the
// fourth had stopped searching at all and assumed water sits at a fixed Y (the
// nominal sea level), which is false in any world whose water was
// painted or carved rather than generated around a global waterline.
//
// NOT a claim about where water OUGHT to be. There is no waterline rule
// anywhere in the pipeline: water is voxels, and a basin below the nominal sea
// level may be perfectly dry.
public static class VoxelWater
{
    // Surface Y of the water nearest `startY` in one column, or null if the
    // column holds none within `searchDepth` voxels either way.
    //
    // Symmetric on purpose. A boat riding a swell sits with water above its
    // origin as often as below it, and a swimming sprite is inside the water
    // rather than over it — a search that only looked downward answered null
    // for both and had to be worked around at the call site.
    public static float? InColumn(WorldState ws, int wx, int startY, int wz, int searchDepth)
    {
        if (ws == null)
        {
            return null;
        }
        for (int d = 0; d <= searchDepth; d++)
        {
            if (ws.GetBlockWorld(wx, startY + d, wz) == Blocks.WaterId)
            {
                return TopOfRun(ws, wx, startY + d, wz, searchDepth);
            }
            if (d != 0 && ws.GetBlockWorld(wx, startY - d, wz) == Blocks.WaterId)
            {
                return TopOfRun(ws, wx, startY - d, wz, searchDepth);
            }
        }
        return null;
    }

    // Surface Y of the nearest water within `xzRadius` columns of `world`, or
    // null. Rings expand outward from the point's own column, boundary cells
    // only, so each column is tested exactly once.
    //
    // The first hit's Y is the right answer wherever it came from: one body of
    // water stands at one level, so a shoreline point whose own column is dry
    // still gets the pond's real surface.
    public static float? FindNear(WorldState ws, Vector3 world, int xzRadius, int searchDepth)
    {
        if (ws == null)
        {
            return null;
        }
        int cx = Mathf.FloorToInt(world.X);
        int cz = Mathf.FloorToInt(world.Z);
        int startY = Mathf.FloorToInt(world.Y);

        for (int r = 0; r <= xzRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    if (r > 0 && Mathf.Abs(dx) != r && Mathf.Abs(dz) != r)
                    {
                        continue;
                    }
                    float? y = InColumn(ws, cx + dx, startY, cz + dz, searchDepth);
                    if (y.HasValue)
                    {
                        return y;
                    }
                }
            }
        }
        return null;
    }

    // Surface Y of the topmost water in this column, or null if it holds none.
    // Scans the world's full height, so it is a placement-pass query rather
    // than a per-frame one — but it makes no assumption about where the water
    // is, which is exactly what a placement pass needs.
    public static float? TopOfColumn(WorldState ws, int wx, int wz)
    {
        if (ws == null)
        {
            return null;
        }
        int minY = ws.Min.Y * ChunkState.SIZE;
        int maxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        for (int y = maxY; y >= minY; y--)
        {
            if (ws.GetBlockWorld(wx, y, wz) == Blocks.WaterId)
            {
                return y + 1f;
            }
        }
        return null;
    }

    // Y of the solid floor under a water column — the top face of the first
    // non-water voxel below `waterY`. Falls back to the search bound when the
    // column never exits water, which is correct enough: a reflection clamped
    // that deep is already dim, and the alternative is scanning to the world
    // floor for every ocean column.
    public static float FloorBelow(WorldState ws, int wx, int wz, float waterY, int searchDepth)
    {
        int waterTopY = Mathf.FloorToInt(waterY);
        int minY = waterTopY - searchDepth;
        if (ws == null)
        {
            return minY;
        }
        for (int y = waterTopY - 1; y >= minY; y--)
        {
            if (ws.GetBlockWorld(wx, y, wz) != Blocks.WaterId)
            {
                return y + 1;
            }
        }
        return minY;
    }

    // Walk up from a known water voxel to the boundary above the run's top.
    private static float TopOfRun(WorldState ws, int wx, int waterY, int wz, int searchDepth)
    {
        int top = waterY;
        int limit = waterY + searchDepth;
        while (top < limit && ws.GetBlockWorld(wx, top + 1, wz) == Blocks.WaterId)
        {
            top++;
        }
        return top + 1f;
    }
}
