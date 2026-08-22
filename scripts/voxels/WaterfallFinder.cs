using System;
using System.Collections.Generic;
using Godot;

// One metre of the edge water pours over: the AIR column it pours into and the
// horizontal direction it leaves in. A cascade is a LINE of these, and the sheet
// is swept from them — the fall is a jet leaving a lip, not the block of water
// that would stand in the drop.
//
// Direction is one of the four axis steps, pointing AWAY from the pool that
// feeds this column, so the sweep knows which way is "out over the edge".
public readonly struct WaterfallLip
{
    public readonly int X;
    public readonly int Z;
    public readonly int DirX;
    public readonly int DirZ;

    public WaterfallLip(int x, int z, int dirX, int dirZ)
    {
        X = x;
        Z = z;
        DirX = dirX;
        DirZ = dirZ;
    }
}

// One cascade: where the water leaves the lip, where it lands, and how wide the
// sheet is.
public readonly struct WaterfallSite
{
    // Centre of the sheet in world XZ, at the Y of the topmost water VOXEL it
    // pours from.
    public readonly Vector3 Top;

    // Topmost occupied VOXEL the sheet lands on — the pool below, or the bed if
    // it lands dry.
    public readonly int BottomY;

    // Columns the sheet spans, so a five-wide fall reads as one wide effect
    // rather than five narrow ones stacked side by side.
    public readonly int Columns;

    // The edge the water actually pours over, each metre-wide step carrying the
    // direction it leaves in.
    public readonly IReadOnlyList<WaterfallLip> Lips;

    public WaterfallSite(Vector3 top, int bottomY, int columns, IReadOnlyList<WaterfallLip> lips)
    {
        Top = top;
        BottomY = bottomY;
        Columns = columns;
        Lips = lips ?? Array.Empty<WaterfallLip>();
    }

    public int Height => Mathf.RoundToInt(Top.Y) - BottomY;
}

// Where the world's cascades are, read straight off the finished voxels.
//
// ONE rule, and it is the whole rule: wherever a water voxel sits beside an air
// voxel, the water pours into that air column. The lip is the TOPMOST air voxel
// of that span with water beside it, and the sheet runs from there down to the
// floor of the span.
//
// The voxels are the only honest source. This used to be derived from the fields
// that PRODUCED the world instead — worldgen from a scratch water surface walked
// down its own staircase, the map painter from its painted height and water
// layers — so each saw only the falls its own pass had a notion of, and neither
// saw one made by anything else: a tunnel breaching a lake, a stamped scene, a
// hand-edited voxel, a pool spilling into a shaft it never routed. Reading the
// finished world sees all of them and needs nothing from the process that built
// it, which is why one implementation serves worldgen and the painter's bake.
public static class WaterfallFinder
{
    // Voxel classes the rule cares about. Air is 0 so an absent chunk reads as
    // air, matching WorldState.GetBlockWorld.
    private const byte Air = 0;
    private const byte Water = 1;
    private const byte Barrier = 2;

    // Neighbour offsets. A lip pours AWAY from whichever of these holds the
    // pool, so the direction it carries is the offset negated.
    private static readonly int[] NeighbourDx = { 1, -1, 0, 0 };
    private static readonly int[] NeighbourDz = { 0, 0, 1, -1 };

    // One lip before the cascades are grouped: the way out, and the floor of the
    // air span it pours down.
    private readonly struct RawLip
    {
        public readonly int DirX;
        public readonly int DirZ;
        public readonly int BaseY;

        public RawLip(int dirX, int dirZ, int baseY)
        {
            DirX = dirX;
            DirZ = dirZ;
            BaseY = baseY;
        }
    }

    // `minFallHeight` drops cascades too short to be drawn before they become
    // entities — a one-voxel step off a pool edge is a rapid, and a world holds
    // thousands of them.
    public static List<WaterfallSite> Find(WorldState ws, float minFallHeight)
    {
        int minX = ws.Min.X * ChunkState.SIZE;
        int maxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int minY = ws.Min.Y * ChunkState.SIZE;
        int maxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int minZ = ws.Min.Z * ChunkState.SIZE;
        int maxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;
        int ySpan = maxY - minY + 1;
        int zSpan = maxZ - minZ + 1;

        // Three rolling YZ slices — the column being walked and its two X
        // neighbours — so every voxel is classified exactly once and all four
        // neighbour tests are array reads.
        byte[] prev = new byte[zSpan * ySpan];
        byte[] cur = new byte[zSpan * ySpan];
        byte[] next = new byte[zSpan * ySpan];
        FillSlice(ws, minX - 1, prev, minY, minZ, ySpan, zSpan);
        FillSlice(ws, minX, cur, minY, minZ, ySpan, zSpan);

        // Keyed by lip column AND the level it pours from: one column can be the
        // low side of two different pools.
        var byCell = new Dictionary<(int X, int Z, int Top), List<RawLip>>();
        var lipY = new int[4];
        for (int wx = minX; wx <= maxX; wx++)
        {
            FillSlice(ws, wx + 1, next, minY, minZ, ySpan, zSpan);
            for (int lz = 0; lz < zSpan; lz++)
            {
                int col = lz * ySpan;
                bool open = false;
                // One past the bottom, so a span running off the floor of the
                // world closes on the same path as every other.
                for (int ly = ySpan - 1; ly >= -1; ly--)
                {
                    if (ly >= 0 && cur[col + ly] == Air)
                    {
                        if (!open)
                        {
                            open = true;
                            lipY[0] = lipY[1] = lipY[2] = lipY[3] = -1;
                        }
                        // Topmost air in this span with water beside it, kept
                        // per side: two pools at different levels spilling into
                        // the same shaft are two falls.
                        if (lipY[0] < 0 && next[col + ly] == Water) { lipY[0] = ly; }
                        if (lipY[1] < 0 && prev[col + ly] == Water) { lipY[1] = ly; }
                        if (lipY[2] < 0 && lz + 1 < zSpan && cur[col + ySpan + ly] == Water) { lipY[2] = ly; }
                        if (lipY[3] < 0 && lz > 0 && cur[col - ySpan + ly] == Water) { lipY[3] = ly; }
                        continue;
                    }
                    if (!open) { continue; }
                    open = false;
                    int baseY = ly + 1 + minY;
                    for (int d = 0; d < 4; d++)
                    {
                        if (lipY[d] < 0) { continue; }
                        var key = (wx, lz + minZ, lipY[d] + minY);
                        if (!byCell.TryGetValue(key, out List<RawLip> cell))
                        {
                            cell = new List<RawLip>();
                            byCell[key] = cell;
                        }
                        cell.Add(new RawLip(-NeighbourDx[d], -NeighbourDz[d], baseY));
                    }
                }
            }
            (prev, cur, next) = (cur, next, prev);
        }

        List<WaterfallSite> sites = GroupSites(byCell, minFallHeight);
        var parts = new List<string>();
        foreach (WaterfallSite site in sites)
        {
            parts.Add($"({site.Top.X:F0}, {site.Top.Y:F0}, {site.Top.Z:F0})"
                + $" {site.Height}v/{site.Columns}col");
        }
        GD.Print($"[Waterfalls] {byCell.Count} lip columns -> {sites.Count} cascades"
            + $" at or over {minFallHeight}m: {string.Join("; ", parts)}");
        return sites;
    }

    // Classify one world column of voxels into `slice`, indexed [lz * ySpan + ly].
    // Walks the chunks covering the column rather than asking per voxel: the
    // world is tens of millions of voxels and the per-voxel chunk lookup would be
    // the whole cost.
    private static void FillSlice(WorldState ws, int wx, byte[] slice,
        int minY, int minZ, int ySpan, int zSpan)
    {
        Array.Clear(slice, 0, slice.Length);
        int cx = FloorDiv(wx, ChunkState.SIZE);
        int lx = Mod(wx, ChunkState.SIZE);
        for (int cy = ws.Min.Y; cy <= ws.Max.Y; cy++)
        {
            for (int cz = ws.Min.Z; cz <= ws.Max.Z; cz++)
            {
                if (!ws._chunks.TryGetValue(new Vector3I(cx, cy, cz), out ChunkState chunk))
                {
                    continue;
                }
                int baseY = cy * ChunkState.SIZE - minY;
                int baseZ = cz * ChunkState.SIZE - minZ;
                for (int vz = 0; vz < ChunkState.SIZE; vz++)
                {
                    int row = (baseZ + vz) * ySpan + baseY;
                    for (int vy = 0; vy < ChunkState.SIZE; vy++)
                    {
                        int id = chunk.Voxels[lx, vy, vz];
                        slice[row + vy] = Blocks.IsWater(id) ? Water
                            : Blocks.IsEmpty(id) ? Air : Barrier;
                    }
                }
            }
        }
    }

    // Group lips into cascades — one per sheet, not one per column. A five-wide
    // fall is one waterfall and wants one effect across it.
    //
    // 8-connected and BY THE LEVEL THEY POUR FROM. Diagonally, because an outside
    // corner turns through a diagonal and the two perpendicular strips must reach
    // the same entity or the mesh builder cannot skirt the wedge between them. By
    // level, because two pools at different heights spilling past each other are
    // two falls, and merging them would put one sheet's top at the other's water.
    private static List<WaterfallSite> GroupSites(
        Dictionary<(int X, int Z, int Top), List<RawLip>> byCell, float minFallHeight)
    {
        var sites = new List<WaterfallSite>();
        var seen = new HashSet<(int X, int Z, int Top)>();
        var open = new Queue<(int X, int Z, int Top)>();
        var members = new List<WaterfallLip>();
        var cells = new List<(int X, int Z)>();
        foreach ((int X, int Z, int Top) start in byCell.Keys)
        {
            if (!seen.Add(start)) { continue; }
            open.Clear();
            open.Enqueue(start);
            members.Clear();
            cells.Clear();
            int bottom = int.MaxValue;
            long sumX = 0;
            long sumZ = 0;
            while (open.Count > 0)
            {
                (int X, int Z, int Top) key = open.Dequeue();
                cells.Add((key.X, key.Z));
                sumX += key.X;
                sumZ += key.Z;
                foreach (RawLip lip in byCell[key])
                {
                    members.Add(new WaterfallLip(key.X, key.Z, lip.DirX, lip.DirZ));
                    // The DEEPEST landing under the sheet: a fall over uneven
                    // ground reaches the bottom of what it spans.
                    bottom = Math.Min(bottom, lip.BaseY - 1);
                }
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var neighbour = (key.X + dx, key.Z + dz, key.Top);
                        if (byCell.ContainsKey(neighbour) && seen.Add(neighbour))
                        {
                            open.Enqueue(neighbour);
                        }
                    }
                }
            }

            // Both ends name a water SURFACE, and a surface sits one voxel above
            // the topmost voxel it caps — these are voxels.
            if (start.Top - bottom < minFallHeight) { continue; }

            // The centroid of a curved or L-shaped lip line is not one of its own
            // columns — it lands in the pool the fall hangs off, and the entity
            // would file into the chunk there. Snap to the nearest member, the
            // same fix the POI resolver makes.
            float avgX = sumX / (float)cells.Count;
            float avgZ = sumZ / (float)cells.Count;
            (int X, int Z) best = cells[0];
            float bestD = float.MaxValue;
            foreach ((int X, int Z) cell in cells)
            {
                float dx = cell.X - avgX;
                float dz = cell.Z - avgZ;
                if (dx * dx + dz * dz < bestD)
                {
                    bestD = dx * dx + dz * dz;
                    best = cell;
                }
            }
            sites.Add(new WaterfallSite(
                new Vector3(best.X + 0.5f, start.Top, best.Z + 0.5f),
                bottom, cells.Count, members.ToArray()));
        }
        return sites;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ((a + 1) / b) - 1;

    private static int Mod(int a, int b) => ((a % b) + b) % b;
}
