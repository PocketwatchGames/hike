using System;
using System.Collections.Generic;
using Godot;

// Builds the invisible barriers that stand at the top edge of every drop taller
// than a legal step, so a body simply cannot walk off one.
//
// This is the geometric alternative to constraining velocity per tick. The
// appeal is not that it is cheaper — it is that "slide along the edge instead
// of falling" stops being something we compute at all. The barrier IS a wall,
// so MoveAndSlide produces the slide, with true geometric normals, correct
// multi-contact behaviour at corners, and no lookahead probe to snag on jagged
// terrain. Every failure mode of the per-tick approach (estimated normals,
// corner cutting, iteration that may not converge, choosing a reference height,
// sizing a probe against the capsule radius) is structurally absent here.
//
// Barriers are emitted for every chunk regardless of whether anything currently
// collides with them; opting in is a collision-mask decision on the body. That
// keeps the A/B against the per-tick guard instant instead of requiring a world
// rebuild.
public static class LedgeBarrierMesher
{
    private const int N = ChunkState.SIZE;

    // How far a barrier rises above the surface it guards, in metres. Must
    // exceed the movement capsule's height (1.5) so it cannot be climbed by the
    // step-up lift, which raises the body by stepHeight before moving.
    private const float BarrierHeight = 2f;

    // Drop of the base below the surface points the wall is strung between, so a
    // cell that snapped down between them cannot open a gap underneath.
    private const float BaseSink = 0.6f;

    // How far the wall is pulled back from the contour, in metres. Bounded above
    // by the movement capsule's radius doubled (0.5) or a one-voxel ledge with
    // drops on both sides stops being walkable.
    private const float LedgeInset = 0.2f;


    private static readonly Vector3I[] Horizontal =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1),
    };

    // Marching-squares edge table over the four cell-centre samples of a 2x2
    // block. Bit 0 = (x,z), 1 = (x+1,z), 2 = (x+1,z+1), 3 = (x,z+1); the four
    // crossing points E0..E3 lie between consecutive samples going round.
    // -1 terminates. The two diagonal cases resolve with the centre treated as
    // INSIDE, so a diagonal run of walkable cells stays connected instead of
    // being pinched into separate islands.
    private static readonly int[][] MarchCases =
    {
        new[] { -1 },              // 0000
        new[] { 0, 3, -1 },        // 0001
        new[] { 0, 1, -1 },        // 0010
        new[] { 1, 3, -1 },        // 0011
        new[] { 1, 2, -1 },        // 0100
        new[] { 0, 1, 2, 3 },      // 0101 diagonal
        new[] { 0, 2, -1 },        // 0110
        new[] { 2, 3, -1 },        // 0111
        new[] { 2, 3, -1 },        // 1000
        new[] { 0, 2, -1 },        // 1001
        new[] { 0, 3, 1, 2 },      // 1010 diagonal
        new[] { 1, 2, -1 },        // 1011
        new[] { 1, 3, -1 },        // 1100
        new[] { 0, 1, -1 },        // 1101
        new[] { 0, 3, -1 },        // 1110
        new[] { -1 },              // 1111
    };

    // Crossing point of edge `e` of the block anchored at (x, z), in cell-centre
    // sample space, already pulled toward the walkable side by LedgeInset.
    //
    // The inset is applied HERE, to the shared point, rather than to each
    // segment afterwards. Offsetting whole segments along their own normals
    // moves a shared endpoint to two different places — that is what tore gaps
    // at convex turns, and what padding the ends to close them turned into
    // heavy overlap on every straight joint. An edge belongs to exactly two
    // blocks and both derive the same displacement from it (the direction toward
    // whichever of its two samples is walkable), so the point simply moves, and
    // the contour stays watertight with no padding at all.
    private static Vector2 EdgePoint(int e, int x, int z, bool[] inside)
    {
        switch (e)
        {
            // E0 spans samples 0..1 along X; E2 spans 3..2 along X.
            case 0: return new Vector2(x + 1.0f + (inside[0] ? -LedgeInset : LedgeInset), z + 0.5f);
            case 2: return new Vector2(x + 1.0f + (inside[2] ? LedgeInset : -LedgeInset), z + 1.5f);
            // E1 spans samples 1..2 along Z; E3 spans 3..0 along Z.
            case 1: return new Vector2(x + 1.5f, z + 1.0f + (inside[1] ? -LedgeInset : LedgeInset));
            default: return new Vector2(x + 0.5f, z + 1.0f + (inside[3] ? LedgeInset : -LedgeInset));
        }
    }

    // The dual-contoured ground point for the cell standing at (x, y, z), in
    // chunk-local space. The surface vertex for a floor can land in the cell
    // below the air or in the air cell itself depending on where the density
    // crossing fell, so both are tried and the one nearest this level wins.
    private static bool TryGroundPoint(DcCellSurface surface, int x, int y, int z, out Vector3 p)
    {
        bool below = surface.TryGetLocal(x, y - 1, z, out Vector3 pb);
        bool here = surface.TryGetLocal(x, y, z, out Vector3 ph);
        if (below && here)
        {
            p = Mathf.Abs(pb.Y - y) <= Mathf.Abs(ph.Y - y) ? pb : ph;
            return true;
        }
        if (below) { p = pb; return true; }
        if (here) { p = ph; return true; }
        p = default;
        return false;
    }

    // Emits one wall segment, extruded from the ground it guards. The points
    // arrive already inset, so nothing is offset or padded here.
    private static void AddSegment(List<Vector3> tris, Vector2 p0, Vector2 p1, float groundY)
    {
        Vector2 a2 = p0;
        Vector2 b2 = p1;

        Vector3 a = new(a2.X, groundY - BaseSink, a2.Y);
        Vector3 b = new(b2.X, groundY - BaseSink, b2.Y);
        Vector3 aTop = new(a2.X, groundY + BarrierHeight, a2.Y);
        Vector3 bTop = new(b2.X, groundY + BarrierHeight, b2.Y);

        // Winding is irrelevant here — the shape is built with backface
        // collision on, since a barrier must be felt from whichever side a body
        // reaches it.
        tris.Add(a); tris.Add(b); tris.Add(bTop);
        tris.Add(a); tris.Add(bTop); tris.Add(aTop);
    }

    // True when (x,y,z) is a surface a body stands ON: air here, solid beneath.
    private static bool IsSurface(System.Func<int, int, int, int> getVoxel, int x, int y, int z)
    {
        return !Blocks.IsSolid(getVoxel(x, y, z)) && Blocks.IsSolid(getVoxel(x, y - 1, z));
    }

    // Does the neighbouring column offer footing within a legal step of `y`?
    // A solid neighbour at `y` is a wall, which needs no barrier because terrain
    // collision already stops the body.
    //
    // A water surface within a legal step is reachable HOWEVER DEEP the column
    // beneath it is: entering water is a wade or a swim, never a fall, and the
    // body can raise itself back to the waterline unaided (Player's swim
    // step-up). So depth is not consulted — which is what keeps a barrier from
    // standing at every line where shallow water meets deep. Only the surface
    // being more than a step BELOW us is a real drop, and that is judged the
    // same way for water as for ground.
    private static bool NeighbourIsReachable(System.Func<int, int, int, int> getVoxel, int nx, int y, int nz,
        int maxLegalDropVoxels)
    {
        // Only has to cover the legal drop plus a voxel of slack; anything
        // deeper is a ledge regardless of how much deeper.
        int probeDepth = maxLegalDropVoxels + 1;
        if (Blocks.IsSolid(getVoxel(nx, y, nz)))
        {
            return true;
        }
        for (int d = 0; d <= probeDepth; d++)
        {
            int ny = y - d;
            if (Blocks.IsWater(getVoxel(nx, ny, nz)))
            {
                // First water voxel scanning down, so this is the column's top:
                // the level a body entering it meets, judged by the same step
                // rule as any other surface.
                return d <= maxLegalDropVoxels;
            }
            if (IsSurface(getVoxel, nx, ny, nz))
            {
                return d <= maxLegalDropVoxels;
            }
        }
        return false;
    }

    // Collects barrier triangles for one chunk, in CHUNK-LOCAL space. Returns
    // null when the chunk has no ledges, which is the common case for flat
    // terrain and lets the caller skip creating a body at all.
    //
    // The walls are the CONTOUR of the walkable region at each level, extracted
    // by marching squares over cell-centre samples — not one axis-aligned quad
    // per guarded cell face.
    //
    // That difference is the whole point. A per-face wall can only ever run
    // along a lattice axis, so a ledge cutting diagonally across the grid gets a
    // staircase of walls whose corners stick out past the ground into thin air;
    // a body walks to the real edge, falls through the notch beside the wall,
    // and is never touched by it. Marching squares puts a single 45-degree
    // segment across a diagonal block instead, so the wall follows the ledge.
    //
    // The field is NeighbourIsReachable rather than IsSurface: it already
    // answers "could a body at this level be here" including the legal step down
    // and water, so its boundary is exactly the set of real drops. A block is
    // skipped unless some cell in it is a genuine surface, which keeps contours
    // out of the open air above a cliff.
    //
    // The two coordinate spaces in play are easy to conflate: getVoxel is
    // world-indexed (it resolves across chunk boundaries) while emitted vertices
    // must be chunk-local (the node transform places them). So the origin is
    // added for lookups and NOT for output.
    // `maxLegalDropVoxels` is the deepest drop the class of body this set is for
    // takes willingly; the barrier stands at the top of everything deeper. One
    // set is built per LedgeBarrierClasses entry, each on its own layer.
    public static List<Vector3> Build(System.Func<int, int, int, int> getVoxel,
        DcCellSurface surface, int chunkWorldX, int chunkWorldY, int chunkWorldZ,
        int maxLegalDropVoxels)
    {
        List<Vector3> tris = null;
        var inside = new bool[4];
        var real = new bool[4];

        for (int y = 0; y < N; y++)
        {
            int wy = chunkWorldY + y;
            // Blocks anchored inside the chunk only. The block at N-1 covers the
            // seam with the next chunk, whose own blocks start at 0 — so every
            // seam is emitted exactly once, by the chunk on its low side.
            for (int z = 0; z < N; z++)
            {
                for (int x = 0; x < N; x++)
                {
                    int i = 0;
                    bool anyReal = false;
                    bool anyOut = false;
                    bool anyIn = false;
                    for (int c = 0; c < 4; c++)
                    {
                        // Sample order must match the bit order of MarchCases:
                        // (x,z), (x+1,z), (x+1,z+1), (x,z+1).
                        int ox = c == 1 || c == 2 ? 1 : 0;
                        int oz = c >= 2 ? 1 : 0;
                        int wx = chunkWorldX + x + ox;
                        int wz = chunkWorldZ + z + oz;
                        inside[c] = NeighbourIsReachable(getVoxel, wx, wy, wz, maxLegalDropVoxels);
                        real[c] = IsSurface(getVoxel, wx, wy, wz);
                        anyReal |= real[c];
                        anyIn |= inside[c];
                        anyOut |= !inside[c];
                        if (inside[c]) { i |= 1 << c; }
                    }
                    if (!anyReal || !anyIn || !anyOut)
                    {
                        continue;
                    }

                    // Ground height for this block: the surface the walkable
                    // cells actually sit on, so the wall stands on the drawn
                    // terrain rather than on the lattice.
                    float groundY = y;
                    int found = 0;
                    float sumY = 0f;
                    for (int c = 0; c < 4; c++)
                    {
                        int ox = c == 1 || c == 2 ? 1 : 0;
                        int oz = c >= 2 ? 1 : 0;
                        if (!real[c])
                        {
                            continue;
                        }
                        if (TryGroundPoint(surface, x + ox, y, z + oz, out Vector3 gp))
                        {
                            sumY += gp.Y;
                            found++;
                        }
                    }
                    if (found > 0)
                    {
                        groundY = sumY / found;
                    }

                    int[] edges = MarchCases[i];
                    for (int e = 0; e + 1 < edges.Length && edges[e] >= 0; e += 2)
                    {
                        Vector2 p0 = EdgePoint(edges[e], x, z, inside);
                        Vector2 p1 = EdgePoint(edges[e + 1], x, z, inside);
                        tris ??= new List<Vector3>();
                        AddSegment(tris, p0, p1, groundY);
                    }
                }
            }
        }
        return tris;
    }
}
