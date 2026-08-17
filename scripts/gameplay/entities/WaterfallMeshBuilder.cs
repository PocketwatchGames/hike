using System.Collections.Generic;
using Godot;

// Sweeps a cascade's lip line into the falling sheet.
//
// The sheet is a JET, not a block of water: it leaves the lip moving outward,
// gravity bends it over, and it enters the pool below a short way out from the
// wall. So the profile is the trajectory of that jet, in the fall's own height
// as the parameter — horizontal reach goes as sqrt(depth fallen) because depth
// goes as t² while reach goes as t. That is what makes the top a rounded convex
// shoulder rolling off the lip rather than a corner, and it is why the reach is
// nearly all spent in the first metre of the drop.
//
// Reconstructing the voxel columns the water passes through was the obvious
// alternative and it is wrong: those columns are a staircase walked out from the
// lip, so skinning them draws a slab of standing water reaching out over the
// terrain, notched wherever the staircase stepped.
public static class WaterfallMeshBuilder
{
    // Segments per metre of fall, and the bounds on the total. The shoulder is
    // where the curve actually bends, so this is about resolving the top metre;
    // below that the sheet is nearly straight and more segments buy nothing.
    private const float SEGMENTS_PER_METER = 1.5f;
    private const int MIN_SEGMENTS = 6;
    private const int MAX_SEGMENTS = 28;
    // Smallest fraction of the drop used for the tangent at the very top. The
    // profile's slope is infinite at the lip, so the first segment's normal has
    // to be taken a little way in or it degenerates.
    private const float TANGENT_EPSILON = 0.02f;

    // `origin` is the entity's world position; the mesh comes back in local
    // space around it. Returns null if there is no lip to sweep.
    public static ArrayMesh Build(WaterfallSimState data, WaterfallData style)
    {
        if (data.Lips.Length == 0 || data.FallHeight <= 0.01f) { return null; }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        // Which lip columns exist, so a strip can tell whether it has a
        // neighbour beside it — an open side gets tucked in, which is what keeps
        // the sheet's edges from ending on a hard metre-wide corner.
        var present = new HashSet<Vector2I>();
        foreach (WaterfallLip lip in data.Lips)
        {
            present.Add(new Vector2I(lip.X, lip.Z));
        }

        float height = data.FallHeight + Mathf.Max(style.landingDepth, 0f);
        int segments = Mathf.Clamp(
            Mathf.CeilToInt(height * SEGMENTS_PER_METER), MIN_SEGMENTS, MAX_SEGMENTS);
        bool any = false;
        foreach (WaterfallLip lip in data.Lips)
        {
            SweepStrip(st, lip, data, style, present, height, segments);
            any = true;
        }
        return any ? st.Commit() : null;
    }

    private static void SweepStrip(SurfaceTool st, WaterfallLip lip, WaterfallSimState data,
        WaterfallData style, HashSet<Vector2I> present, float height, int segments)
    {
        var pour = new Vector3(lip.DirX, 0f, lip.DirZ);
        // The width axis is the horizontal perpendicular to the pour. Adjacent
        // lip columns facing the same way generate identical vertices along
        // their shared edge, so the strips meet exactly with no seam.
        var wide = new Vector3(-lip.DirZ, 0f, lip.DirX);

        // The lip's own outer face — the boundary between the pool and the drop,
        // which is where the water leaves the ground.
        Vector3 centre = new Vector3(lip.X + 0.5f, data.TopY, lip.Z + 0.5f) - pour * 0.5f;

        // Tuck in the sides that have no neighbouring strip.
        var left = new Vector2I(lip.X - (int)wide.X, lip.Z - (int)wide.Z);
        var right = new Vector2I(lip.X + (int)wide.X, lip.Z + (int)wide.Z);
        float inset = Mathf.Clamp(style.edgeInset, 0f, 0.45f);
        float halfLeft = present.Contains(left) ? 0.5f : 0.5f - inset;
        float halfRight = present.Contains(right) ? 0.5f : 0.5f - inset;

        Vector3 prevL = Vector3.Zero;
        Vector3 prevR = Vector3.Zero;
        for (int i = 0; i <= segments; i++)
        {
            float s = i / (float)segments;
            Vector3 point = centre + pour * Reach(s, style.pourReach) - new Vector3(0f, height * s, 0f);
            Vector3 normal = Normal(s, style.pourReach, height, pour, wide);
            Vector3 l = point - wide * halfLeft - data.WorldPosition;
            Vector3 r = point + wide * halfRight - data.WorldPosition;
            // V runs down the fall in metres so the streaks scroll in world
            // units; U runs across it, shared with the neighbouring strip.
            float v = height * s;
            // U is the world coordinate along the width axis itself, so two
            // strips side by side agree on it and the streak pattern crosses
            // the join without a seam.
            float uCentre = centre.X * wide.X + centre.Z * wide.Z;
            float uL = uCentre - halfLeft;
            float uR = uCentre + halfRight;

            if (i > 0)
            {
                st.SetNormal(normal); st.SetUV(new Vector2(uL, v - height / segments)); st.AddVertex(prevL);
                st.SetNormal(normal); st.SetUV(new Vector2(uL, v)); st.AddVertex(l);
                st.SetNormal(normal); st.SetUV(new Vector2(uR, v)); st.AddVertex(r);

                st.SetNormal(normal); st.SetUV(new Vector2(uL, v - height / segments)); st.AddVertex(prevL);
                st.SetNormal(normal); st.SetUV(new Vector2(uR, v)); st.AddVertex(r);
                st.SetNormal(normal); st.SetUV(new Vector2(uR, v - height / segments)); st.AddVertex(prevR);
            }
            prevL = l;
            prevR = r;
        }
    }

    // Horizontal distance travelled by the time the jet has fallen the fraction
    // `s` of its drop. Ballistic: the fall goes as t², the reach as t, so the
    // reach goes as the square root of the fall — nearly all of it spent in the
    // first stretch, which is the convex shoulder.
    private static float Reach(float s, float pourReach)
    {
        return pourReach * Mathf.Sqrt(Mathf.Clamp(s, 0f, 1f));
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
}
