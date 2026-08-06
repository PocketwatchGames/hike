using System.Collections.Generic;
using Godot;

// Stage 1 visualisation for the cell-decomposition cutaway, and the console dump
// of the region table. Nothing here touches a clip path.
//
// This exists to be LOOKED AT before anything renders from it: a wrong join and a
// wrong cut height produce the same symptom once geometry starts disappearing,
// and are trivial to tell apart here.
//
// It draws OUTLINES, not cells. Drawing every cell as a tile was technically
// complete and unreadable — several hundred rainbow quads, and the one question
// the picture has to answer ("does this patch match a space I'd name out loud")
// is a question about a BOUNDARY. So the default view is the outline of the
// region the player is standing in, plus a second outline at the height that
// region would cut at, and nothing else. Per-cell tiles survive at level 3 for
// when the question really is about an individual cell.
//
// Drawn from Sim._Process around the player and from WorldEditor around the edit
// cursor, exactly like NavGridDebug: the editor is where the test structures get
// loaded, and it has no player for Sim's call to fire from.
public static class CellRegionDebug
{
    public enum ELevel
    {
        Off = 0,
        // Just the player's region: its floor outline, its cut plane, its walls.
        Mine = 1,
        // Plus every other region at the player's elevation, outlined dimly.
        Nearby = 2,
        // Plus per-cell tiles. The everything view; busy by nature.
        Cells = 3,
    }

    // Vertical reach either side of the player for levels 2 and 3. A cell whose
    // air run doesn't reach into this band is somebody else's storey and is pure
    // clutter — burying the picture under buried caves and under-floor voids is
    // what made the first version unreadable. Wide enough to keep a balcony deck
    // and the floor below it on screen together, which is the case that most
    // needs comparing.
    private const float ELEVATION_BAND = 6f;
    // Lift outlines off the surfaces they sit on so they don't z-fight.
    private const float SURFACE_LIFT = 0.04f;
    // Inset for per-cell tiles at level 3, so neighbouring cells read as separate
    // tiles. Outlines use no inset: their whole job is to join up across cells
    // into one continuous boundary.
    private const float CELL_INSET = 0.12f;
    private const float SKY_STUB_HEIGHT = 0.6f;
    // Marker at the cut plane over each column the wall flood claimed.
    private const float WALL_MARK_SIZE = 0.3f;
    // Rows in the console dump. Outdoors is one enormous sky region plus a long
    // tail of one-cell slivers; the tail is noise once the head is readable.
    private const int DUMP_ROWS = 24;

    private static readonly Vector3[] QuadBuffer = new Vector3[5];

    // Armed by the clip_cell_dump cvar, consumed by the next tick that has a live
    // field. Deferred rather than immediate so the command also works from the
    // command line, where it runs in Main._Ready — long before a world exists —
    // which is the only way an unattended headless run can ask for the table.
    public static bool DumpPending;

    public static void Draw(CellField field, CellRegions regions, Vector3 center,
        ELevel level, int radius, bool includeSky)
    {
        if (field == null || regions == null || level == ELevel.Off || radius <= 0)
        {
            return;
        }
        int cx = Mathf.FloorToInt(center.X);
        int cz = Mathf.FloorToInt(center.Z);

        // The player's own region first and unconditionally, so it is legible
        // even where it runs past the radius the other levels are bounded by.
        DrawRegionOutline(field, regions, regions.PlayerRegion, cx, cz, radius, mine: true);
        DrawWallMarks(field, regions, cx, cz, radius);
        if (level == ELevel.Mine)
        {
            return;
        }

        float lowY = center.Y - ELEVATION_BAND;
        float highY = center.Y + ELEVATION_BAND;
        var drawn = new HashSet<int>();
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (!field.TryColumn(cx + dx, cz + dz, out int gx, out int gz))
                {
                    continue;
                }
                int count = field.CountAt(gx, gz);
                for (int slot = 0; slot < count; slot++)
                {
                    Cell cell = field.CellAt(gx, gz, slot);
                    if (!InBand(cell, lowY, highY) || (cell.IsSky && !includeSky))
                    {
                        continue;
                    }
                    int label = regions.LabelAt(gx, gz, slot);
                    if (label < 0 || label == regions.PlayerRegion)
                    {
                        continue;
                    }
                    if (level == ELevel.Cells)
                    {
                        DrawCellTile(field, regions, gx, gz, cell, label);
                    }
                    else if (drawn.Add(label))
                    {
                        DrawRegionOutline(field, regions, label, cx, cz, radius, mine: false);
                    }
                }
            }
        }
    }

    // The boundary of one region at its cells' own floor heights, plus a second
    // outline at the plane it would cut at.
    //
    // An edge is drawn where the neighbouring column holds no cell of this region
    // — so adjacent cells of one region contribute nothing between them and the
    // result is a single closed outline around the space, not a grid. That is the
    // whole readability win: a room becomes one shape instead of forty tiles.
    //
    // The cut outline is the more useful of the two. Seeing it float three metres
    // above the ceiling of the room you are standing in is what makes a rounding
    // problem obvious; no table of numbers lands that as quickly.
    private static void DrawRegionOutline(CellField field, CellRegions regions, int label,
        int cx, int cz, int radius, bool mine)
    {
        if (label < 0 || label >= regions.Regions.Count)
        {
            return;
        }
        CellRegion region = regions.Regions[label];
        Color color = RegionColor(region, mine);
        Color cutColor = color * (mine ? 0.75f : 0.4f);
        cutColor.A = 1f;
        bool drawCut = !region.IsSky;

        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (!field.TryColumn(cx + dx, cz + dz, out int gx, out int gz))
                {
                    continue;
                }
                int count = field.CountAt(gx, gz);
                for (int slot = 0; slot < count; slot++)
                {
                    if (regions.LabelAt(gx, gz, slot) != label)
                    {
                        continue;
                    }
                    float floorY = field.CellAt(gx, gz, slot).FloorY + SURFACE_LIFT;
                    for (int side = 0; side < 4; side++)
                    {
                        if (ColumnHasLabel(field, regions, gx, gz, side, label))
                        {
                            continue;
                        }
                        Edge(field, gx, gz, side, floorY, color);
                        if (drawCut)
                        {
                            Edge(field, gx, gz, side, region.CutHeight, cutColor);
                        }
                    }
                }
            }
        }
    }

    private static void DrawWallMarks(CellField field, CellRegions regions, int cx, int cz, int radius)
    {
        if (regions.PlayerRegion < 0 || regions.PlayerRegion >= regions.Regions.Count)
        {
            return;
        }
        // Red once the flood ran to its safety bound rather than terminating on a
        // wall's far face — the claim past that point is arbitrary, and the whole
        // reason to look at these marks is to see where that starts.
        Color color = regions.WallClaimHitBudget ? new Color(1f, 0.3f, 0.25f) : new Color(1f, 0.85f, 0.2f);
        float y = regions.Regions[regions.PlayerRegion].CutHeight;
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (!field.TryColumn(cx + dx, cz + dz, out int gx, out int gz) || !regions.IsWallColumn(gx, gz))
                {
                    continue;
                }
                float x = field.WorldX(gx) + 0.5f;
                float z = field.WorldZ(gz) + 0.5f;
                DebugDraw.Line(new Vector3(x - WALL_MARK_SIZE, y, z), new Vector3(x + WALL_MARK_SIZE, y, z), color);
                DebugDraw.Line(new Vector3(x, y, z - WALL_MARK_SIZE), new Vector3(x, y, z + WALL_MARK_SIZE), color);
            }
        }
    }

    // Level 3 only: one cell as a floor quad, a ceiling quad and a corner post.
    private static void DrawCellTile(CellField field, CellRegions regions, int gx, int gz, in Cell cell, int label)
    {
        Color color = RegionColor(regions.Regions[label], mine: false);
        float x0 = field.WorldX(gx) + CELL_INSET;
        float x1 = field.WorldX(gx) + 1f - CELL_INSET;
        float z0 = field.WorldZ(gz) + CELL_INSET;
        float z1 = field.WorldZ(gz) + 1f - CELL_INSET;
        float floorY = cell.FloorY + SURFACE_LIFT;

        Quad(x0, x1, z0, z1, floorY, color);
        // An authored Opening joins nothing, so it never shares a colour with the
        // rooms it separates. Crossed through so the reason is visible rather than
        // reading as an unexplained singleton.
        if (cell.IsOpening)
        {
            DebugDraw.Line(new Vector3(x0, floorY, z0), new Vector3(x1, floorY, z1), color);
            DebugDraw.Line(new Vector3(x0, floorY, z1), new Vector3(x1, floorY, z0), color);
        }
        float topY = cell.IsSky ? cell.FloorY + SKY_STUB_HEIGHT : cell.CeilingY;
        if (!cell.IsSky)
        {
            Quad(x0, x1, z0, z1, topY, color);
        }
        DebugDraw.Line(new Vector3(x0, floorY, z0), new Vector3(x0, topY, z0), color);
    }

    // Does the column on this side hold a cell of the same region? If not, the
    // shared edge is a region boundary and gets drawn.
    private static bool ColumnHasLabel(CellField field, CellRegions regions, int gx, int gz, int side, int label)
    {
        int nx = gx + (side == 0 ? 1 : side == 1 ? -1 : 0);
        int nz = gz + (side == 2 ? 1 : side == 3 ? -1 : 0);
        if (nx < 0 || nz < 0 || nx >= CellField.GRID_SIZE || nz >= CellField.GRID_SIZE)
        {
            return false;
        }
        int count = field.CountAt(nx, nz);
        for (int slot = 0; slot < count; slot++)
        {
            if (regions.LabelAt(nx, nz, slot) == label)
            {
                return true;
            }
        }
        return false;
    }

    // The shared edge between this column and the one on `side`, at height y.
    private static void Edge(CellField field, int gx, int gz, int side, float y, Color color)
    {
        float x0 = field.WorldX(gx);
        float z0 = field.WorldZ(gz);
        float x1 = x0 + 1f;
        float z1 = z0 + 1f;
        Vector3 a;
        Vector3 b;
        switch (side)
        {
            case 0: a = new Vector3(x1, y, z0); b = new Vector3(x1, y, z1); break;
            case 1: a = new Vector3(x0, y, z0); b = new Vector3(x0, y, z1); break;
            case 2: a = new Vector3(x0, y, z1); b = new Vector3(x1, y, z1); break;
            default: a = new Vector3(x0, y, z0); b = new Vector3(x1, y, z0); break;
        }
        DebugDraw.Line(a, b, color);
    }

    private static bool InBand(in Cell cell, float lowY, float highY)
    {
        float top = cell.IsSky ? float.PositiveInfinity : cell.CeilingY;
        return cell.FloorY <= highY && top >= lowY;
    }

    private static void Quad(float x0, float x1, float z0, float z1, float y, Color color)
    {
        QuadBuffer[0] = new Vector3(x0, y, z0);
        QuadBuffer[1] = new Vector3(x1, y, z0);
        QuadBuffer[2] = new Vector3(x1, y, z1);
        QuadBuffer[3] = new Vector3(x0, y, z1);
        QuadBuffer[4] = QuadBuffer[0];
        DebugDraw.Lines(QuadBuffer, color);
    }

    // Keyed off the region's lexicographically-first cell in WORLD coords, NOT
    // its id: ids are assigned in scan order and re-assigned every tick, so an
    // id-derived colour strobes as the window scrolls. The world key is stable,
    // so a region keeps its colour until its shape actually changes — which is
    // what makes "did that just split?" readable.
    //
    // The player's own region is the only saturated thing on screen. Everything
    // else is dim, so the picture separates on VALUE before it separates on hue
    // and stays readable however many regions are in frame.
    private static Color RegionColor(in CellRegion region, bool mine)
    {
        if (region.IsSky)
        {
            return new Color(0.35f, 0.4f, 0.45f, 1f);
        }
        int n = region.KeyX * 374761393 + region.KeyZ * 668265263 + region.KeyFloorY * 1274126177;
        n = (n ^ (n >> 13)) * 1274126177;
        float hue = ((n ^ (n >> 16)) & 0xFFFF) / 65535f;
        return mine ? Color.FromHsv(hue, 0.5f, 1f) : Color.FromHsv(hue, 0.6f, 0.42f);
    }

    // One-shot console report. Reads as the answer to "do these regions match
    // intuition" — counts, the ceiling spread that decides whether cut-height
    // lumps are actually bounded, and whether the region is truncated by the
    // window (in which case its cut height is measured over the resident part
    // only and will drift as the player walks, with no visible cause).
    public static void Dump(CellField field, CellRegions regions, Vector3 center)
    {
        if (field == null || regions == null)
        {
            GD.Print("[cell_regions] no field — set clip_cell_debug and stand in a loaded world.");
            return;
        }
        int enclosed = 0;
        int sky = 0;
        int truncated = 0;
        foreach (CellRegion r in regions.Regions)
        {
            if (r.IsSky) { sky++; } else { enclosed++; }
            if (r.TouchesWindowEdge) { truncated++; }
        }

        GD.Print($"[cell_regions] center=({Mathf.FloorToInt(center.X)},{Mathf.FloorToInt(center.Y)},{Mathf.FloorToInt(center.Z)}) "
            + $"regions={regions.Regions.Count} (enclosed={enclosed} sky={sky} truncated={truncated}) "
            + $"scannedColumns={field.ScannedColumns} truncatedColumns={field.TruncatedColumns} "
            + $"clearanceBucket={regions.UseClearanceBucket} step={regions.StepVoxels}");
        GD.Print($"[cell_regions] playerRegion={regions.PlayerRegion} seed={regions.SeedSource} "
            + $"wallColumns={regions.WallColumnsClaimed} wallDepth={regions.WallClaimDepth} "
            + $"wallHitBudget={regions.WallClaimHitBudget}");

        // The player's own region, spelled out. It is the only row that decides
        // anything, and reading it off a 24-row table sorted by size is work.
        if (regions.PlayerRegion >= 0 && regions.PlayerRegion < regions.Regions.Count)
        {
            CellRegion me = regions.Regions[regions.PlayerRegion];
            if (me.IsSky)
            {
                GD.Print("[cell_regions] YOU ARE IN A SKY REGION — sky never cuts, so nothing here is armed. "
                    + "Go inside something to evaluate this.");
            }
            else
            {
                GD.Print($"[cell_regions] YOUR REGION: {me.CellCount} cells, floor {me.MinFloorY}..{me.MaxFloorY}, "
                    + $"ceiling {me.MinCeilingY}..{me.MaxCeilingY}, would cut at {me.CutHeight}"
                    + $"{(me.MaxCeilingY < me.CutHeight ? $" — {me.CutHeight - me.MaxCeilingY}m ABOVE its own ceiling" : "")}"
                    + $"{(me.TouchesWindowEdge ? ", TRUNCATED by the window (cut height will drift)" : "")}");
            }
        }

        GD.Print("[cell_regions]   id  cells  floorY        ceilingY      cut   edge  key");
        var ordered = new List<CellRegion>(regions.Regions);
        ordered.Sort((a, b) => b.CellCount.CompareTo(a.CellCount));
        int rows = Mathf.Min(ordered.Count, DUMP_ROWS);
        for (int i = 0; i < rows; i++)
        {
            CellRegion r = ordered[i];
            string ceilings = r.IsSky
                ? "sky"
                : $"{r.MinCeilingY}..{r.MaxCeilingY} (spread {r.MaxCeilingY - r.MinCeilingY})";
            string cut = r.IsSky ? "-" : r.CutHeight.ToString();
            string mark = r.Id == regions.PlayerRegion ? "*" : " ";
            GD.Print($"[cell_regions] {mark}{r.Id,4} {r.CellCount,6}  {r.MinFloorY}..{r.MaxFloorY,-8}  {ceilings,-22}  {cut,-5} "
                + $"{(r.TouchesWindowEdge ? "EDGE" : "    ")}  ({r.KeyX},{r.KeyFloorY},{r.KeyZ})");
        }
        if (ordered.Count > rows)
        {
            GD.Print($"[cell_regions] ... {ordered.Count - rows} smaller regions omitted");
        }
    }
}
