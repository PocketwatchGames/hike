using Godot;

// Wireframe shell around every Opening / Barrier voxel near the edit cursor.
// Both types are deliberately invisible in the world — an Opening is the void
// of a doorway or window, a Barrier is solid but produces no surface — so
// without this the editor gives no way to see what has already been marked, or
// to tell a marked doorway from a plain hole in a wall.
//
// Only faces bordering a DIFFERENT voxel type are drawn, so a stacked run of
// markers reads as one outlined doorway rather than a pile of boxes.
//
// Gated by `editor_markers` (on by default). Colours come from the WorldEditor's
// exports so they sit beside the other preview colours in the inspector.
//
// The shells are dense enough that DebugDraw's default occluded treatment (the
// caller's colour at 22% alpha) still reads as loud saturated line work through
// a wall, so both types share one deliberately washed-out `occludedColor`. Which
// type it is stops mattering once it's behind something — that it's THERE is the
// only thing worth showing.
public static class EditorMarkerOverlay
{
    // Pulls the outline off the cell faces, so it neither z-fights the wall the
    // marker is embedded in nor merges with the outline of the marker next door.
    private const float FaceInset = 0.04f;

    // Draws the marker shells within a box around `center`, skipping anything
    // the cutaway has cut away (at or above `clipY`) — an outline floating over
    // a sliced-open room reads as geometry that isn't there.
    public static void Draw(WorldState ws, Vector3 center, float clipY, int radiusXZ, int radiusY,
        Color openingColor, Color barrierColor, Color occludedColor)
    {
        if (ws == null)
        {
            return;
        }

        int cx = Mathf.FloorToInt(center.X);
        int cy = Mathf.FloorToInt(center.Y);
        int cz = Mathf.FloorToInt(center.Z);

        int yMin = cy - radiusY;
        int yMax = cy + radiusY;
        // A cell spans [y, y+1], so it survives the clip only if its top is under
        // it. clipY is +inf with the cutaway parked, which this leaves alone.
        if (clipY < yMax + 1f)
        {
            yMax = Mathf.FloorToInt(clipY) - 1;
        }

        for (int y = yMin; y <= yMax; y++)
        {
            for (int z = cz - radiusXZ; z <= cz + radiusXZ; z++)
            {
                for (int x = cx - radiusXZ; x <= cx + radiusXZ; x++)
                {
                    int type = ws.GetBlockWorld(x, y, z);
                    if (type != Blocks.OpeningId && type != Blocks.BarrierId)
                    {
                        continue;
                    }
                    DrawCell(ws, x, y, z, type,
                        type == Blocks.OpeningId ? openingColor : barrierColor, occludedColor);
                }
            }
        }
    }

    private static void DrawCell(WorldState ws, int x, int y, int z, int type, Color color, Color occludedColor)
    {
        float x0 = x + FaceInset, x1 = x + 1f - FaceInset;
        float y0 = y + FaceInset, y1 = y + 1f - FaceInset;
        float z0 = z + FaceInset, z1 = z + 1f - FaceInset;

        if (ws.GetBlockWorld(x - 1, y, z) != type)
        {
            DrawQuad(new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1), color, occludedColor);
        }
        if (ws.GetBlockWorld(x + 1, y, z) != type)
        {
            DrawQuad(new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1), color, occludedColor);
        }
        if (ws.GetBlockWorld(x, y - 1, z) != type)
        {
            DrawQuad(new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1), color, occludedColor);
        }
        if (ws.GetBlockWorld(x, y + 1, z) != type)
        {
            DrawQuad(new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), color, occludedColor);
        }
        if (ws.GetBlockWorld(x, y, z - 1) != type)
        {
            DrawQuad(new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0), color, occludedColor);
        }
        if (ws.GetBlockWorld(x, y, z + 1) != type)
        {
            DrawQuad(new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), color, occludedColor);
        }
    }

    private static void DrawQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, Color occludedColor)
    {
        DebugDraw.Line(a, b, color, 0f, occludedColor);
        DebugDraw.Line(b, c, color, 0f, occludedColor);
        DebugDraw.Line(c, d, color, 0f, occludedColor);
        DebugDraw.Line(d, a, color, 0f, occludedColor);
    }
}
