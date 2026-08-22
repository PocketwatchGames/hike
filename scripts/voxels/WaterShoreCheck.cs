using System;
using System.Text;
using Godot;

// Headless shoreline-coverage check: `--headless -- "water_shore_check 1"`.
//
// The water surface is a flat plane of unit quads on the voxel grid; the shore
// under it is a DC surface whose vertex sits anywhere inside its own cell. Where
// the terrain dips below the waterline but no water quad reaches over it, the
// player sees a dry notch and the shoreline reads as a staircase. That gap is
// what this measures, and it measures the two halves independently so neither
// can hide the other:
//
//   SUBMERGED — read off the committed TERRAIN mesh: any vertex below the
//     waterline. Lattice-agnostic on purpose (the answer must not depend on
//     voxel_center_sampling) and it is the drawn geometry, not the voxel grid.
//   COVERED   — read off the committed WATER mesh: an upward-facing quad at the
//     waterline over that column's footprint.
//
// A column that is submerged and not covered is the artifact. The ASCII map is
// the point of the tool: the failure is a SHAPE — notches at inside corners, a
// ragged edge along a diagonal — and a count alone can't say which.
public static class WaterShoreCheck
{
    private const int N = ChunkState.SIZE;
    // Waterline plane sits at WATER_TOP_VOXEL + 1.
    private const int WATER_TOP_VOXEL = 8;
    private const float WATERLINE = WATER_TOP_VOXEL + 1;
    // Chunk-edge columns read Air outside the array, so their DC surface drops
    // into a phantom cliff and reports as submerged. Not the shoreline under
    // test — skip a margin wide enough to clear it.
    private const int MARGIN = 2;
    // A dip shallower than this is thinner than the water's own rim band and
    // cannot be seen; counting it would drown the signal.
    private const float MIN_VISIBLE_DIP = 0.02f;

    public static void RunAndQuit(SceneTree tree)
    {
        Blocks.Bind();
        GD.Print($"[water_shore_check] voxel_center_sampling = {CVars.voxelCenterSampling.Value}");
        // Both shore shapes are run against both Shape channels, because they
        // fail differently and only one of them was ever the reported bug.
        // BENCHED is worldgen's Y-snapped plateau: the terrace lands exactly on
        // the water plane, so nothing sags and nothing needs covering. GRADED is
        // a slope, where the DC vertex is free to sag under the waterline — the
        // case the shell exists for.
        foreach (bool graded in new[] { false, true })
        {
            Case("round lake", RoundLake, graded);
            Case("diagonal shore", DiagonalShore, graded);
            Case("channel", Channel, graded);
            Case("cliff", Cliff, graded);
        }
        SealedPocket();
        GD.Print("[water_shore_check] done");
        tree.Quit();
    }

    // Solid to the seabed inside the radius, up to the waterline outside it.
    // A circle is the shape worth testing: one lake presents every shoreline
    // bearing plus both senses of corner, which axis-aligned shapes never do.
    private static void RoundLake(int[,,] v)
    {
        const float R = 4.5f;
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                float dx = x - N * 0.5f + 0.5f;
                float dz = z - N * 0.5f + 0.5f;
                Fill(v, x, z, dx * dx + dz * dz < R * R);
            }
        }
    }

    // A 45-degree shoreline through the XZ grid — a plan-view staircase, so the
    // stepping is maximal and every step corner is an inside corner.
    private static void DiagonalShore(int[,,] v)
    {
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                Fill(v, x, z, x + z < N);
            }
        }
    }

    // A two-voxel-wide diagonal channel: both banks are shore, so a fix that
    // over-dilates drowns the banks and shows up as a covered column with no
    // submerged terrain under it.
    private static void Channel(int[,,] v)
    {
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                int d = x - z;
                Fill(v, x, z, d >= 0 && d < 2);
            }
        }
    }

    // A diagonal STONE cliff plunging into water — the shape in the bug report,
    // and a different path through the mesher than any beach: the shell cell at
    // the waterline has solid rock directly above it. Stone is the point of the
    // block choice, not decoration: it is the one ground block carrying
    // edgeRoughness, which carves its face back off the voxel boundary and opens
    // the crack the water is supposed to be hiding.
    private static void Cliff(int[,,] v)
    {
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                if (x + z < N)
                {
                    Fill(v, x, z, true);
                    continue;
                }
                for (int y = 0; y < N - 1; y++)
                {
                    v[x, y, z] = Blocks.StoneId;
                }
            }
        }
    }

    // One column: a basin holding water to the waterline, or shore standing at
    // it. Shore tops out at WATER_TOP_VOXEL exactly — that is the row whose DC
    // cap can sag under the plane, and so the only row the shell must reach.
    private static void Fill(int[,,] v, int x, int z, bool water)
    {
        int solidTop = water ? WATER_TOP_VOXEL - 4 : WATER_TOP_VOXEL;
        for (int y = 0; y <= solidTop; y++)
        {
            v[x, y, z] = Blocks.GroundId;
        }
        if (!water)
        {
            return;
        }
        for (int y = solidTop + 1; y <= WATER_TOP_VOXEL; y++)
        {
            v[x, y, z] = Blocks.WaterId;
        }
    }

    private static void Case(string name, Action<int[,,]> shape, bool graded)
    {
        var v = new int[N, N, N];
        shape(v);
        // Worldgen is authoritative for the Shape channel, so the probe drives it
        // directly rather than inferring one: a graded slope stamps SharpAxes.None
        // over its surface, a bench keeps the block's Y-snapping default.
        SharpAxes Shape(int x, int y, int z) => graded ? SharpAxes.None : Blocks.DefaultShape(Get(x, y, z));
        int Get(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= N || y >= N || z >= N) { return Blocks.AirId; }
            return v[x, y, z];
        }

        // Deepest submerged terrain vertex per column, and whether the water
        // mesh puts a waterline quad over that column.
        var dip = new float[N, N];
        var covered = new bool[N, N];

        var stTerrain = new SurfaceTool();
        stTerrain.Begin(Mesh.PrimitiveType.Triangles);
        for (int c = 0; c < 4; c++)
        {
            stTerrain.SetCustomFormat(c, SurfaceTool.CustomFormat.RgbaFloat);
        }
        ChunkMesherDC.Build(new ChunkState(Vector3I.Zero), Get,
            Shape,
            (x, y, z) => 0, (x, y, z) => 0, (x, y, z) => LightEngine.MAX_LIGHT,
            (x, y, z) => false, (x, y, z) => true,
            stTerrain, 0, 0, 0, out bool hasTerrain, out DcCellSurface surface);
        if (hasTerrain)
        {
            var verts = stTerrain.Commit().SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            foreach (Vector3 p in verts)
            {
                float depth = WATERLINE - p.Y;
                if (depth <= MIN_VISIBLE_DIP)
                {
                    continue;
                }
                int cx = Mathf.FloorToInt(p.X);
                int cz = Mathf.FloorToInt(p.Z);
                if (cx < 0 || cz < 0 || cx >= N || cz >= N)
                {
                    continue;
                }
                dip[cx, cz] = Mathf.Max(dip[cx, cz], depth);
            }
        }

        var stWater = new SurfaceTool();
        stWater.Begin(Mesh.PrimitiveType.Triangles);
        stWater.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        WaterMesher.Build(new ChunkState(Vector3I.Zero), Get, stWater, 0, 0, 0, out bool hasWater);
        if (hasWater)
        {
            var arrays = stWater.Commit().SurfaceGetArrays(0);
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var norms = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                if (norms[i].Y < 0.5f)
                {
                    continue;
                }
                if (Mathf.Abs(verts[i].Y - WATERLINE) > 0.1f)
                {
                    continue;
                }
                float minX = Mathf.Min(verts[i].X, Mathf.Min(verts[i + 1].X, verts[i + 2].X));
                float minZ = Mathf.Min(verts[i].Z, Mathf.Min(verts[i + 1].Z, verts[i + 2].Z));
                int cx = Mathf.FloorToInt(minX + 0.01f);
                int cz = Mathf.FloorToInt(minZ + 0.01f);
                if (cx < 0 || cz < 0 || cx >= N || cz >= N)
                {
                    continue;
                }
                covered[cx, cz] = true;
            }
        }

        int gaps = 0;
        int cracks = 0;
        float worst = 0f;
        // How far the drawn rock face sits off the voxel lattice at the
        // waterline. A clean vertical face lands exactly on a boundary plane;
        // edgeRoughness carves it back, and that recession IS the width of the
        // slot between the water's edge and the rock.
        float recession = 0f;
        var map = new StringBuilder();
        for (int z = MARGIN; z < N - MARGIN; z++)
        {
            map.Append("      ");
            for (int x = MARGIN; x < N - MARGIN; x++)
            {
                bool submerged = dip[x, z] > MIN_VISIBLE_DIP;
                bool isWater = Get(x, WATER_TOP_VOXEL, z) == Blocks.WaterId;
                char c;
                if (surface != null && surface.TryGetLocal(x, WATER_TOP_VOXEL, z, out Vector3 fp))
                {
                    recession = Mathf.Max(recession, Mathf.Max(
                        Mathf.Abs(fp.X - Mathf.Round(fp.X)), Mathf.Abs(fp.Z - Mathf.Round(fp.Z))));
                }
                if (isWater)
                {
                    c = '~';
                }
                else if (Blocks.IsSolid(Get(x, WATER_TOP_VOXEL, z)) && TouchesWater(Get, x, z) && !covered[x, z])
                {
                    // The shell reaches this column but the mesher dropped its
                    // quad, so nothing covers the slot between the water's edge
                    // and the rock face — you see straight down it.
                    c = 'C';
                    cracks++;
                }
                else if (submerged && !covered[x, z])
                {
                    c = 'X';
                    gaps++;
                    worst = Mathf.Max(worst, dip[x, z]);
                }
                else if (covered[x, z] && submerged)
                {
                    c = '#';
                }
                else if (covered[x, z])
                {
                    // The shell put a quad here and it draws NOTHING: a Y-snapped
                    // terrace lands exactly on the water plane, and TOP_EPSILON
                    // holds the water a hair under it. Worth its own glyph — a
                    // benched shore reads as a fully covered ring in this map
                    // while the waterline the player sees is still the raw voxel
                    // outline of the water columns.
                    c = 'o';
                }
                else
                {
                    c = '.';
                }
                map.Append(c);
            }
            map.Append('\n');
        }

        string kind = graded ? "graded" : "benched";
        GD.Print($"[water_shore_check] {name} ({kind}): {gaps} uncovered submerged column(s) worst dip {worst:F2}m"
            + $", {cracks} open crack column(s), face recession {recession:F2}m");
        GD.Print("      ~ water   # wet shore   o occluded quad   X UNCOVERED DIP   C OPEN CRACK   . dry");
        GD.Print(map.ToString().TrimEnd('\n'));
        DumpShoreHeights($"{name} {kind}", surface);
    }

    // Water sealed under rock with no air gap must stay UNSKINNED. That is what
    // the roofed test is for — such a pocket's faces leak through the ceiling
    // cutaway, which strips the solid above it and leaves the seabed as the only
    // depth in the buffer — and the shell exception above relaxes that test, so
    // the pocket is the case that has to keep passing. Its shell borders only
    // roofed water, so it must not find an open neighbour to borrow from.
    private static void SealedPocket()
    {
        var v = new int[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int y = 0; y < N; y++)
            {
                for (int z = 0; z < N; z++)
                {
                    v[x, y, z] = Blocks.StoneId;
                }
            }
        }
        for (int x = 6; x < 10; x++)
        {
            for (int y = 4; y < 6; y++)
            {
                for (int z = 6; z < 10; z++)
                {
                    v[x, y, z] = Blocks.WaterId;
                }
            }
        }
        int Get(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= N || y >= N || z >= N) { return Blocks.StoneId; }
            return v[x, y, z];
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        WaterMesher.Build(new ChunkState(Vector3I.Zero), Get, st, 0, 0, 0, out bool hasWater);
        int tris = hasWater
            ? st.Commit().SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length / 3
            : 0;
        GD.Print($"[water_shore_check] sealed pocket: {tris} water triangle(s) — must be 0");

        // The shell exception runs its 8-neighbour scan on every solid-under-
        // solid voxel, which is most of the underground, so the all-rock chunk
        // is the worst case for it. Timed rather than argued: this is the number
        // to watch if chunk builds ever regress.
        const int REPS = 200;
        ulong t0 = Time.GetTicksUsec();
        for (int i = 0; i < REPS; i++)
        {
            var bench = new SurfaceTool();
            bench.Begin(Mesh.PrimitiveType.Triangles);
            bench.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
            WaterMesher.Build(new ChunkState(Vector3I.Zero), Get, bench, 0, 0, 0, out bool _);
        }
        double perBuild = (Time.GetTicksUsec() - t0) / (double)REPS / 1000.0;
        // The terrain mesher on the same chunk, for scale — the water mesh is
        // only worth optimizing against what the chunk build already costs.
        ulong t1 = Time.GetTicksUsec();
        for (int i = 0; i < REPS; i++)
        {
            var bench = new SurfaceTool();
            bench.Begin(Mesh.PrimitiveType.Triangles);
            for (int c = 0; c < 4; c++) { bench.SetCustomFormat(c, SurfaceTool.CustomFormat.RgbaFloat); }
            ChunkMesherDC.Build(new ChunkState(Vector3I.Zero), Get,
                (x, y, z) => Blocks.DefaultShape(Get(x, y, z)),
                (x, y, z) => 0, (x, y, z) => 0, (x, y, z) => LightEngine.MAX_LIGHT,
                (x, y, z) => false, (x, y, z) => true,
                bench, 0, 0, 0, out bool _, out DcCellSurface _);
        }
        double dcBuild = (Time.GetTicksUsec() - t1) / (double)REPS / 1000.0;
        GD.Print($"[water_shore_check] worst-case all-rock chunk: water {perBuild:F3} ms, terrain {dcBuild:F3} ms");
    }

    // Is any of the eight columns around this one water at the waterline row —
    // i.e. does the water volume's shell reach it?
    private static bool TouchesWater(Func<int, int, int, int> get, int x, int z)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if ((dx != 0 || dz != 0) && get(x + dx, WATER_TOP_VOXEL, z + dz) == Blocks.WaterId)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // Height of the drawn terrain at each DC cell along the waterline row, in cm
    // relative to the water plane. Coverage can be perfect and the shoreline
    // still read as a staircase: the waterline the player sees is where this
    // field crosses zero, so if the field itself only takes a few quantized
    // values the crossing snaps to the cell lattice.
    private static void DumpShoreHeights(string name, DcCellSurface surface)
    {
        if (surface == null)
        {
            return;
        }
        var map = new StringBuilder();
        for (int z = MARGIN; z < N - MARGIN; z++)
        {
            map.Append("      ");
            for (int x = MARGIN; x < N - MARGIN; x++)
            {
                if (!surface.TryGetLocal(x, WATER_TOP_VOXEL, z, out Vector3 p))
                {
                    map.Append("   .");
                    continue;
                }
                map.Append($"{Mathf.RoundToInt((p.Y - WATERLINE) * 100f),4}");
            }
            map.Append('\n');
        }
        GD.Print($"      terrain height at the waterline row, cm vs the water plane ({name}):");
        GD.Print(map.ToString().TrimEnd('\n'));
    }
}
