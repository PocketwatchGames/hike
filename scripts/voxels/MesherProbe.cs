using System;
using System.Collections.Generic;
using Godot;

// TEMPORARY diagnostic for voxel_center_sampling.
//   ramp   — normal.y along a 45° worldgen-style ramp. Alternation here is what
//            shows up as lighting banding (N·L follows the normal directly, so
//            it bands even when the material pick is stable).
//   tunnel — flat ground over a buried tunnel. Guards the fix that motivated
//            geometric normals: buried air must not tilt the surface.
//   shapes — topology the lattice change exists to deliver.
public static class MesherProbe
{
    private const int N = ChunkState.SIZE;
    private static int RUN = 2;

    public static void Run()
    {
        GD.Print($"[probe] === voxel_center_sampling = {CVars.voxelCenterSampling.Value} ===");
        RUN = 1; Ramp();
        RUN = 2; Ramp();
        RUN = 3; Ramp();
        RUN = 2; RampProfile();
        RUN = 3; RampProfile();
        RUN = 2; RampShadingTerms();
        RUN = 3; RampShadingTerms();
        RUN = 2; RampGeometry();
        CliffGeometry();
        RUN = 2; DiagonalWall();
        RUN = 3; DiagonalWall();
        Tunnel();
        TunnelSunBake();
        CliffSunBake();
        int r0 = ChunkMesherDC.VERT_RELAX_ITERATIONS;
        ChunkMesherDC.VERT_RELAX_ITERATIONS = 0; TunnelProfile();
        ChunkMesherDC.VERT_RELAX_ITERATIONS = 2; TunnelProfile();
        ChunkMesherDC.VERT_RELAX_ITERATIONS = r0;
        Shapes();
    }

    // Sweep the normal-smoothing knobs against every case at once. Banding is a
    // spread number, and the tunnel case is the regression guard the smoothing
    // must not break — a setting that flattens ramps by letting buried geometry
    // back in is a worse bug than the banding.
    public static void Sweep()
    {
        float selfW0 = ChunkMesherDC.NORMAL_SMOOTH_SELF_WEIGHT;
        float minDot0 = ChunkMesherDC.NORMAL_SMOOTH_MIN_DOT;
        int iters0 = ChunkMesherDC.NORMAL_SMOOTH_ITERATIONS;

        GD.Print("[sweep] relax/smooth | ramp 1-in-2 1-in-3 | diagWall 1-in-2 1-in-3 | tunnel (want 1.000)");
        int relax0 = ChunkMesherDC.VERT_RELAX_ITERATIONS;
        foreach (int relax in new[] { 0, 1, 2, 4, 8, 16 })
        {
            foreach (int iters in new[] { 1, 2 })
            {
                ChunkMesherDC.VERT_RELAX_ITERATIONS = relax;
                ChunkMesherDC.NORMAL_SMOOTH_ITERATIONS = iters;
                RUN = 2; float r2 = RampSpread();
                RUN = 3; float r3 = RampSpread();
                RUN = 2; float d2 = DiagonalWallSpread();
                RUN = 3; float d3 = DiagonalWallSpread();
                float tn = TunnelWorstNy();
                GD.Print($"[sweep] relax={relax,3} smooth={iters} | ramp {r2,6:F3} {r3,6:F3}"
                    + $" | diag {d2,6:F3} {d3,6:F3} | tunnel {tn,6:F3}");
            }
        }
        ChunkMesherDC.VERT_RELAX_ITERATIONS = relax0;

        ChunkMesherDC.NORMAL_SMOOTH_SELF_WEIGHT = selfW0;
        ChunkMesherDC.NORMAL_SMOOTH_MIN_DOT = minDot0;
        ChunkMesherDC.NORMAL_SMOOTH_ITERATIONS = iters0;
    }

    // rampSlope=1 in worldgen → 1 horizontal cell per vertical voxel → 45°,
    // true normal.y = 0.707.
    private static Vector3[] BuildRamp(out Vector3[] norms)
    {
        var v = new VoxelType[N, N, N];
        var top = new int[N];
        for (int x = 0; x < N; x++)
        {
            top[x] = Mathf.Clamp(1 + x / RUN, 0, N - 1);
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= top[x]; y++) { v[x, y, z] = VoxelType.Terrain; }
            }
        }
        VoxelType Get(int x, int y, int z) => Sample(v, x, y, z);

        // Reproduce WorldGen.StampGradeShapes, NOT "the whole ramp top is soft".
        // The difference is the whole point: the grade rule is per-axis and
        // local, so on a terraced ramp only the columns beside a riser qualify
        // and each tread's interior reads as flat and gets Y-snapped.
        int Top(int x) => top[Mathf.Clamp(x, 0, N - 1)];
        bool IsGrade(int x)
        {
            int c = Top(x);
            int lo = Top(x - 1), hi = Top(x + 1);
            bool axisX = Mathf.Abs(lo - c) <= 1 && Mathf.Abs(hi - c) <= 1 && (lo != c || hi != c);
            // The Z axis is uniform in this test body, so it never qualifies.
            return axisX;
        }
        VoxelTypeInfo.SharpAxes Shape(int x, int y, int z)
        {
            if (Get(x, y, z) == VoxelType.Air) { return VoxelTypeInfo.SharpAxes.None; }
            if (x < 0 || x >= N || y != top[x]) { return VoxelTypeInfo.SharpAxes.Y; }
            return IsGrade(x) ? VoxelTypeInfo.SharpAxes.None : VoxelTypeInfo.SharpAxes.Y;
        }
        return Build(Get, Shape, out norms);
    }

    // Spread of normal.y over the ramp face — the lighting banding, directly.
    private static float RampSpread()
    {
        var verts = BuildRamp(out Vector3[] norms);
        float lo = 1f, hi = 0f;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (p.Z < 5f || p.Z > 11f || p.X < 3f || p.X > 12f) { continue; }
            float ny = norms[i].Y;
            if (ny < 0.2f) { continue; }
            lo = Mathf.Min(lo, ny);
            hi = Mathf.Max(hi, ny);
        }
        return hi < lo ? 0f : hi - lo;
    }

    private static void Ramp()
    {
        var verts = BuildRamp(out Vector3[] norms);
        float lo = 1f, hi = 0f, sum = 0f;
        int n = 0;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (p.Z < 5f || p.Z > 11f || p.X < 3f || p.X > 12f) { continue; }
            float ny = norms[i].Y;
            if (ny < 0.2f) { continue; } // ignore near-vertical riser side faces
            lo = Mathf.Min(lo, ny);
            hi = Mathf.Max(hi, ny);
            sum += ny;
            n++;
        }
        GD.Print($"[probe] ramp 1-in-{RUN} (true normal.y={RUN / Mathf.Sqrt(RUN * RUN + 1f):F3}): min={lo:F3} max={hi:F3} spread={hi - lo:F3} mean={sum / Mathf.Max(n, 1):F3} verts={n}");
    }

    // A wall whose face runs 45° through the XZ grid (solid where x+z < k), so
    // the boundary is a plan-view staircase. The true face normal is constant
    // (-1,0,-1)/sqrt2 at every height — any variation along the run is the
    // vertical banding, and the smoothing gate is the prime suspect: the two
    // staircase face normals are -X and -Z, exactly 90 degrees apart, so a
    // min-dot of 0.5 rejects every neighbour that could smooth them.
    private static Vector3[] BuildDiagonalWall(out Vector3[] norms)
    {
        var v = new VoxelType[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                if (z >= N - x * RUN) { continue; }
                for (int y = 0; y < N; y++) { v[x, y, z] = VoxelType.Terrain; }
            }
        }
        VoxelType Get(int x, int y, int z) => Sample(v, x, y, z);
        return Build(Get, (x, y, z) => VoxelTypeInfo.GetDefaultShape(Get(x, y, z)), out norms);
    }

    // Spread of the horizontal normal direction along the diagonal face,
    // measured as the angle off the true 45 degree bearing.
    private static float DiagonalWallSpread()
    {
        var verts = BuildDiagonalWall(out Vector3[] norms);
        var truth = new Vector3(-1f, 0f, -1f / RUN).Normalized();
        float lo = 1f, hi = -1f;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            // Interior of the face only: away from chunk edges and the top/bottom caps.
            if (p.Y < 4f || p.Y > 12f) { continue; }
            if (p.X < 3f || p.Z < 3f || p.X > 13f || p.Z > 13f) { continue; }
            var h = new Vector3(norms[i].X, 0f, norms[i].Z);
            if (h.Length() < 0.3f) { continue; }
            float d = h.Normalized().Dot(truth);
            lo = Mathf.Min(lo, d);
            hi = Mathf.Max(hi, d);
        }
        return hi < lo ? 0f : hi - lo;
    }

    // Per-column normal.y profile along the ramp. A spread number says banding
    // exists; this says WHERE, so the pattern can be matched against the shape
    // channel instead of guessed at.
    private static void RampProfile()
    {
        var verts = BuildRamp(out Vector3[] norms);
        var sum = new float[N];
        var cnt = new int[N];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (p.Z < 5f || p.Z > 11f) { continue; }
            if (norms[i].Y < 0.2f) { continue; }
            int xi = Mathf.Clamp(Mathf.FloorToInt(p.X), 0, N - 1);
            sum[xi] += norms[i].Y;
            cnt[xi]++;
        }
        var sb = new System.Text.StringBuilder($"[probe] ramp 1-in-{RUN} normal.y by x: ");
        for (int x = 2; x <= 13; x++)
        {
            sb.Append(cnt[x] > 0 ? $"{sum[x] / cnt[x]:F3} " : "  --  ");
        }
        GD.Print(sb.ToString());
    }

    // GEOMETRY, not shading. Two questions the screenshots can't separate:
    //   ramp — do the vertices lie on a straight line? If they do, the slope is
    //          geometrically clean and the banding is purely in the normals.
    //   cliff — does a vertical face hold one X, or does it ripple in and out?
    // Deviation is reported against the best-fit line so a clean slope reads 0
    // regardless of its angle.
    private static void RampGeometry()
    {
        var verts = BuildRamp(out _);
        var minY = new float[N];
        var seen = new bool[N];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (p.Z < 5f || p.Z > 11f) { continue; }
            int xi = Mathf.Clamp(Mathf.FloorToInt(p.X + 0.001f), 0, N - 1);
            if (!seen[xi] || p.Y > minY[xi]) { minY[xi] = p.Y; }
            seen[xi] = true;
        }
        var sb = new System.Text.StringBuilder($"[probe] ramp 1-in-{RUN} top vertex Y by x: ");
        for (int x = 3; x <= 12; x++) { sb.Append(seen[x] ? $"{minY[x]:F2} " : " --  "); }
        GD.Print(sb.ToString());
    }

    // Vertical cliff face: a plateau top at y=10 for x<8, dropping to y=3.
    private static void CliffGeometry()
    {
        var v = new VoxelType[N, N, N];
        var top = new int[N];
        for (int x = 0; x < N; x++)
        {
            top[x] = x < 8 ? 10 : 3;
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= top[x]; y++) { v[x, y, z] = VoxelType.Terrain; }
            }
        }
        VoxelType Get(int x, int y, int z) => Sample(v, x, y, z);
        VoxelTypeInfo.SharpAxes Shape(int x, int y, int z)
        {
            if (Get(x, y, z) == VoxelType.Air) { return VoxelTypeInfo.SharpAxes.None; }
            // A plateau edge jumps more than maxGradeStep, so no column here
            // qualifies as a grade — every surface voxel snaps, as in worldgen.
            return VoxelTypeInfo.SharpAxes.Y;
        }
        var verts = Build(Get, Shape, out Vector3[] norms);

        var maxX = new float[N];
        var minX = new float[N];
        var seen = new bool[N];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (p.Z < 5f || p.Z > 11f) { continue; }
            if (p.Y < 4f || p.Y > 10f) { continue; }
            if (p.X < 6f || p.X > 10f) { continue; }
            int yi = Mathf.Clamp(Mathf.FloorToInt(p.Y + 0.001f), 0, N - 1);
            if (!seen[yi]) { maxX[yi] = p.X; minX[yi] = p.X; }
            else { maxX[yi] = Mathf.Max(maxX[yi], p.X); minX[yi] = Mathf.Min(minX[yi], p.X); }
            seen[yi] = true;
        }
        var sb = new System.Text.StringBuilder("[probe] cliff face vertex X by y: ");
        for (int y = 4; y <= 10; y++) { sb.Append(seen[y] ? $"{minX[y]:F2} " : " --  "); }
        GD.Print(sb.ToString());

        var sn = new System.Text.StringBuilder("[probe] cliff face normal.x by y: ");
        var sum = new float[N];
        var cnt = new int[N];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (p.Z < 5f || p.Z > 11f || p.Y < 4f || p.Y > 10f || p.X < 6f || p.X > 10f) { continue; }
            int yi = Mathf.Clamp(Mathf.FloorToInt(p.Y + 0.001f), 0, N - 1);
            sum[yi] += norms[i].X; cnt[yi]++;
        }
        for (int y = 4; y <= 10; y++) { sn.Append(cnt[y] > 0 ? $"{sum[y] / cnt[y]:F2} " : " --  "); }
        GD.Print(sn.ToString());
    }

    // The two per-vertex terms that modulate lighting but are invisible in the
    // debug_normals view: baked AO (COLOR.a -> ao_factor) and concavity
    // (CUSTOM2.w). Either one banding per voxel would look exactly like the
    // reported stair-stepping while the normals stay perfectly smooth.
    private static void RampShadingTerms()
    {
        var st = new SurfaceTool();
        st.Begin(Godot.Mesh.PrimitiveType.Triangles);
        st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(1, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(2, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(3, SurfaceTool.CustomFormat.RgbaFloat);

        var v = new VoxelType[N, N, N];
        var top = new int[N];
        for (int x = 0; x < N; x++)
        {
            top[x] = Mathf.Clamp(1 + x / RUN, 0, N - 1);
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= top[x]; y++) { v[x, y, z] = VoxelType.Terrain; }
            }
        }
        VoxelType Get(int x, int y, int z) => Sample(v, x, y, z);
        ChunkMesherDC.Build(new ChunkState(Vector3I.Zero), Get,
            (x, y, z) => VoxelTypeInfo.GetDefaultShape(Get(x, y, z)),
            (x, y, z) => 0, (x, y, z) => 0, (x, y, z) => LightEngine.MAX_LIGHT, (x, y, z) => true,
            st, 0, 0, 0, out bool hasAnyFace);
        if (!hasAnyFace) { return; }

        var arrays = st.Commit().SurfaceGetArrays(0);
        var verts = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
        var cols = arrays[(int)Godot.Mesh.ArrayType.Color].AsColorArray();
        var c3 = arrays[(int)Godot.Mesh.ArrayType.Custom3].AsFloat32Array();

        var aoSum = new float[N];
        var sunSum = new float[N];
        var cnt = new int[N];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (p.Z < 5f || p.Z > 11f) { continue; }
            int xi = Mathf.Clamp(Mathf.FloorToInt(p.X), 0, N - 1);
            aoSum[xi] += cols[i].A;
            sunSum[xi] += c3[i * 4];
            cnt[xi]++;
        }
        var sb = new System.Text.StringBuilder($"[probe] ramp 1-in-{RUN} baked AO by x: ");
        for (int x = 2; x <= 13; x++) { sb.Append(cnt[x] > 0 ? $"{aoSum[x] / cnt[x]:F3} " : "  --  "); }
        GD.Print(sb.ToString());
        // Per-VERTEX spread, not the per-column mean: diamond facets are single
        // vertices deviating from their neighbours, which a column average hides.
        float slo = 1f, shi = 0f;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (p.Z < 5f || p.Z > 11f || p.X < 3f || p.X > 12f) { continue; }
            slo = Mathf.Min(slo, c3[i * 4]);
            shi = Mathf.Max(shi, c3[i * 4]);
        }
        GD.Print($"[probe] ramp 1-in-{RUN} baked sun per-vertex spread = {shi - slo:F3}");
        var sb2 = new System.Text.StringBuilder($"[probe] ramp 1-in-{RUN} baked sun by x: ");
        for (int x = 2; x <= 13; x++) { sb2.Append(cnt[x] > 0 ? $"{sunSum[x] / cnt[x]:F3} " : "  --  "); }
        GD.Print(sb2.ToString());
    }

    // The case a shared light-volume texel cannot express: a ONE-voxel roof with
    // sunlit sky above and a dark cave below. Dilating sun into solid voxels
    // gives that roof a single value, so either the ground above reads dark or
    // the ceiling below reads lit. The per-vertex bake reads the air each
    // surface faces, so both sides should resolve independently:
    //   ground above the roof -> 1.0,  cave ceiling under it -> 0.0.
    private static void TunnelSunBake()
    {
        var v = new VoxelType[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= 7; y++) { v[x, y, z] = VoxelType.Terrain; }
            }
        }
        for (int x = 0; x < N; x++)
        {
            for (int z = 4; z <= 7; z++)
            {
                for (int y = 5; y <= 6; y++) { v[x, y, z] = VoxelType.Air; }
            }
        }
        VoxelType Get(int x, int y, int z) => Sample(v, x, y, z);

        // Mimics LightEngine.ComputeSunlight's column scan: an air voxel is lit
        // only when nothing solid stands between it and the sky.
        int Sun(int x, int y, int z)
        {
            if (VoxelTypeInfo.IsSolid(Get(x, y, z))) { return 0; }
            for (int up = y + 1; up < N; up++)
            {
                if (VoxelTypeInfo.IsSolid(Get(x, up, z))) { return 0; }
            }
            return LightEngine.MAX_LIGHT;
        }

        var st = new SurfaceTool();
        st.Begin(Godot.Mesh.PrimitiveType.Triangles);
        st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(1, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(2, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(3, SurfaceTool.CustomFormat.RgbaFloat);
        ChunkMesherDC.Build(new ChunkState(Vector3I.Zero), Get,
            (x, y, z) => VoxelTypeInfo.GetDefaultShape(Get(x, y, z)),
            (x, y, z) => 0, (x, y, z) => 0, Sun, (x, y, z) => true,
            st, 0, 0, 0, out bool hasAnyFace);
        if (!hasAnyFace) { GD.Print("[probe] tunnel sun: no faces"); return; }

        var arrays = st.Commit().SurfaceGetArrays(0);
        var verts = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
        var c3 = arrays[(int)Godot.Mesh.ArrayType.Custom3].AsFloat32Array();

        float groundLo = 1f, ceilHi = 0f;
        int groundN = 0, ceilN = 0;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (p.X < 4f || p.X > 12f || p.Z < 4.5f || p.Z > 7.5f) { continue; }
            float sun = c3[i * 4];
            if (Mathf.Abs(p.Y - 8f) < 0.01f) { groundLo = Mathf.Min(groundLo, sun); groundN++; }
            if (Mathf.Abs(p.Y - 7f) < 0.01f) { ceilHi = Mathf.Max(ceilHi, sun); ceilN++; }
        }
        GD.Print($"[probe] tunnel sun bake: ground-above-roof min={groundLo:F3} (want 1.000, n={groundN})"
            + $"  cave-ceiling max={ceilHi:F3} (want 0.000, n={ceilN})");
    }

    // Baked sun down a vertical cliff face, with a realistic column-scan sun.
    // The air beside an open cliff is fully sky-exposed at every height, so a
    // bake that just reads "the adjacent air voxel" returns 1.0 all the way
    // down — the wall's own orientation is never accounted for.
    private static void CliffSunBake()
    {
        var v = new VoxelType[N, N, N];
        for (int x = 0; x < N; x++)
        {
            int top = x < 8 ? 10 : 3;
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= top; y++) { v[x, y, z] = VoxelType.Terrain; }
            }
        }
        VoxelType Get(int x, int y, int z) => Sample(v, x, y, z);
        int Sun(int x, int y, int z)
        {
            if (VoxelTypeInfo.IsSolid(Get(x, y, z))) { return 0; }
            for (int up = y + 1; up < N; up++)
            {
                if (VoxelTypeInfo.IsSolid(Get(x, up, z))) { return 0; }
            }
            return LightEngine.MAX_LIGHT;
        }

        var st = new SurfaceTool();
        st.Begin(Godot.Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < 4; i++) { st.SetCustomFormat(i, SurfaceTool.CustomFormat.RgbaFloat); }
        ChunkMesherDC.Build(new ChunkState(Vector3I.Zero), Get,
            (x, y, z) => VoxelTypeInfo.SharpAxes.Y,
            (x, y, z) => 0, (x, y, z) => 0, Sun, (x, y, z) => true,
            st, 0, 0, 0, out bool hasAnyFace);
        if (!hasAnyFace) { GD.Print("[probe] cliff sun: no faces"); return; }

        var arrays = st.Commit().SurfaceGetArrays(0);
        var verts = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
        var c3 = arrays[(int)Godot.Mesh.ArrayType.Custom3].AsFloat32Array();

        var sum = new float[N];
        var cnt = new int[N];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (p.Z < 5f || p.Z > 11f || p.X < 7.5f || p.X > 8.5f) { continue; }
            int yi = Mathf.Clamp(Mathf.FloorToInt(p.Y + 0.001f), 0, N - 1);
            sum[yi] += c3[i * 4];
            cnt[yi]++;
        }
        var sb = new System.Text.StringBuilder("[probe] cliff baked sun by y (top y=10 -> base y=4): ");
        for (int y = 10; y >= 4; y--) { sb.Append(cnt[y] > 0 ? $"{sum[y] / cnt[y]:F2} " : " --  "); }
        GD.Print(sb.ToString());
    }

    private static void DiagonalWall()
    {
        GD.Print($"[probe] diagonal wall 1-in-{RUN}: horizontal-normal spread = {DiagonalWallSpread():F3} (want 0.000)");
    }

    // Flat ground at y=8 with a 1-voxel-roofed tunnel under z=4..7.
    // normal.y across the flat ground, by z. The tunnel is buried under z=4..7,
    // so any dip localized there is buried geometry reaching the surface.
    private static void TunnelProfile()
    {
        var v = new VoxelType[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= 7; y++) { v[x, y, z] = VoxelType.Terrain; }
            }
        }
        for (int x = 0; x < N; x++)
        {
            for (int z = 4; z <= 7; z++)
            {
                for (int y = 5; y <= 6; y++) { v[x, y, z] = VoxelType.Air; }
            }
        }
        VoxelType Get(int x, int y, int z) => Sample(v, x, y, z);
        var verts = Build(Get, (x, y, z) => VoxelTypeInfo.GetDefaultShape(Get(x, y, z)), out Vector3[] norms);
        var lo = new float[N];
        for (int i = 0; i < N; i++) { lo[i] = 1f; }
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (Mathf.Abs(p.Y - 8f) > 0.01f || p.X < 4f || p.X > 12f) { continue; }
            int zi = Mathf.Clamp(Mathf.FloorToInt(p.Z), 0, N - 1);
            lo[zi] = Mathf.Min(lo[zi], norms[i].Y);
        }
        var sb = new System.Text.StringBuilder($"[probe] tunnel relax={ChunkMesherDC.VERT_RELAX_ITERATIONS} ground normal.y by z: ");
        for (int z = 1; z <= 11; z++) { sb.Append(lo[z] <= 1f ? $"{lo[z]:F3} " : "  --  "); }
        GD.Print(sb.ToString());
    }

    private static void Tunnel()
    {
        GD.Print($"[probe] tunnel: worst surface normal.y over buried tunnel = {TunnelWorstNy():F3} (want 1.000)");
    }

    private static float TunnelWorstNy()
    {
        var v = new VoxelType[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= 7; y++) { v[x, y, z] = VoxelType.Terrain; }
            }
        }
        for (int x = 0; x < N; x++)
        {
            for (int z = 4; z <= 7; z++)
            {
                for (int y = 5; y <= 6; y++) { v[x, y, z] = VoxelType.Air; }
            }
        }
        VoxelType Get(int x, int y, int z) => Sample(v, x, y, z);
        var verts = Build(Get, (x, y, z) => VoxelTypeInfo.GetDefaultShape(Get(x, y, z)), out Vector3[] norms);

        float worst = 1f;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (Mathf.Abs(p.Y - 8f) > 0.01f || p.X < 4f || p.X > 12f || p.Z < 1f || p.Z > 15f) { continue; }
            worst = Mathf.Min(worst, norms[i].Y);
        }
        return worst;
    }

    private static void Shapes()
    {
        var door = new VoxelType[N, N, N];
        for (int x = 4; x <= 12; x++)
        {
            for (int y = 4; y <= 8; y++) { door[x, y, 8] = VoxelType.Stone; }
        }
        for (int y = 4; y <= 6; y++) { door[8, y, 8] = VoxelType.Air; }
        VoxelType Get(int x, int y, int z) => Sample(door, x, y, z);
        var verts = Build(Get, (x, y, z) => VoxelTypeInfo.GetDefaultShape(Get(x, y, z)), out _);
        var hits = new SortedSet<float>();
        foreach (Vector3 p in verts)
        {
            if (p.X < 7f || p.X > 10f || p.Y < 5f || p.Y > 6f || p.Z < 7f || p.Z > 10f) { continue; }
            hits.Add(Mathf.Round(p.X * 100f) / 100f);
        }
        GD.Print($"[probe] doorway jambs X=[{string.Join(", ", hits)}] (want 8 and 9 present)");
    }

    private static VoxelType Sample(VoxelType[,,] v, int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= N || y >= N || z >= N) { return VoxelType.Air; }
        return v[x, y, z];
    }

    private static Vector3[] Build(Func<int, int, int, VoxelType> get,
        Func<int, int, int, VoxelTypeInfo.SharpAxes> shape, out Vector3[] norms)
    {
        var st = new SurfaceTool();
        st.Begin(Godot.Mesh.PrimitiveType.Triangles);
        st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(1, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(2, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(3, SurfaceTool.CustomFormat.RgbaFloat);
        ChunkMesherDC.Build(new ChunkState(Vector3I.Zero), get, shape,
            (x, y, z) => 0, (x, y, z) => 0, (x, y, z) => LightEngine.MAX_LIGHT, (x, y, z) => true,
            st, 0, 0, 0, out bool hasAnyFace);
        if (!hasAnyFace)
        {
            norms = Array.Empty<Vector3>();
            return Array.Empty<Vector3>();
        }
        var arrays = st.Commit().SurfaceGetArrays(0);
        norms = arrays[(int)Godot.Mesh.ArrayType.Normal].AsVector3Array();
        return arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
    }
}
