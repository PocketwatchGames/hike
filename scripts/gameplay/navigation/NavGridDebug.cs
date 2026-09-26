using Godot;

// Debug overlay that renders the mob-navigability grid around a point so a
// designer can see exactly which columns the pathfinder considers standable —
// the canonical tool for diagnosing "the player can walk in there but the mob
// won't path there." Gated by the `nav_grid` CVar; the game draws it around the
// player from Sim._Process, the world editor around its edit cursor (there is no
// player there, so Sim's call bails before reaching this).
//
// In game it draws the PLAYER's profile, so the cells shown are exactly the
// ones the ledge guard reads when deciding whether a step would walk off a
// drop. The editor has no player and passes none, falling back to the NEAREST
// loaded mob's TraversalProfile (its real maxStepHeight / clearance / headroom)
// so walking a companion up to a spot shows that mob's view of the world — and
// to a default ground walker when no mob is loaded, which is what the humanoid
// NPC entries resolve to anyway.
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
    private static readonly Color FootprintColor = new(1f, 1f, 1f);
    private static readonly Color InflatedColor = new(0.55f, 0.55f, 0.55f);

    // Entities whose origin is this far outside the drawn window still have
    // their colliders drawn — a big bush centred just off-window reaches in.
    private const float FootprintEntityReach = 3f;
    private const float FootprintLift = 0.1f;
    private const int FootprintCircleSegments = 24;

    // profileOverride pins the drawn field to a specific body. The game passes
    // the PLAYER's profile so the overlay shows the exact cells the ledge guard
    // is reading; the editor passes none and falls back to the nearest mob.
    public static void Draw(Sim sim, Vector3 center, TraversalProfile? profileOverride = null)
    {
        WorldState ws = sim?.WorldState;
        if (ws == null)
        {
            return;
        }

        TraversalProfile profile = profileOverride ?? ProfileForNearestMob(sim, center);

        int anchorX = Mathf.FloorToInt(center.X);
        int anchorY = Mathf.FloorToInt(center.Y);
        int anchorZ = Mathf.FloorToInt(center.Z);

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

        DrawColliderFootprints(sim, center, profile.clearanceRadius);
    }

    // The real XZ footprint of every path-blocking collider in the window
    // (white), and the same footprint grown by the profile's body radius
    // (grey) — the region a body CENTER of that radius cannot enter. A green
    // cell whose centre sits inside a grey outline is one the grid calls
    // walkable but physics will refuse: the snag. Trimeshes draw as their XZ
    // bounding box, so their outline over-reports the corners.
    private static void DrawColliderFootprints(Sim sim, Vector3 center, float bodyRadius)
    {
        float reachSq = (RadiusVoxels + FootprintEntityReach) * (RadiusVoxels + FootprintEntityReach);
        foreach (Node3D entity in sim.GetEntities<Node3D>())
        {
            Vector3 p = entity.GlobalPosition;
            float dx = p.X - center.X;
            float dz = p.Z - center.Z;
            if (dx * dx + dz * dz > reachSq)
            {
                continue;
            }
            DrawFootprintsUnder(entity, p.Y + FootprintLift, bodyRadius);
        }
    }

    // Same body filter as PathBlockerRasterizer: only Solid-layer bodies block.
    private static void DrawFootprintsUnder(Node node, float y, float bodyRadius)
    {
        if (node is CollisionObject3D body && (body.CollisionLayer & (uint)ECollisionLayer.Solid) != 0)
        {
            foreach (Node child in body.GetChildren())
            {
                if (child is CollisionShape3D cs && cs.Shape != null && !cs.Disabled)
                {
                    DrawShapeFootprint(cs.Shape, cs.GlobalTransform, y, bodyRadius);
                }
            }
        }
        foreach (Node child in node.GetChildren())
        {
            DrawFootprintsUnder(child, y, bodyRadius);
        }
    }

    private static void DrawShapeFootprint(Shape3D shape, Transform3D xform, float y, float bodyRadius)
    {
        Vector3 o = xform.Origin;
        switch (shape)
        {
            case CylinderShape3D cyl:
                DrawCircleXz(o.X, y, o.Z, cyl.Radius, FootprintColor);
                DrawCircleXz(o.X, y, o.Z, cyl.Radius + bodyRadius, InflatedColor);
                break;
            case SphereShape3D sph:
                DrawCircleXz(o.X, y, o.Z, sph.Radius, FootprintColor);
                DrawCircleXz(o.X, y, o.Z, sph.Radius + bodyRadius, InflatedColor);
                break;
            case CapsuleShape3D cap:
                DrawCircleXz(o.X, y, o.Z, cap.Radius, FootprintColor);
                DrawCircleXz(o.X, y, o.Z, cap.Radius + bodyRadius, InflatedColor);
                break;
            case BoxShape3D box:
            {
                Vector3 h = box.Size * 0.5f;
                Vector3 bx = xform.Basis.X.Normalized();
                Vector3 bz = xform.Basis.Z.Normalized();
                float sx = xform.Basis.X.Length() * h.X;
                float sz = xform.Basis.Z.Length() * h.Z;
                DrawBoxXz(o, bx * sx, bz * sz, y, FootprintColor);
                DrawBoxXz(o, bx * (sx + bodyRadius), bz * (sz + bodyRadius), y, InflatedColor);
                break;
            }
            case ConcavePolygonShape3D concave:
            {
                Vector3[] verts = concave.Data;
                if (verts == null || verts.Length == 0)
                {
                    break;
                }
                float minX = float.MaxValue;
                float maxX = float.MinValue;
                float minZ = float.MaxValue;
                float maxZ = float.MinValue;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 w = xform * verts[i];
                    minX = Mathf.Min(minX, w.X);
                    maxX = Mathf.Max(maxX, w.X);
                    minZ = Mathf.Min(minZ, w.Z);
                    maxZ = Mathf.Max(maxZ, w.Z);
                }
                DrawRectXz(minX, maxX, minZ, maxZ, y, FootprintColor);
                DrawRectXz(minX - bodyRadius, maxX + bodyRadius, minZ - bodyRadius, maxZ + bodyRadius, y, InflatedColor);
                break;
            }
        }
    }

    private static void DrawCircleXz(float cx, float y, float cz, float radius, Color color)
    {
        float step = Mathf.Tau / FootprintCircleSegments;
        for (int i = 0; i < FootprintCircleSegments; i++)
        {
            float a0 = i * step;
            float a1 = a0 + step;
            DebugDraw.Line(
                new Vector3(cx + Mathf.Cos(a0) * radius, y, cz + Mathf.Sin(a0) * radius),
                new Vector3(cx + Mathf.Cos(a1) * radius, y, cz + Mathf.Sin(a1) * radius),
                color);
        }
    }

    private static void DrawBoxXz(Vector3 o, Vector3 halfX, Vector3 halfZ, float y, Color color)
    {
        DrawQuadXz(o - halfX - halfZ, o + halfX - halfZ, o + halfX + halfZ, o - halfX + halfZ, y, color);
    }

    private static void DrawRectXz(float minX, float maxX, float minZ, float maxZ, float y, Color color)
    {
        DrawQuadXz(new Vector3(minX, y, minZ), new Vector3(maxX, y, minZ),
            new Vector3(maxX, y, maxZ), new Vector3(minX, y, maxZ), y, color);
    }

    private static void DrawQuadXz(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float y, Color color)
    {
        a.Y = y;
        b.Y = y;
        c.Y = y;
        d.Y = y;
        DebugDraw.Line(a, b, color);
        DebugDraw.Line(b, c, color);
        DebugDraw.Line(c, d, color);
        DebugDraw.Line(d, a, color);
    }

    // Nearest loaded mob's profile, or the default ground walker if none. The
    // companion following the player is normally the nearest, which is exactly
    // the mob a designer is debugging.
    private static TraversalProfile ProfileForNearestMob(Sim sim, Vector3 center)
    {
        Mob nearest = null;
        float bestSq = float.MaxValue;
        foreach (Mob mob in sim.GetEntities<Mob>())
        {
            float dSq = mob.GlobalPosition.DistanceSquaredTo(center);
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
