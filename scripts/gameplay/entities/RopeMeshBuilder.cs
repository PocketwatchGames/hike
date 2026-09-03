using Godot;

// Sweeps the hanging rope. Built in code rather than authored because the one
// thing that varies is the only thing an authored mesh fixes: a rope is as long
// as its drop, and every placement's drop is different.
//
// Local space, hanging from the origin down -Y, so the entity seats the whole
// rope by placing one node at the top of the line.
public static class RopeMeshBuilder
{
    // Rings per metre. A rope is a thin silhouette against rock, so it needs
    // segments only where the knot modulation does.
    private const float RINGS_PER_METER = 4f;
    private const int MIN_RINGS = 4;
    private const int MAX_RINGS = 256;

    // How far the radius swells at a knot, as a fraction of the base radius.
    // Reads as a hand-laid rope at a distance instead of a smooth dowel.
    private const float KNOT_BULGE = 0.35f;
    // Metres between knots.
    private const float KNOT_SPACING = 0.5f;

    // `length` is the drop plus whatever stub stands above the anchor; `sides`
    // is the tube's cross-section count.
    public static ArrayMesh Build(float length, float radius, int sides)
    {
        if (length <= 0f || radius <= 0f || sides < 3)
        {
            return null;
        }

        int rings = Mathf.Clamp(Mathf.CeilToInt(length * RINGS_PER_METER), MIN_RINGS, MAX_RINGS);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        for (int r = 0; r < rings; r++)
        {
            float y0 = -length * r / rings;
            float y1 = -length * (r + 1) / rings;
            float r0 = RadiusAt(radius, -y0);
            float r1 = RadiusAt(radius, -y1);
            for (int s = 0; s < sides; s++)
            {
                float a0 = Mathf.Tau * s / sides;
                float a1 = Mathf.Tau * (s + 1) / sides;
                Vector3 n0 = new(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
                Vector3 n1 = new(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                // v runs in metres so a tiling rope texture keeps its scale
                // whatever the drop is.
                Quad(st,
                    n0, new Vector3(n0.X * r0, y0, n0.Z * r0), (float)s / sides, -y0,
                    n1, new Vector3(n1.X * r0, y0, n1.Z * r0), (float)(s + 1) / sides, -y0,
                    n1, new Vector3(n1.X * r1, y1, n1.Z * r1), (float)(s + 1) / sides, -y1,
                    n0, new Vector3(n0.X * r1, y1, n0.Z * r1), (float)s / sides, -y1);
            }
        }

        return st.Commit();
    }

    private static float RadiusAt(float radius, float distanceDown)
    {
        float phase = distanceDown / KNOT_SPACING * Mathf.Tau;
        return radius * (1f + KNOT_BULGE * Mathf.Sin(phase));
    }

    private static void Quad(SurfaceTool st,
        Vector3 na, Vector3 pa, float ua, float va,
        Vector3 nb, Vector3 pb, float ub, float vb,
        Vector3 nc, Vector3 pc, float uc, float vc,
        Vector3 nd, Vector3 pd, float ud, float vd)
    {
        // Clockwise seen from outside the tube — Godot front-faces are CW, the
        // opposite of the OpenGL convention, so the a-b-c order these corners
        // read in draws the rope inside out.
        Vertex(st, na, pa, ua, va);
        Vertex(st, nc, pc, uc, vc);
        Vertex(st, nb, pb, ub, vb);

        Vertex(st, na, pa, ua, va);
        Vertex(st, nd, pd, ud, vd);
        Vertex(st, nc, pc, uc, vc);
    }

    private static void Vertex(SurfaceTool st, Vector3 normal, Vector3 position, float u, float v)
    {
        st.SetNormal(normal);
        st.SetUV(new Vector2(u, v));
        st.AddVertex(position);
    }
}
