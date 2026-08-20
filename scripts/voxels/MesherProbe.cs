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

    // The probe reads the flat block tables from its very first synthetic
    // voxel, so it must bind them itself: run from a launch argument there is
    // no game start to have done it, and it died in Blocks.IsSolid instead.
    private static void EnsureBound()
    {
        Blocks.Bind();
    }

    public static void Run()
    {
        EnsureBound();
        GD.Print($"[probe] === voxel_center_sampling = {CVars.voxelCenterSampling.Value} ===");
        RUN = 1; Ramp();
        RUN = 2; Ramp();
        RUN = 3; Ramp();
        RUN = 2; RampProfile();
        RUN = 3; RampProfile();
        RUN = 2; RampShadingTerms();
        RUN = 3; RampShadingTerms();
        RUN = 2; RampGeometry();
        foreach (int drop in new[] { 2, 3, 4, 6, 8 }) { CliffGeometry(drop); }
        // The same sweep with the shading smoothers off. Their whole job is to
        // stop a terraced RAMP from banding, and they do it by averaging a
        // riser's vertex normals into the treads it sits between — which also
        // decides whether a short wall can ever reach the wall tile. The A/B is
        // what separates "the geometry isn't vertical" (it is, at every drop)
        // from "the shading normal was averaged away" (it was).
        int cliffRelax0 = ChunkMesherDC.VERT_RELAX_ITERATIONS;
        int cliffSmooth0 = ChunkMesherDC.NORMAL_SMOOTH_ITERATIONS;
        ChunkMesherDC.VERT_RELAX_ITERATIONS = 0;
        ChunkMesherDC.NORMAL_SMOOTH_ITERATIONS = 0;
        GD.Print("[probe] --- relax=0 smooth=0 ---");
        foreach (int drop in new[] { 2, 3, 4 }) { CliffGeometry(drop); }
        ChunkMesherDC.VERT_RELAX_ITERATIONS = cliffRelax0;
        ChunkMesherDC.NORMAL_SMOOTH_ITERATIONS = cliffSmooth0;
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
        EnsureBound();
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
        var v = new int[N, N, N];
        var top = new int[N];
        for (int x = 0; x < N; x++)
        {
            top[x] = Mathf.Clamp(1 + x / RUN, 0, N - 1);
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= top[x]; y++) { v[x, y, z] = Blocks.GroundId; }
            }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);

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
        SharpAxes Shape(int x, int y, int z)
        {
            if (Get(x, y, z) == Blocks.AirId) { return SharpAxes.None; }
            if (x < 0 || x >= N || y != top[x]) { return SharpAxes.Y; }
            return IsGrade(x) ? SharpAxes.None : SharpAxes.Y;
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
        var v = new int[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                if (z >= N - x * RUN) { continue; }
                for (int y = 0; y < N; y++) { v[x, y, z] = Blocks.GroundId; }
            }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);
        return Build(Get, (x, y, z) => Blocks.DefaultShape(Get(x, y, z)), out norms);
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
    // Cliff face geometry AND shading normal, per wall height. The material pick
    // is smoothstep(wallBand.x, wallBand.y, normal.y) in voxel_clip.gdshader and
    // the authored bands start at 0.3–0.4, so a face whose normal.y never gets
    // below that renders as ground however vertical the geometry under it is.
    // Sweeping the drop is what shows where that threshold is actually crossed —
    // the terracing approaches pick their quantize step against this.
    private const int CLIFF_BASE = 3;

    private static void CliffGeometry(int drop)
    {
        CliffFace(drop, print: true);
        CliffFace(drop, print: true, diagonal: true);
    }

    // Flattest normal.y anywhere on the face. This is the number the wall/flat
    // pick reads: below the terrain's wallBand.x the face renders as wall.
    //
    // `diagonal` runs the plateau edge at 45 degrees THROUGH THE XZ GRID instead
    // of along it. Worth testing separately because the two cases reach the
    // smoothing gate differently: an axis-aligned riser's face cells all share
    // one normal, while a diagonal edge is a plan-view staircase of -X and -Z
    // cells that are 90 degrees apart and therefore reject each other.
    // `sharpRiser` stamps SharpAxes.All on the riser voxels, which is what makes
    // DC cut a true cubic face there instead of a Y-snapped one.
    private static float CliffFace(int drop, bool print, bool diagonal = false, bool sharpRiser = false)
    {
        int high = CLIFF_BASE + drop;
        var v = new int[N, N, N];
        var top = new int[N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                top[x, z] = (diagonal ? x + z < N : x < 8) ? high : CLIFF_BASE;
                for (int y = 0; y <= top[x, z]; y++) { v[x, y, z] = Blocks.GroundId; }
            }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);
        int Top(int x, int z) => top[Mathf.Clamp(x, 0, N - 1), Mathf.Clamp(z, 0, N - 1)];
        SharpAxes Shape(int x, int y, int z)
        {
            if (Get(x, y, z) == Blocks.AirId) { return SharpAxes.None; }
            // A riser voxel: buried under the high tread, exposed sideways to
            // the low one. Only these get the cubic treatment — the treads stay
            // Y-snapped so flat ground is unaffected.
            if (sharpRiser && x >= 0 && x < N && z >= 0 && z < N && y <= Top(x, z) && y > CLIFF_BASE
                && (Top(x - 1, z) < y || Top(x + 1, z) < y || Top(x, z - 1) < y || Top(x, z + 1) < y))
            {
                return SharpAxes.All;
            }
            // A plateau edge jumps more than maxGradeStep, so no column here
            // qualifies as a grade — every surface voxel snaps, as in worldgen.
            return SharpAxes.Y;
        }
        var verts = Build(Get, Shape, out Vector3[] norms);

        int loY = CLIFF_BASE + 1;
        var minX = new float[N];
        var seen = new bool[N];
        var sumNy = new float[N];
        var cnt = new int[N];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (p.Y < loY || p.Y > high) { continue; }
            // Sample away from the chunk border either way; the diagonal edge
            // runs the whole map, so it is picked out by distance to the line.
            if (diagonal)
            {
                if (p.X < 3f || p.X > 13f || p.Z < 3f || p.Z > 13f) { continue; }
                if (Mathf.Abs(p.X + p.Z - N) > 1.5f) { continue; }
            }
            else
            {
                if (p.Z < 5f || p.Z > 11f) { continue; }
                if (p.X < 6f || p.X > 10f) { continue; }
            }
            int yi = Mathf.Clamp(Mathf.FloorToInt(p.Y + 0.001f), 0, N - 1);
            minX[yi] = seen[yi] ? Mathf.Min(minX[yi], p.X) : p.X;
            seen[yi] = true;
            sumNy[yi] += norms[i].Y;
            cnt[yi]++;
        }

        string tag = (diagonal ? "diag" : "axis") + (sharpRiser ? "+sharp" : "");
        var sn = new System.Text.StringBuilder($"[probe] cliff {tag,10} drop={drop,2} face normal.y by y: ");
        float best = 1f;
        for (int y = loY; y <= high; y++)
        {
            if (cnt[y] == 0) { sn.Append(" --  "); continue; }
            float ny = sumNy[y] / cnt[y];
            best = Mathf.Min(best, ny);
            sn.Append($"{ny:F2} ");
        }

        if (print)
        {
            var sb = new System.Text.StringBuilder($"[probe] cliff {tag,10} drop={drop,2} face vertex X by y: ");
            for (int y = loY; y <= high; y++) { sb.Append(seen[y] ? $"{minX[y]:F2} " : " --  "); }
            GD.Print(sb.ToString());
            sn.Append($"| flattest {best:F2}");
            GD.Print(sn.ToString());
        }
        return best;
    }

    // Can a step be given a wall face at ONE voxel — the height every character
    // walks up without a jump? Two candidate levers, measured against the
    // wallBand.x (0.3-0.4) the wall tile needs:
    //   Y-snap   — what worldgen stamps today, but only if the step is not
    //              classified as a grade first (maxGradeStep must be 0, or a
    //              1-voxel delta is smoothed by definition and never gets here).
    //   All-snap — SharpAxes.All on the riser voxels alone. DC then places their
    //              vertices on the voxel-grid corner, so the face is cubic, and
    //              the shader takes its face normal verbatim (sharpness = 1)
    //              instead of an averaged one.
    public static void StepTexture()
    {
        GD.Print("[step] drop | axis Ysnap  axis All | diag Ysnap  diag All   (want < 0.30)");
        foreach (int drop in new[] { 1, 2, 3 })
        {
            float ay = CliffFace(drop, false);
            float aa = CliffFace(drop, false, sharpRiser: true);
            float dy = CliffFace(drop, false, diagonal: true);
            float da = CliffFace(drop, false, diagonal: true, sharpRiser: true);
            GD.Print($"[step] {drop,4} | {ay,10:F2} {aa,9:F2} | {dy,10:F2} {da,9:F2}");
        }
    }

    // The crease gate against both things it has to serve at once: can a SHORT
    // wall keep a wall-tile normal, and do RAMPS still come out unbanded? The
    // gate is the only knob that can separate them — a riser's lips sit ~45
    // degrees away (dot ~0.7) while a ramp's neighbours are within a few degrees
    // (dot ~1.0), so there is a threshold between the two if the numbers allow.
    // Wall columns want to be BELOW wallBand.x (0.3-0.4); ramp spreads want to
    // stay near zero; tunnel wants 1.000 (buried air must not tilt the surface).
    public static void WallSweep()
    {
        EnsureBound();
        float minDot0 = ChunkMesherDC.NORMAL_SMOOTH_MIN_DOT;
        int relax0 = ChunkMesherDC.VERT_RELAX_ITERATIONS;

        GD.Print("[wall] relax minDot | axis d2 d3 d4 | diag d2 d3 d4 (want <0.30) |"
            + " ramp 1-in-2 1-in-3 (want ~0) | tunnel (want 1.000)");
        foreach (int relax in new[] { 0, 1, 2 })
        {
            foreach (float minDot in new[] { 0.5f, 0.7f, 0.8f, 0.9f, 0.95f, 0.99f })
            {
                ChunkMesherDC.VERT_RELAX_ITERATIONS = relax;
                ChunkMesherDC.NORMAL_SMOOTH_MIN_DOT = minDot;
                float a2 = CliffFace(2, false);
                float a3 = CliffFace(3, false);
                float a4 = CliffFace(4, false);
                float g2 = CliffFace(2, false, diagonal: true);
                float g3 = CliffFace(3, false, diagonal: true);
                float g4 = CliffFace(4, false, diagonal: true);
                RUN = 2; float r2 = RampSpread();
                RUN = 3; float r3 = RampSpread();
                float tn = TunnelWorstNy();
                GD.Print($"[wall] {relax,5} {minDot,6:F2} | {a2,5:F2} {a3,5:F2} {a4,5:F2}"
                    + $" | {g2,5:F2} {g3,5:F2} {g4,5:F2} | {r2,6:F3} {r3,6:F3} | {tn,6:F3}");
            }
        }

        ChunkMesherDC.NORMAL_SMOOTH_MIN_DOT = minDot0;
        ChunkMesherDC.VERT_RELAX_ITERATIONS = relax0;
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

        var v = new int[N, N, N];
        var top = new int[N];
        for (int x = 0; x < N; x++)
        {
            top[x] = Mathf.Clamp(1 + x / RUN, 0, N - 1);
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= top[x]; y++) { v[x, y, z] = Blocks.GroundId; }
            }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);
        ChunkMesherDC.Build(new ChunkState(Vector3I.Zero), Get,
            (x, y, z) => Blocks.DefaultShape(Get(x, y, z)),
            (x, y, z) => 0, (x, y, z) => 0, (x, y, z) => LightEngine.MAX_LIGHT, (x, y, z) => false, (x, y, z) => true,
            st, 0, 0, 0, out bool hasAnyFace, out DcCellSurface _);
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
        var v = new int[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= 7; y++) { v[x, y, z] = Blocks.GroundId; }
            }
        }
        for (int x = 0; x < N; x++)
        {
            for (int z = 4; z <= 7; z++)
            {
                for (int y = 5; y <= 6; y++) { v[x, y, z] = Blocks.AirId; }
            }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);

        // Mimics LightEngine.ComputeSunlight's column scan: an air voxel is lit
        // only when nothing solid stands between it and the sky.
        int Sun(int x, int y, int z)
        {
            if (Blocks.IsSolid(Get(x, y, z))) { return 0; }
            for (int up = y + 1; up < N; up++)
            {
                if (Blocks.IsSolid(Get(x, up, z))) { return 0; }
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
            (x, y, z) => Blocks.DefaultShape(Get(x, y, z)),
            (x, y, z) => 0, (x, y, z) => 0, Sun, (x, y, z) => false, (x, y, z) => true,
            st, 0, 0, 0, out bool hasAnyFace, out DcCellSurface _);
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
        var v = new int[N, N, N];
        for (int x = 0; x < N; x++)
        {
            int top = x < 8 ? 10 : 3;
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= top; y++) { v[x, y, z] = Blocks.GroundId; }
            }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);
        int Sun(int x, int y, int z)
        {
            if (Blocks.IsSolid(Get(x, y, z))) { return 0; }
            for (int up = y + 1; up < N; up++)
            {
                if (Blocks.IsSolid(Get(x, up, z))) { return 0; }
            }
            return LightEngine.MAX_LIGHT;
        }

        var st = new SurfaceTool();
        st.Begin(Godot.Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < 4; i++) { st.SetCustomFormat(i, SurfaceTool.CustomFormat.RgbaFloat); }
        ChunkMesherDC.Build(new ChunkState(Vector3I.Zero), Get,
            (x, y, z) => SharpAxes.Y,
            (x, y, z) => 0, (x, y, z) => 0, Sun, (x, y, z) => false, (x, y, z) => true,
            st, 0, 0, 0, out bool hasAnyFace, out DcCellSurface _);
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
        var v = new int[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= 7; y++) { v[x, y, z] = Blocks.GroundId; }
            }
        }
        for (int x = 0; x < N; x++)
        {
            for (int z = 4; z <= 7; z++)
            {
                for (int y = 5; y <= 6; y++) { v[x, y, z] = Blocks.AirId; }
            }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);
        var verts = Build(Get, (x, y, z) => Blocks.DefaultShape(Get(x, y, z)), out Vector3[] norms);
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
        var v = new int[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= 7; y++) { v[x, y, z] = Blocks.GroundId; }
            }
        }
        for (int x = 0; x < N; x++)
        {
            for (int z = 4; z <= 7; z++)
            {
                for (int y = 5; y <= 6; y++) { v[x, y, z] = Blocks.AirId; }
            }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);
        var verts = Build(Get, (x, y, z) => Blocks.DefaultShape(Get(x, y, z)), out Vector3[] norms);

        float worst = 1f;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = verts[i];
            if (Mathf.Abs(p.Y - 8f) > 0.01f || p.X < 4f || p.X > 12f || p.Z < 1f || p.Z > 15f) { continue; }
            worst = Mathf.Min(worst, norms[i].Y);
        }
        return worst;
    }

    // Where does a MATERIAL boundary land relative to the voxel boundary it was
    // authored at? Flat ground, TerrainId 1 for x<8 and 2 for x>=8, so geometry
    // is identical everywhere and only the material channel moves. The rendered
    // transition sits midway between the last vertex carrying kit 1 and the
    // first carrying kit 2, so those two X values bracket the seam.
    public static void MaterialRegistration()
    {
        GD.Print($"[matreg] === voxel_center_sampling = {CVars.voxelCenterSampling.Value} ===");
        // Edge roughness carves vertices off the plane by a per-cell hash, which
        // would drop them from the flat-surface filters below.
        float rough = CVars.voxelEdgeRoughness.Value;
        CVars.voxelEdgeRoughness.Value = 0f;
        FlatKitSplit();
        FlatTileSplit();
        WallOnGround();
        BuildingCrossSection();
        CVars.voxelEdgeRoughness.Value = rough;
    }

    // Same measurement on the TILE channel: flat ground, Terrain for x<8 and
    // Stone for x>=8.
    private static void FlatTileSplit()
    {
        var v = new int[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= 7; y++) { v[x, y, z] = x < 8 ? Blocks.GroundId : Blocks.StoneId; }
            }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);
        var verts = BuildIds(Get, (x, y, z) => Blocks.DefaultShape(Get(x, y, z)), (x, y, z) => 1,
            out int[] tiles, out int[] kits, out Vector3[] norms);
        int stoneTile = Blocks.StoneId;
        var byX = new SortedDictionary<float, SortedSet<string>>();
        for (int i = 0; i < verts.Length; i++)
        {
            if (norms[i].Y < 0.9f || Mathf.Abs(verts[i].Y - 8f) > 0.01f || verts[i].Z < 4f || verts[i].Z > 12f) { continue; }
            float x = Mathf.Round(verts[i].X * 100f) / 100f;
            if (!byX.TryGetValue(x, out var set)) { set = new SortedSet<string>(); byX[x] = set; }
            set.Add(tiles[i] == stoneTile ? "stone" : "grass");
        }
        var parts = new List<string>();
        foreach (var kv in byX) { parts.Add($"{kv.Key:F1}:{string.Join("/", kv.Value)}"); }
        GD.Print($"[matreg] flat tile split (authored seam at x=8.0) vertexX:tile = {string.Join(" ", parts)}");
    }

    private static void FlatKitSplit()
    {
        var v = new int[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= 7; y++) { v[x, y, z] = Blocks.GroundId; }
            }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);
        var verts = BuildIds(Get, (x, y, z) => SharpAxes.Y, (x, y, z) => x < 8 ? 1 : 2,
            out int[] tiles, out int[] kits, out Vector3[] norms);
        var byX = new SortedDictionary<float, SortedSet<int>>();
        for (int i = 0; i < verts.Length; i++)
        {
            if (norms[i].Y < 0.9f || Mathf.Abs(verts[i].Y - 8f) > 0.01f || verts[i].Z < 4f || verts[i].Z > 12f) { continue; }
            float x = Mathf.Round(verts[i].X * 100f) / 100f;
            if (!byX.TryGetValue(x, out var set)) { set = new SortedSet<int>(); byX[x] = set; }
            set.Add(kits[i]);
        }
        var parts = new List<string>();
        foreach (var kv in byX) { parts.Add($"{kv.Key:F1}:{string.Join("/", kv.Value)}"); }
        GD.Print($"[matreg] flat kit split (authored seam at x=8.0) vertexX:kit = {string.Join(" ", parts)}");
    }

    // A hard block sitting ON soft ground — the stone-wall-in-grass case. The
    // wall's own faces must read pure stone, and the ground quads beside it must
    // stay pure grass rather than gradient into the wall's material.
    private static void WallOnGround()
    {
        var v = new int[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= 7; y++) { v[x, y, z] = Blocks.GroundId; }
            }
        }
        for (int z = 0; z < N; z++)
        {
            for (int y = 8; y <= 10; y++) { v[8, y, z] = Blocks.StoneId; }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);
        var verts = BuildIds(Get, (x, y, z) => Blocks.DefaultShape(Get(x, y, z)), (x, y, z) => 1,
            out int[] tiles, out int[] kits, out Vector3[] norms);
        int stoneTile = Blocks.StoneId;
        var byX = new SortedDictionary<float, SortedSet<string>>();
        for (int i = 0; i < verts.Length; i++)
        {
            if (norms[i].Y < 0.9f || Mathf.Abs(verts[i].Y - 8f) > 0.01f || verts[i].Z < 4f || verts[i].Z > 12f) { continue; }
            float x = Mathf.Round(verts[i].X * 100f) / 100f;
            if (!byX.TryGetValue(x, out var set)) { set = new SortedSet<string>(); byX[x] = set; }
            set.Add(tiles[i] == stoneTile ? "stone" : "grass");
        }
        var parts = new List<string>();
        foreach (var kv in byX) { parts.Add($"{kv.Key:F1}:{string.Join("/", kv.Value)}"); }
        GD.Print($"[matreg] stone wall on grass at x=8 — ground vertexX:tile = {string.Join(" ", parts)}");
    }

    // The reported artefact: a stone building on grass. Ground is Terrain
    // (kit 1) everywhere; the building's floor is Stone and its walls are Stone
    // columns at x=4 and x=12. Prints the top-surface tile per vertex X so the
    // -X and +X seams can be compared against each other.
    private static void BuildingCrossSection()
    {
        var v = new int[N, N, N];
        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                for (int y = 0; y <= 7; y++) { v[x, y, z] = Blocks.GroundId; }
            }
        }
        for (int x = 4; x <= 12; x++)
        {
            for (int z = 4; z <= 12; z++) { v[x, 7, z] = Blocks.StoneId; }
        }
        for (int y = 8; y <= 10; y++)
        {
            for (int z = 4; z <= 12; z++) { v[4, y, z] = Blocks.StoneId; v[12, y, z] = Blocks.StoneId; }
            for (int x = 4; x <= 12; x++) { v[x, y, 4] = Blocks.StoneId; v[x, y, 12] = Blocks.StoneId; }
        }
        int Get(int x, int y, int z) => Sample(v, x, y, z);
        var verts = BuildIds(Get, (x, y, z) => Blocks.DefaultShape(Get(x, y, z)), (x, y, z) => 1,
            out int[] tiles, out int[] kits, out Vector3[] norms);
        int stoneTile = Blocks.StoneId;
        var byX = new SortedDictionary<float, SortedSet<string>>();
        for (int i = 0; i < verts.Length; i++)
        {
            // Floor level only, down the middle of the building in Z.
            if (norms[i].Y < 0.9f || Mathf.Abs(verts[i].Y - 8f) > 0.01f || Mathf.Abs(verts[i].Z - 8f) > 1.01f) { continue; }
            float x = Mathf.Round(verts[i].X * 100f) / 100f;
            if (!byX.TryGetValue(x, out var set)) { set = new SortedSet<string>(); byX[x] = set; }
            set.Add(tiles[i] == stoneTile ? "stone" : "grass");
        }
        var parts = new List<string>();
        foreach (var kv in byX) { parts.Add($"{kv.Key:F1}:{string.Join("/", kv.Value)}"); }
        GD.Print($"[matreg] building floor x=[4..12] walls at x=4,12 — vertexX:tile = {string.Join(" ", parts)}");
    }

    // Build + return each vertex's OWN tile/kit id, decoded from the flat
    // per-triangle id triple (CUSTOM0.xyz / CUSTOM1.yzw) via the vertex's
    // barycentric selector in COLOR.rgb.
    private static Vector3[] BuildIds(Func<int, int, int, int> get,
        Func<int, int, int, SharpAxes> shape,
        Func<int, int, int, int> terrainId,
        out int[] tiles, out int[] kits, out Vector3[] norms)
    {
        var st = new SurfaceTool();
        st.Begin(Godot.Mesh.PrimitiveType.Triangles);
        st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(1, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(2, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(3, SurfaceTool.CustomFormat.RgbaFloat);
        ChunkMesherDC.Build(new ChunkState(Vector3I.Zero), get, shape,
            terrainId, (x, y, z) => 0, (x, y, z) => LightEngine.MAX_LIGHT, (x, y, z) => false, (x, y, z) => true,
            st, 0, 0, 0, out bool hasAnyFace, out DcCellSurface _);
        if (!hasAnyFace)
        {
            tiles = Array.Empty<int>();
            kits = Array.Empty<int>();
            norms = Array.Empty<Vector3>();
            return Array.Empty<Vector3>();
        }
        var arrays = st.Commit().SurfaceGetArrays(0);
        Vector3[] verts = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
        norms = arrays[(int)Godot.Mesh.ArrayType.Normal].AsVector3Array();
        Color[] colors = arrays[(int)Godot.Mesh.ArrayType.Color].AsColorArray();
        float[] c0 = arrays[(int)Godot.Mesh.ArrayType.Custom0].AsFloat32Array();
        float[] c1 = arrays[(int)Godot.Mesh.ArrayType.Custom1].AsFloat32Array();
        tiles = new int[verts.Length];
        kits = new int[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            int sel = colors[i].R > 0.5f ? 0 : (colors[i].G > 0.5f ? 1 : 2);
            tiles[i] = Mathf.RoundToInt(c0[i * 4 + sel]);
            kits[i] = Mathf.RoundToInt(c1[i * 4 + 1 + sel]);
        }
        return verts;
    }

    private static void Shapes()
    {
        var door = new int[N, N, N];
        for (int x = 4; x <= 12; x++)
        {
            for (int y = 4; y <= 8; y++) { door[x, y, 8] = Blocks.StoneId; }
        }
        for (int y = 4; y <= 6; y++) { door[8, y, 8] = Blocks.AirId; }
        int Get(int x, int y, int z) => Sample(door, x, y, z);
        var verts = Build(Get, (x, y, z) => Blocks.DefaultShape(Get(x, y, z)), out _);
        var hits = new SortedSet<float>();
        foreach (Vector3 p in verts)
        {
            if (p.X < 7f || p.X > 10f || p.Y < 5f || p.Y > 6f || p.Z < 7f || p.Z > 10f) { continue; }
            hits.Add(Mathf.Round(p.X * 100f) / 100f);
        }
        GD.Print($"[probe] doorway jambs X=[{string.Join(", ", hits)}] (want 8 and 9 present)");
    }

    private static int Sample(int[,,] v, int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= N || y >= N || z >= N) { return Blocks.AirId; }
        return v[x, y, z];
    }

    private static Vector3[] Build(Func<int, int, int, int> get,
        Func<int, int, int, SharpAxes> shape, out Vector3[] norms)
    {
        var st = new SurfaceTool();
        st.Begin(Godot.Mesh.PrimitiveType.Triangles);
        st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(1, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(2, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(3, SurfaceTool.CustomFormat.RgbaFloat);
        ChunkMesherDC.Build(new ChunkState(Vector3I.Zero), get, shape,
            (x, y, z) => 0, (x, y, z) => 0, (x, y, z) => LightEngine.MAX_LIGHT, (x, y, z) => false, (x, y, z) => true,
            st, 0, 0, 0, out bool hasAnyFace, out DcCellSurface _);
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
