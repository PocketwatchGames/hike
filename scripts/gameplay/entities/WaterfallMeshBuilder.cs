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

    // How finely a drawdown quad is subdivided along the flow. Its UVs are
    // sampled from a distance field rather than from its own axes, so the quad
    // needs enough interior vertices to follow that field where it curves.
    private const int DRAWDOWN_STEPS = 3;

    // Arc length along the lip line: one measurement per step, so `u` is
    // CONTINUOUS along the whole brink and through its corners.
    //
    // It used to be the step's own world-axis projection (start . wide), which is
    // continuous along a straight run and breaks completely at a corner — the two
    // steps there project onto perpendicular axes, so u jumped to an unrelated
    // value and the streak field started over. That is what made corners read as
    // two separate falls meeting, and it is also why the drawdown could not be
    // tied to the sheet: there was no shared coordinate to tie them with.
    private readonly struct LipArc
    {
        // Arc length at the step's midpoint, and which way it grows along Wide.
        public readonly float Centre;
        public readonly float Dir;

        public LipArc(float centre, float dir)
        {
            Centre = centre;
            Dir = dir;
        }

        // Arc length at an offset along the step's width axis, in metres.
        public float At(float alongWide) => Centre + alongWide * Dir;
    }

    // Walk the lip steps end to end and hand each one its arc length. Steps join
    // where they share a lattice endpoint, which covers a straight run and a
    // corner alike — a corner is simply where the walk turns, and u carries
    // straight through it.
    private static LipArc[] MeasureLips(WaterfallLip[] lips)
    {
        var atEnd = new Dictionary<Vector2I, List<int>>();
        for (int i = 0; i < lips.Length; i++)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                Vector2I key = EndKey(lips[i], side);
                if (!atEnd.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>();
                    atEnd[key] = list;
                }
                list.Add(i);
            }
        }

        var arcs = new LipArc[lips.Length];
        var walked = new bool[lips.Length];
        float cursor = 0f;
        // Open ends first, so a straight run is walked from one of its ends
        // rather than from the middle; a closed ring has none and starts anywhere.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < lips.Length; i++)
            {
                if (walked[i]) { continue; }
                bool openEnd = atEnd[EndKey(lips[i], -1)].Count == 1
                    || atEnd[EndKey(lips[i], 1)].Count == 1;
                if (pass == 0 && !openEnd) { continue; }

                Vector2I entry = atEnd[EndKey(lips[i], -1)].Count == 1
                    ? EndKey(lips[i], -1) : EndKey(lips[i], 1);
                WalkChain(lips, atEnd, arcs, walked, i, entry, ref cursor);
                // Separate chains must not share arc length, or their streaks
                // would correlate across a gap they have no connection through.
                cursor += CHAIN_SEPARATION;
            }
        }
        return arcs;
    }

    // Arc length between one chain and the next. Large enough that the streak
    // noise decorrelates; the value itself means nothing else.
    private const float CHAIN_SEPARATION = 16f;

    private static void WalkChain(WaterfallLip[] lips, Dictionary<Vector2I, List<int>> atEnd,
        LipArc[] arcs, bool[] walked, int index, Vector2I entry, ref float cursor)
    {
        while (true)
        {
            walked[index] = true;
            Vector2I minus = EndKey(lips[index], -1);
            Vector2I exit = entry == minus ? EndKey(lips[index], 1) : minus;
            // Arc length grows in whichever direction the walk is going, so the
            // step's own +Wide may run either way against it.
            float dir = exit == EndKey(lips[index], 1) ? 1f : -1f;
            arcs[index] = new LipArc(cursor + 0.5f, dir);
            cursor += 1f;

            int next = -1;
            foreach (int candidate in atEnd[exit])
            {
                if (!walked[candidate]) { next = candidate; break; }
            }
            if (next < 0) { return; }
            index = next;
            entry = exit;
        }
    }

    // A step's endpoints, on the voxel lattice so they key exactly.
    private static Vector2I EndKey(WaterfallLip lip, int side)
    {
        Vector3 end = StartCentre(lip, 0f) + Wide(lip) * (0.5f * side);
        return new Vector2I(Mathf.RoundToInt(end.X), Mathf.RoundToInt(end.Z));
    }

    // World XZ of a step's endpoint, for the nearest-lip lookup.
    private static Vector2 EndPos(WaterfallLip lip, int side)
    {
        Vector3 end = StartCentre(lip, 0f) + Wide(lip) * (0.5f * side);
        return new Vector2(end.X, end.Z);
    }

    // The nearest point on the whole lip line to a point on the pool, as the UV
    // the sheet would have there: `u` its arc length, `v` MINUS the distance to
    // it. This is what makes the drawdown converge at a corner — the flow follows
    // the gradient of a distance field, so two lips meeting at a right angle hand
    // over smoothly instead of switching axis, and it is measured against every
    // lip rather than the one that happened to emit the quad.
    private static Vector2 NearestLipUv(Vector2 point, WaterfallLip[] lips, LipArc[] arcs)
    {
        float bestDistance = float.MaxValue;
        float bestU = 0f;
        for (int i = 0; i < lips.Length; i++)
        {
            Vector2 a = EndPos(lips[i], -1);
            Vector2 b = EndPos(lips[i], 1);
            Vector2 ab = b - a;
            float lengthSq = ab.LengthSquared();
            float t = lengthSq > 1e-6f ? Mathf.Clamp((point - a).Dot(ab) / lengthSq, 0f, 1f) : 0f;
            float distance = point.DistanceTo(a + ab * t);
            if (distance >= bestDistance) { continue; }
            bestDistance = distance;
            // t runs from the -Wide end to the +Wide end, so it maps onto the
            // step's own width offset before arc length is taken.
            bestU = arcs[i].At(t - 0.5f);
        }
        return new Vector2(bestU, -bestDistance);
    }

    // The mesh comes back in local space around the entity's world position.
    // Its Mesh is null if there is no lip to sweep.
    public static WaterfallMesh Build(WaterfallSimState data, WaterfallData style)
    {
        if (data.Lips.Length == 0 || data.FallHeight <= 0.01f) { return default; }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        // The drawdown is a SEPARATE surface because it carries a different
        // material: it lies on the pool and composites with it, where the sheet
        // hangs beyond the lip and depth-sorts against it.
        var drawdownTool = new SurfaceTool();
        drawdownTool.Begin(Mesh.PrimitiveType.Triangles);
        LipArc[] arcs = MeasureLips(data.Lips);

        float height = data.FallHeight + Mathf.Max(style.landingDepth, 0f);
        // Resolved once for the whole sheet, from the drop it is actually swept
        // over — the throw grows as the square root of that, so a short fall
        // stands closer to its wall than a tall one instead of further out.
        float reach = style.ReachFor(height);
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
            SweepStrip(st, lip, arcs[System.Array.IndexOf(data.Lips, lip)], data, style, present, height, reach, segments);
        }
        SweepCorners(st, data, arcs, style, height, reach, segments);
        int drawdownQuads = SweepDrawdown(drawdownTool, data, arcs, style);

        ArrayMesh mesh = st.Commit();
        int drawdownSurface = -1;
        if (drawdownQuads > 0)
        {
            drawdownSurface = mesh.GetSurfaceCount();
            mesh = drawdownTool.Commit(mesh);
        }
        return new WaterfallMesh(mesh, 0, drawdownSurface);
    }

    // One metre-wide step of the lip, swept down the jet's profile.
    private static void SweepStrip(SurfaceTool st, WaterfallLip lip, LipArc arc, WaterfallSimState data,
        WaterfallData style, HashSet<Vector2I> present, float height, float reach, int segments)
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

        // Arc length along the lip, not a world-axis projection — see LipArc.
        float uL = arc.At(-halfLeft);
        float uR = arc.At(halfRight);

        Vector3 prevL = Vector3.Zero;
        Vector3 prevR = Vector3.Zero;
        Vector3 prevNormal = Vector3.Zero;
        float prevV = 0f;
        for (int i = 0; i <= segments; i++)
        {
            float s = Profile(i / (float)segments, style.shoulderBias);
            Vector3 point = Trace(start, pour, s, reach, height);
            // Per RING, not per quad: a quad shaded with a single normal is
            // flat, and a curve built out of flat quads bands. Handing each ring
            // its own normal lets the rasterizer interpolate down the curve, and
            // because a ring's normal is a pure function of `s` the quad above
            // and the quad below agree on it exactly.
            Vector3 normal = Normal(s, reach, height, pour, wide);
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

    // The drawdown: the sheet carried on UPSTREAM of the lip, flat across the
    // pool it is leaving.
    //
    // Its UVs come from the NEAREST POINT ON THE WHOLE LIP LINE, not from the
    // axes of the step that emitted the quad. That is what makes a corner work:
    // the streak field is elongated along v, so the ropes follow the gradient of
    // a distance field and converge into the corner instead of switching axis by
    // ninety degrees where two perpendicular steps meet. It is also what ties the
    // run to the sheet — at the lip the distance is zero and the arc length is
    // the step's own, so the UV a rope arrives with is exactly the UV the sheet
    // continues it from.
    //
    // Pool columns are CLAIMED as they are covered, and a run stops at the first
    // one already taken. At an outside corner two perpendicular steps are fed by
    // the same column and would otherwise both extend back across it — coplanar,
    // coincident, and z-fighting. The drawdown there is genuinely shared water,
    // so first-come is not an approximation.
    private static int SweepDrawdown(SurfaceTool st, WaterfallSimState data, LipArc[] arcs,
        WaterfallData style)
    {
        float length = Mathf.Max(style.drawdownLength, 0f);
        if (length <= 0.01f) { return 0; }

        int quads = 0;
        var claimed = new HashSet<Vector2I>();
        foreach (WaterfallLip lip in data.Lips)
        {
            Vector3 pour = Pour(lip);
            Vector3 wide = Wide(lip);
            Vector3 start = StartCentre(lip, data.TopY);

            // Walk back a column at a time, stopping where the pool is already
            // covered. Whole metres, because that is the grid the columns are on.
            float reachBack = 0f;
            for (int k = 1; k <= Mathf.CeilToInt(length); k++)
            {
                var column = new Vector2I(lip.X - lip.DirX * k, lip.Z - lip.DirZ * k);
                if (!claimed.Add(column)) { break; }
                reachBack = Mathf.Min(k, length);
            }
            if (reachBack <= 0.01f) { continue; }

            // Subdivided along the flow so the UVs can follow the distance field
            // where it curves; a single quad would interpolate straight across a
            // corner it is supposed to bend around.
            Vector3 prevL = Vector3.Zero;
            Vector3 prevR = Vector3.Zero;
            Vector2 prevUvL = Vector2.Zero;
            Vector2 prevUvR = Vector2.Zero;
            for (int i = 0; i <= DRAWDOWN_STEPS; i++)
            {
                // From the far end IN, so the last ring lands exactly on the lip.
                float back = reachBack * (1f - i / (float)DRAWDOWN_STEPS);
                Vector3 centre = start - pour * back;
                Vector3 l = centre - wide * 0.5f;
                Vector3 r = centre + wide * 0.5f;
                Vector2 uvL = NearestLipUv(new Vector2(l.X, l.Z), data.Lips, arcs);
                Vector2 uvR = NearestLipUv(new Vector2(r.X, r.Z), data.Lips, arcs);
                if (i > 0)
                {
                    quads++;
                    QuadUv(st, Vector3.Up,
                        prevL - data.WorldPosition, l - data.WorldPosition,
                        r - data.WorldPosition, prevR - data.WorldPosition,
                        prevUvL, uvL, uvR, prevUvR);
                }
                prevL = l;
                prevR = r;
                prevUvL = uvL;
                prevUvR = uvR;
            }
        }
        return quads;
    }

    // Where two perpendicular strips leave the SAME pool column, they share a
    // corner at the lip and then diverge, opening a widening wedge between them
    // as they fall. This closes it with a quarter-turn skirt swept from the same
    // profile, so a fall pouring off an outside corner reads as one continuous
    // sheet wrapping it rather than two curtains with a gap.
    private static void SweepCorners(SurfaceTool st, WaterfallSimState data, LipArc[] arcs,
        WaterfallData style, float height, float reach, int segments)
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
                    // The skirt hangs from the arc length the two steps SHARE at
                    // this endpoint, so its streaks continue theirs instead of
                    // starting over in the wedge between them.
                    float uApex = ArcAtEnd(data.Lips, arcs, list[a], kv.Key);
                    SweepCorner(st, apex, uApex, Pour(list[a]), Pour(list[b]),
                        data, style, height, reach, segments);
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

    // Arc length of a step at the endpoint keyed by `end`.
    private static float ArcAtEnd(WaterfallLip[] lips, LipArc[] arcs, WaterfallLip lip, Vector2I end)
    {
        int index = System.Array.IndexOf(lips, lip);
        float side = EndKey(lip, 1) == end ? 0.5f : -0.5f;
        return arcs[index].At(side);
    }

    private static void SweepCorner(SurfaceTool st, Vector3 apex, float uApex, Vector3 dirA, Vector3 dirB,
        WaterfallSimState data, WaterfallData style, float height, float reach, int segments)
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
                ring[k] = Trace(apex, dir, s, reach, height) - data.WorldPosition;
                // A skirt curves in two directions at once, so its normals have
                // to vary around the turn as well as down the drop — one per
                // vertex in both axes, or the corner bands in the other one.
                ringNormals[k] = Normal(s, reach, height, dir, Perp(dir));
            }
            if (i > 0)
            {
                for (int k = 0; k < CORNER_STEPS; k++)
                {
                    // U walks around the turn in metres of arc at the reach the
                    // sheet has by then; it only feeds the streak noise, so an
                    // approximate arc length is enough.
                    // Centred on the shared arc length and spreading as the
                    // skirt widens, which it does with depth: at the lip the two
                    // sheets meet at a point and there is no wedge yet.
                    float span = Mathf.Pi * 0.5f * reach * Mathf.Sqrt(s);
                    float u0 = uApex + (k / (float)CORNER_STEPS - 0.5f) * span;
                    float u1 = uApex + ((k + 1) / (float)CORNER_STEPS - 0.5f) * span;
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
    private static Vector3 Trace(Vector3 start, Vector3 pour, float s, float reach, float height)
    {
        return start + pour * (reach * Mathf.Sqrt(s)) - new Vector3(0f, height * s, 0f);
    }

    // Outward-facing normal of the sheet at `s`, from the analytic tangent of
    // the profile crossed with the width axis. Taken a little inside the top
    // because the profile's slope is infinite exactly at the lip.
    private static Vector3 Normal(float s, float reach, float height, Vector3 pour, Vector3 wide)
    {
        float t = Mathf.Max(s, TANGENT_EPSILON);
        // d(reach)/ds = reach / (2 sqrt(s)); the drop's is -height.
        Vector3 tangent = (pour * (reach / (2f * Mathf.Sqrt(t))) - new Vector3(0f, height, 0f)).Normalized();
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

    // As Quad, but each corner carries its OWN uv rather than sharing a u across
    // the width and a v along the length. The drawdown needs it: its uvs are
    // sampled from a distance field, so no two corners of a quad need agree.
    private static void QuadUv(SurfaceTool st, Vector3 normal,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
    {
        Vertex(st, normal, a, uvA.X, uvA.Y);
        Vertex(st, normal, b, uvB.X, uvB.Y);
        Vertex(st, normal, c, uvC.X, uvC.Y);

        Vertex(st, normal, a, uvA.X, uvA.Y);
        Vertex(st, normal, c, uvC.X, uvC.Y);
        Vertex(st, normal, d, uvD.X, uvD.Y);
    }

    private static void Vertex(SurfaceTool st, Vector3 normal, Vector3 position, float u, float v)
    {
        st.SetNormal(normal);
        st.SetUV(new Vector2(u, v));
        st.AddVertex(position);
    }
}

// What Build hands back: the swept geometry, and which of its surfaces is the
// sheet and which the drawdown. They carry different materials because they sort
// differently, and a fall authored with no drawdown has only the one.
public readonly struct WaterfallMesh
{
    public readonly ArrayMesh Mesh;
    public readonly int SheetSurface;
    public readonly int DrawdownSurface;

    public WaterfallMesh(ArrayMesh mesh, int sheetSurface, int drawdownSurface)
    {
        Mesh = mesh;
        SheetSurface = sheetSurface;
        DrawdownSurface = drawdownSurface;
    }
}
