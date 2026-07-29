using System;
using Godot;

// TEMPORARY diagnostic: dump the shape-channel decision for a patch of world.
// Console: `grade_debug "<x> <z>"` (world voxel coords, player position is fine).
//
// Prints aligned grids so the stage that diverges is visible directly:
//   H     — live surface height (topmost solid), as a height ramp relative to
//            the patch minimum: 0-9 then a-z, '+' past that
//   M     — the same for the generator's HeightMap, when one is supplied
//   D     — M minus H: '.' equal, else the signed delta ('^' = hm above live,
//            'v' = hm below live, digit = magnitude)
//   G     — grade rule evaluated on LIVE heights (. grade / # snap)
//   R     — grade rule evaluated on the HeightMap, i.e. what the pass decided
//   S     — the shape byte actually on the live surface voxel
//            (. = None/soft, Y = Y-snap, A = All, ? = other)
// R vs S localizes a stamping fault; G vs R localizes a stale height field.
public static class GradeDebug
{
    private const int RADIUS = 8;

    // `ws` lets WorldGen call this at the end of Generate, before Sim exists —
    // a console invocation can't see the world the generation passes built.
    // `hmHeight` / `hmIsGrade` expose the generator's height field for the same
    // reason: from the console there is no HeightMap left to compare against.
    public static void Dump(string arg, WorldState ws = null,
        Func<int, int, int> hmHeight = null, Func<int, int, bool> hmIsGrade = null)
    {
        // The console hands the value over verbatim, surrounding quotes included.
        string[] parts = (arg ?? "").Trim('"').Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out int cx) || !int.TryParse(parts[1], out int cz))
        {
            GD.Print("[grade] usage: grade_debug \"<worldX> <worldZ>\"");
            return;
        }
        ws ??= Sim.Current?.WorldState;
        if (ws == null)
        {
            GD.Print("[grade] no world loaded");
            return;
        }

        int span = RADIUS * 2 + 1;
        var h = new int[span, span];
        for (int ix = 0; ix < span; ix++)
        {
            for (int iz = 0; iz < span; iz++)
            {
                h[ix, iz] = SurfaceHeight(ws, cx - RADIUS + ix, cz - RADIUS + iz);
            }
        }

        int hMin = int.MaxValue;
        foreach (int v in h) { hMin = Math.Min(hMin, v); }

        GD.Print($"[grade] centre=({cx},{cz}) radius={RADIUS}  maxGradeStep assumed 1  patchMinY={hMin}");
        Grid("H (live surface height, ramp from patchMinY)", span, (ix, iz) => Ramp(h[ix, iz] - hMin));
        if (hmHeight != null)
        {
            Grid("M (HeightMap height, same ramp)", span,
                (ix, iz) => Ramp(hmHeight(cx - RADIUS + ix, cz - RADIUS + iz) - hMin));
            Grid("D (HeightMap - live: . equal, ^ above, v below)", span, (ix, iz) =>
            {
                int d = hmHeight(cx - RADIUS + ix, cz - RADIUS + iz) - h[ix, iz];
                if (d == 0) { return "."; }
                if (Math.Abs(d) > 9) { return d > 0 ? "^" : "v"; }
                return Math.Abs(d).ToString();
            });
        }
        Grid("G (rule on LIVE heights: . grade / # snap)", span, (ix, iz) =>
        {
            if (ix == 0 || iz == 0 || ix == span - 1 || iz == span - 1) { return " "; }
            return IsGrade(h, ix, iz, 1) ? "." : "#";
        });
        if (hmIsGrade != null)
        {
            Grid("R (rule on HeightMap = what the pass decided)", span,
                (ix, iz) => hmIsGrade(cx - RADIUS + ix, cz - RADIUS + iz) ? "." : "#");
        }
        Grid("S (stamped shape on surface voxel)", span, (ix, iz) =>
        {
            var s = ws.GetShapeWorld(cx - RADIUS + ix, h[ix, iz], cz - RADIUS + iz);
            if (s == VoxelTypeInfo.SharpAxes.None) { return "."; }
            if (s == VoxelTypeInfo.SharpAxes.All) { return "A"; }
            if (s == VoxelTypeInfo.SharpAxes.Y) { return "Y"; }
            return "?";
        });
        Grid("V (surface voxel type initial)", span, (ix, iz) =>
        {
            VoxelType v = ws.GetVoxelWorld(cx - RADIUS + ix, h[ix, iz], cz - RADIUS + iz);
            return v.ToString().Substring(0, 1);
        });
        CrossSection(ws, cx, cz, h[RADIUS, RADIUS]);
    }

    // Vertical X/Y slice through the centre Z. The plan grids above only ever
    // show one voxel per column, so cave floors, cavern floors and anything
    // under an overhang are invisible in them — this is the view that shows
    // whether the layered pass reached those surfaces.
    private static void CrossSection(WorldState ws, int cx, int cz, int centreY)
    {
        const int Y_ABOVE = 4;
        const int Y_BELOW = 18;
        int span = RADIUS * 2 + 1;

        GD.Print($"[grade] X/Y cross-section at z={cz}  " +
            "(' '=air '~'=water '.'=soft ground 'Y'=snapped ground '#'=other solid)");
        for (int y = centreY + Y_ABOVE; y >= centreY - Y_BELOW; y--)
        {
            var sb = new System.Text.StringBuilder($"[grade]   y={y,4} ");
            for (int ix = 0; ix < span; ix++)
            {
                int wx = cx - RADIUS + ix;
                VoxelType v = ws.GetVoxelWorld(wx, y, cz);
                if (v == VoxelType.Air) { sb.Append(' '); continue; }
                if (v == VoxelType.Water) { sb.Append('~'); continue; }
                if (v != VoxelType.Terrain && v != VoxelType.Desert && v != VoxelType.Marsh)
                {
                    sb.Append('#');
                    continue;
                }
                sb.Append(ws.GetShapeWorld(wx, y, cz) == VoxelTypeInfo.SharpAxes.None ? '.' : 'Y');
            }
            GD.Print(sb.ToString());
        }
    }

    // Single-char height ramp so rows stay column-aligned regardless of sign
    // or magnitude (a raw `% 10` prints "-2" and shifts every later column).
    private static string Ramp(int rel)
    {
        if (rel < 0) { return "-"; }
        if (rel < 10) { return ((char)('0' + rel)).ToString(); }
        if (rel < 36) { return ((char)('a' + rel - 10)).ToString(); }
        return "+";
    }

    // Same per-axis test WorldGen.HeightMap.IsGrade applies, evaluated against
    // the live world so a mismatch with S localizes the failing stamper.
    private static bool IsGrade(int[,] h, int ix, int iz, int maxStep)
    {
        int c = h[ix, iz];
        return Axis(c, h[ix - 1, iz], h[ix + 1, iz], maxStep)
            || Axis(c, h[ix, iz - 1], h[ix, iz + 1], maxStep);
    }

    private static bool Axis(int c, int lo, int hi, int maxStep)
    {
        return Math.Abs(lo - c) <= maxStep && Math.Abs(hi - c) <= maxStep && (lo != c || hi != c);
    }

    // Topmost solid voxel in the column, searched from the world ceiling down.
    // ws.Min/Max are CHUNK coords, not voxel coords — scanning them directly
    // searches a ~SIZE-tall window around y=0 and reports a buried voxel (or
    // nothing) for every real column.
    private static int SurfaceHeight(WorldState ws, int wx, int wz)
    {
        int minY = ws.Min.Y * ChunkState.SIZE;
        int maxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        for (int y = maxY; y >= minY; y--)
        {
            VoxelType v = ws.GetVoxelWorld(wx, y, wz);
            if (VoxelTypeInfo.IsSolid(v))
            {
                return y;
            }
        }
        return minY;
    }

    private static void Grid(string label, int span, Func<int, int, string> cell)
    {
        GD.Print($"[grade] {label}   (rows = +Z, cols = +X)");
        for (int iz = span - 1; iz >= 0; iz--)
        {
            var sb = new System.Text.StringBuilder("[grade]   ");
            for (int ix = 0; ix < span; ix++)
            {
                sb.Append(cell(ix, iz));
            }
            GD.Print(sb.ToString());
        }
    }
}
