using Godot;

// Debug overlay that renders the mob-navigability grid around the player so a
// designer can see exactly which columns the pathfinder considers standable —
// the canonical tool for diagnosing "the player can walk in there but the mob
// won't path there." Gated by the `nav_grid` CVar and driven from World._Process.
//
// It samples the NEAREST loaded mob's TraversalProfile (its real maxStepHeight /
// clearance / headroom), so walking a companion up to a spot shows that mob's
// view of the world. With no mob loaded it falls back to a default ground walker.
//
// It calls WalkabilityGrid.SampleColumn directly rather than going through
// WalkabilityGrid.Sample — deliberately. Sample reads the process-wide
// SharedWalkabilityCache, whose entries are sized to the FIRST caller's
// half-extent (the key omits the extent). Seeding that cache from here with a
// smaller window than the mobs use (16) corrupts their reads. Sampling columns
// directly gives the identical per-column result with zero cache interaction.
//
// Color key (matches the CVar doc):
//   green square outline  — standable dry cell, drawn at its surface Y
//   orange-tinted square  — standable but wall-proximate: the body fits yet
//                           the cell is charged a wall-avoidance cost, so A*
//                           routes through it only when there's no roomier
//                           cell (green→orange tracks rising cost)
//   cyan square outline   — standable water cell (wade/swim)
//   magenta square        — standable but inside a hazard danger zone (fire
//                           trap / campfire / spike trap): wander and ordinary
//                           goto route around it, only an attacking mob walks in
//   red cross             — in-bounds column the pathfinder rejects (no
//                           surface in range, insufficient headroom, or the
//                           body disk can't clear the surrounding walls)
// Out-of-bounds columns (unloaded chunks) are skipped to avoid clutter.
public static class NavGridDebug
{
    // Half-extent of the drawn window, in voxels/metres. 8 → a 17×17 column
    // grid centered on the player.
    private const int RadiusVoxels = 8;

    // Lift the drawn square a hair above the surface so it doesn't z-fight the
    // terrain, and inset it from the cell edges so neighbouring cells read as
    // distinct tiles rather than one merged sheet.
    private const float SurfaceLift = 0.05f;
    private const float CellInset = 0.1f;
    private const float RejectCrossSize = 0.4f;

    private static readonly Color WalkableColor = new(0.2f, 0.9f, 0.2f);
    private static readonly Color PenaltyColor = new(1f, 0.6f, 0.1f);
    private static readonly Color WaterColor = new(0.2f, 0.7f, 1f);
    private static readonly Color HazardColor = new(1f, 0.2f, 1f);
    private static readonly Color RejectColor = new(1f, 0.2f, 0.2f);

    public static void Draw(Sim sim, Vector3 playerPos)
    {
        WorldState ws = sim?.WorldState;
        if (ws == null)
        {
            return;
        }

        TraversalProfile profile = ProfileForNearestMob(sim, playerPos);

        int anchorX = Mathf.FloorToInt(playerPos.X);
        int anchorY = Mathf.FloorToInt(playerPos.Y);
        int anchorZ = Mathf.FloorToInt(playerPos.Z);

        // One column's worth of layer slots, reused across columns.
        WalkabilityCell[] column = new WalkabilityCell[WalkabilityGrid.MaxColumnLayers];

        for (int dz = -RadiusVoxels; dz <= RadiusVoxels; dz++)
        {
            for (int dx = -RadiusVoxels; dx <= RadiusVoxels; dx++)
            {
                int wx = anchorX + dx;
                int wz = anchorZ + dz;
                WalkabilityGrid.SampleColumn(ws, sim, profile, wx, anchorY, wz, column, 0);
                if ((column[0].flags & CellFlags.OutOfBounds) != 0)
                {
                    continue;
                }
                float cx = wx + 0.5f;
                float cz = wz + 0.5f;
                bool anyLayer = false;
                for (int layer = 0; layer < WalkabilityGrid.MaxColumnLayers; layer++)
                {
                    WalkabilityCell cell = column[layer];
                    if (!cell.Walkable)
                    {
                        break;
                    }
                    anyLayer = true;
                    // Hazard tint wins over water/cost so the danger zone is
                    // unmistakable — it's the flag that changes pathing.
                    Color c = cell.IsHazard ? HazardColor
                        : cell.IsWater ? WaterColor
                        : ColorForCost(cell.cost);
                    DrawCellSquare(cx, cell.surfaceY + SurfaceLift, cz, c);
                }
                if (!anyLayer)
                {
                    DebugDraw.Cross(new Vector3(cx, anchorY + 0.5f, cz), RejectCrossSize, RejectColor);
                }
            }
        }
    }

    // Nearest loaded mob's profile, or the default ground walker if none. The
    // companion following the player is normally the nearest, which is exactly
    // the mob a designer is debugging.
    private static TraversalProfile ProfileForNearestMob(Sim sim, Vector3 playerPos)
    {
        Mob nearest = null;
        float bestSq = float.MaxValue;
        foreach (Mob mob in sim.GetEntities<Mob>())
        {
            float dSq = mob.GlobalPosition.DistanceSquaredTo(playerPos);
            if (dSq < bestSq)
            {
                bestSq = dSq;
                nearest = mob;
            }
        }
        return new TraversalProfile(nearest?.mobData);
    }

    // Dry walkable cells lerp green→orange as their wall-avoidance cost rises
    // above the neutral 1.0, so the designer can see where the pathfinder is
    // being pushed off walls.
    private static Color ColorForCost(float cost)
    {
        float t = Mathf.Clamp((cost - 1f) / WalkabilityGrid.WallProximityCost, 0f, 1f);
        return WalkableColor.Lerp(PenaltyColor, t);
    }

    // Four-segment outline of a cell footprint at height y, inset from the
    // cell edges. (cx, cz) is the cell center.
    private static void DrawCellSquare(float cx, float y, float cz, Color color)
    {
        float h = 0.5f - CellInset;
        Vector3 a = new(cx - h, y, cz - h);
        Vector3 b = new(cx + h, y, cz - h);
        Vector3 c = new(cx + h, y, cz + h);
        Vector3 d = new(cx - h, y, cz + h);
        DebugDraw.Line(a, b, color);
        DebugDraw.Line(b, c, color);
        DebugDraw.Line(c, d, color);
        DebugDraw.Line(d, a, color);
    }
}
