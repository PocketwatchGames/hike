using System.Collections.Generic;
using Godot;

// Sweeps a cascade's lip line into the falling sheet.
//
// The sheet is a JET, not a block of water: it leaves the lip moving outward,
// gravity bends it over, and it enters the pool below a short way out from the
// wall. So the profile is that trajectory — horizontal reach goes as the square
// root of the depth fallen, because depth goes as t² while reach goes as t.
//
// Reconstructing the voxel columns the water passes through was the obvious
// alternative and it is wrong: those columns are a staircase walked out from the
// lip, so skinning them draws a slab of standing water reaching out over the
// terrain, notched wherever the staircase stepped.
public static class WaterfallMeshBuilder
{
    // Segments per metre of fall, and the bounds on the total.
    private const float SEGMENTS_PER_METER = 1.5f;
    private const int MIN_SEGMENTS = 10;
    private const int MAX_SEGMENTS = 32;
    // Steps around a corner's quarter turn.
    private const int CORNER_STEPS = 4;
    // Smallest fraction of the drop used for the tangent at the very top. The
    // profile's slope is infinite at the lip, so the first segment's normal has
    // to be taken a little way in or it degenerates.
    private const float TANGENT_EPSILON = 0.0005f;

    // The mesh comes back in local space around the entity's world position.
    // Returns null if there is no lip to sweep.
    public static ArrayMesh Build(WaterfallSimState data, WaterfallData style)
    {
        if (data.Lips.Length == 0 || data.FallHeight <= 0.01f) { return null; }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        float height = data.FallHeight + Mathf.Max(style.landingDepth, 0f);
        int segments = Mathf.Clamp(
            Mathf.CeilToInt(height * SEGMENTS_PER_METER), MIN_SEGMENTS, MAX_SEGMENTS);

        // Which lip columns exist, so a strip can tell whether it has a
        // neighbour beside it.
        var present = new HashSet<Vector2I>();
        foreach (WaterfallLip lip in data.Lips)
        {
            present.Add(new Vector2I(lip.X, lip.Z));
        }

        foreach (WaterfallLip lip in data.Lips)
        {
            SweepStrip(st, lip, data, style, present, height, segments);
        }
        SweepCorners(st, data, style, height, segments);
        return st.Commit();
    }

    // One metre-wide step of the lip, swept down the jet's profile.
    private static void SweepStrip(SurfaceTool st, WaterfallLip lip, WaterfallSimState data,
        WaterfallData style, HashSet<Vector2I> present, float height, int segments)
    {
        Vector3 pour = Pour(lip);
        // The width axis is the horizontal perpendicular to the pour. Adjacent
        // lip steps facing the same way generate identical vertices along their
        // shared edge, so the strips meet exactly with no seam.
        Vector3 wide = Wide(lip);
        Vector3 start = StartCentre(lip, data.TopY);

        // Tuck in the sides that have no neighbouring strip. Zero by default —
        // the sheet should fill the full metre it pours over — but authorable
        // for a fall that wants a tapered edge.
        var left = new Vector2I(lip.X - Mathf.RoundToInt(wide.X), lip.Z - Mathf.RoundToInt(wide.Z));
        var right = new Vector2I(lip.X + Mathf.RoundToInt(wide.X), lip.Z + Mathf.RoundToInt(wide.Z));
        float inset = Mathf.Clamp(style.edgeInset, 0f, 0.45f);
        float halfLeft = present.Contains(left) ? 0.5f : 0.5f - inset;
        float halfRight = present.Contains(right) ? 0.5f : 0.5f - inset;

        float uCentre = start.X * wide.X + start.Z * wide.Z;
        float uL = uCentre - halfLeft;
        float uR = uCentre + halfRight;

        Vector3 prevL = Vector3.Zero;
        Vector3 prevR = Vector3.Zero;
        Vector3 prevNormal = Vector3.Zero;
        float prevV = 0f;
        for (int i = 0; i <= segments; i++)
        {
            float s = Profile(i / (float)segments, style.shoulderBias);
            Vector3 point = Trace(start, pour, s, style.pourReach, height);
            // Per RING, not per quad: a quad shaded with a single normal is
            // flat, and a curve built out of flat quads bands. Handing each ring
            // its own normal lets the rasterizer interpolate down the curve, and
            // because a ring's normal is a pure function of `s` the quad above
            // and the quad below agree on it exactly.
            Vector3 normal = Normal(s, style.pourReach, height, pour, wide);
            Vector3 l = point - wide * halfLeft - data.WorldPosition;
            Vector3 r = point + wide * halfRight - data.WorldPosition;
            float v = height * s;
            if (i > 0)
            {
                Quad(st, prevNormal, normal, normal, prevNormal,
                    prevL, l, r, prevR, uL, uR, prevV, v);
            }
            prevL = l;
            prevR = r;
            prevNormal = normal;
            prevV = v;
        }
    }

    // Where two perpendicular strips leave the SAME pool column, they share a
    // corner at the lip and then diverge, opening a widening wedge between them
    // as they fall. This closes it with a quarter-turn skirt swept from the same
    // profile, so a fall pouring off an outside corner reads as one continuous
    // sheet wrapping it rather than two curtains with a gap.
    private static void SweepCorners(SurfaceTool st, WaterfallSimState data, WaterfallData style,
        float height, int segments)
    {
        // Lip-line endpoints land exactly on the voxel lattice, so they key
        // cleanly with no float slop.
        var meeting = new Dictionary<Vector2I, List<WaterfallLip>>();
        foreach (WaterfallLip lip in data.Lips)
        {
            Vector3 start = StartCentre(lip, data.TopY);
            Vector3 wide = Wide(lip);
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 end = start + wide * (0.5f * side);
                var key = new Vector2I(Mathf.RoundToInt(end.X), Mathf.RoundToInt(end.Z));
                if (!meeting.TryGetValue(key, out List<WaterfallLip> list))
                {
                    list = new List<WaterfallLip>();
                    meeting[key] = list;
                }
                list.Add(lip);
            }
        }

        foreach (KeyValuePair<Vector2I, List<WaterfallLip>> kv in meeting)
        {
            List<WaterfallLip> list = kv.Value;
            for (int a = 0; a < list.Count; a++)
            {
                for (int b = a + 1; b < list.Count; b++)
                {
                    if (!IsOutsideCorner(list[a], list[b])) { continue; }
                    var apex = new Vector3(kv.Key.X, data.TopY, kv.Key.Y);
                    SweepCorner(st, apex, Pour(list[a]), Pour(list[b]), data, style, height, segments);
                }
            }
        }
    }

    // Two steps of the lip turn an outside corner when they pour in
    // perpendicular directions AND are fed by the same pool column — that pool
    // corner is the point both sheets hang from. Perpendicular steps fed by
    // DIFFERENT columns are an inside corner, where the two sheets converge
    // instead of parting and there is no gap to fill.
    private static bool IsOutsideCorner(WaterfallLip a, WaterfallLip b)
    {
        if (a.DirX * b.DirX + a.DirZ * b.DirZ != 0) { return false; }
        return a.X - a.DirX == b.X - b.DirX && a.Z - a.DirZ == b.Z - b.DirZ;
    }

    private static void SweepCorner(SurfaceTool st, Vector3 apex, Vector3 dirA, Vector3 dirB,
        WaterfallSimState data, WaterfallData style, float height, int segments)
    {
        var ring = new Vector3[CORNER_STEPS + 1];
        var ringNormals = new Vector3[CORNER_STEPS + 1];
        var prev = new Vector3[CORNER_STEPS + 1];
        var prevNormals = new Vector3[CORNER_STEPS + 1];
        float prevV = 0f;
        for (int i = 0; i <= segments; i++)
        {
            float s = Profile(i / (float)segments, style.shoulderBias);
            float v = height * s;
            for (int k = 0; k <= CORNER_STEPS; k++)
            {
                // Lerping the two axis directions and renormalizing sweeps the
                // short way round, through the outward diagonal — which is the
                // convex side, the one the water is actually on.
                Vector3 dir = dirA.Lerp(dirB, k / (float)CORNER_STEPS).Normalized();
                ring[k] = Trace(apex, dir, s, style.pourReach, height) - data.WorldPosition;
                // A skirt curves in two directions at once, so its normals have
                // to vary around the turn as well as down the drop — one per
                // vertex in both axes, or the corner bands in the other one.
                ringNormals[k] = Normal(s, style.pourReach, height, dir, Perp(dir));
            }
            if (i > 0)
            {
                for (int k = 0; k < CORNER_STEPS; k++)
                {
                    // U walks around the turn in metres of arc at the reach the
                    // sheet has by then; it only feeds the streak noise, so an
                    // approximate arc length is enough.
                    float u0 = k / (float)CORNER_STEPS * Mathf.Pi * 0.5f * style.pourReach;
                    float u1 = (k + 1) / (float)CORNER_STEPS * Mathf.Pi * 0.5f * style.pourReach;
                    Quad(st, prevNormals[k], ringNormals[k], ringNormals[k + 1], prevNormals[k + 1],
                        prev[k], ring[k], ring[k + 1], prev[k + 1], u0, u1, prevV, v);
                }
            }
            System.Array.Copy(ring, prev, ring.Length);
            System.Array.Copy(ringNormals, prevNormals, ringNormals.Length);
            prevV = v;
        }
    }

    // Sample distribution down the profile. Uniform steps in height leave the
    // FIRST polygon spanning a whole segment of drop while the curve has only
    // just started to bend, so the sheet leaves the lip at an angle instead of
    // rolling off it. Biasing the samples toward the top puts several polygons
    // inside the first few centimetres, where the tangent is still horizontal —
    // which is what lets the top of the fall sit flush with the water surface.
    private static float Profile(float u, float bias)
    {
        return Mathf.Pow(Mathf.Clamp(u, 0f, 1f), Mathf.Max(bias, 1f));
    }

    // The jet at depth fraction `s`: out along the pour by the ballistic reach,
    // down by the drop.
    private static Vector3 Trace(Vector3 start, Vector3 pour, float s, float pourReach, float height)
    {
        return start + pour * (pourReach * Mathf.Sqrt(s)) - new Vector3(0f, height * s, 0f);
    }

    // Outward-facing normal of the sheet at `s`, from the analytic tangent of
    // the profile crossed with the width axis. Taken a little inside the top
    // because the profile's slope is infinite exactly at the lip.
    private static Vector3 Normal(float s, float pourReach, float height, Vector3 pour, Vector3 wide)
    {
        float t = Mathf.Max(s, TANGENT_EPSILON);
        // d(reach)/ds = pourReach / (2 sqrt(s)); the drop's is -height.
        Vector3 tangent = (pour * (pourReach / (2f * Mathf.Sqrt(t))) - new Vector3(0f, height, 0f)).Normalized();
        Vector3 normal = wide.Cross(tangent).Normalized();
        return normal.Dot(pour) < 0f ? -normal : normal;
    }

    private static Vector3 Pour(WaterfallLip lip) => new Vector3(lip.DirX, 0f, lip.DirZ);

    private static Vector3 Wide(WaterfallLip lip) => new Vector3(-lip.DirZ, 0f, lip.DirX);

    private static Vector3 Perp(Vector3 dir) => new Vector3(-dir.Z, 0f, dir.X);

    // The lip's own outer face — the boundary between the pool and the drop,
    // which is where the water leaves the ground.
    private static Vector3 StartCentre(WaterfallLip lip, float topY)
    {
        return new Vector3(lip.X + 0.5f, topY, lip.Z + 0.5f) - Pour(lip) * 0.5f;
    }

    // a = previous-left, b = left, c = right, d = previous-right, each with its
    // OWN normal. The mesh carries no index buffer, so a vertex's normal is
    // whatever was set as it was added — which is what makes smooth shading a
    // matter of handing each corner the right one rather than of merging
    // vertices or generating smoothing groups.
    private static void Quad(SurfaceTool st, Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        float uLeft, float uRight, float vTop, float vBottom)
    {
        Vertex(st, na, a, uLeft, vTop);
        Vertex(st, nb, b, uLeft, vBottom);
        Vertex(st, nc, c, uRight, vBottom);

        Vertex(st, na, a, uLeft, vTop);
        Vertex(st, nc, c, uRight, vBottom);
        Vertex(st, nd, d, uRight, vTop);
    }

    private static void Vertex(SurfaceTool st, Vector3 normal, Vector3 position, float u, float v)
    {
        st.SetNormal(normal);
        st.SetUV(new Vector2(u, v));
        st.AddVertex(position);
    }
}
