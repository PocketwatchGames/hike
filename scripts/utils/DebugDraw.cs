using Godot;

// Static immediate-mode debug-line API. Forwards into a single
// DebugDrawRenderer instance hung off World.Current. Every call queues
// line segments that get rendered at the next _Process tick; pass a
// non-zero `lifetime` to keep a shape on screen across frames (useful
// for one-shot events like a hit registration that you don't want to
// re-emit every frame to keep visible).
//
// API surface:
//   Line      — a single segment.
//   Lines     — sequence of (a→b, b→c, c→d, …) along a polyline.
//   Box       — 12 edges of an axis-aligned box.
//   Sphere    — 3 great circles (XY, XZ, YZ planes).
//   Cross     — three orthogonal segments centered at point.
//   Arrow     — line plus a small arrowhead at the tip.
//
// All are no-ops if no World is running (e.g. on the main menu) or
// before the renderer has spawned.
public static class DebugDraw
{
    private const int SphereSegments = 16;
    private const float ArrowHeadFraction = 0.15f;

    public static void Line(Vector3 a, Vector3 b, Color color, float lifetime = 0f)
    {
        DebugDrawRenderer r = DebugDrawRenderer.EnsureInstance();
        if (r == null)
        {
            return;
        }
        r.Enqueue(a, b, color, lifetime);
    }

    // Polyline: connects points[0]→points[1]→...→points[n-1] as a
    // continuous string of segments. Skips no-ops on empty / 1-element
    // inputs so callers don't need to gate it.
    public static void Lines(System.Collections.Generic.IList<Vector3> points, Color color, float lifetime = 0f)
    {
        if (points == null || points.Count < 2)
        {
            return;
        }
        DebugDrawRenderer r = DebugDrawRenderer.EnsureInstance();
        if (r == null)
        {
            return;
        }
        for (int i = 0; i < points.Count - 1; i++)
        {
            r.Enqueue(points[i], points[i + 1], color, lifetime);
        }
    }

    public static void Box(Vector3 min, Vector3 max, Color color, float lifetime = 0f)
    {
        DebugDrawRenderer r = DebugDrawRenderer.EnsureInstance();
        if (r == null)
        {
            return;
        }
        // 8 corners
        Vector3 c000 = new(min.X, min.Y, min.Z);
        Vector3 c100 = new(max.X, min.Y, min.Z);
        Vector3 c010 = new(min.X, max.Y, min.Z);
        Vector3 c110 = new(max.X, max.Y, min.Z);
        Vector3 c001 = new(min.X, min.Y, max.Z);
        Vector3 c101 = new(max.X, min.Y, max.Z);
        Vector3 c011 = new(min.X, max.Y, max.Z);
        Vector3 c111 = new(max.X, max.Y, max.Z);
        // 12 edges
        r.Enqueue(c000, c100, color, lifetime);
        r.Enqueue(c100, c110, color, lifetime);
        r.Enqueue(c110, c010, color, lifetime);
        r.Enqueue(c010, c000, color, lifetime);
        r.Enqueue(c001, c101, color, lifetime);
        r.Enqueue(c101, c111, color, lifetime);
        r.Enqueue(c111, c011, color, lifetime);
        r.Enqueue(c011, c001, color, lifetime);
        r.Enqueue(c000, c001, color, lifetime);
        r.Enqueue(c100, c101, color, lifetime);
        r.Enqueue(c110, c111, color, lifetime);
        r.Enqueue(c010, c011, color, lifetime);
    }

    public static void BoxCentered(Vector3 center, Vector3 size, Color color, float lifetime = 0f)
    {
        Vector3 half = size * 0.5f;
        Box(center - half, center + half, color, lifetime);
    }

    // Three great circles (XY, XZ, YZ) — gives a recognisable wireframe
    // sphere shape with 3 * SphereSegments segments. Cheap enough to
    // render hundreds of these per frame.
    public static void Sphere(Vector3 center, float radius, Color color, float lifetime = 0f)
    {
        DebugDrawRenderer r = DebugDrawRenderer.EnsureInstance();
        if (r == null || radius <= 0f)
        {
            return;
        }
        float step = Mathf.Tau / SphereSegments;
        for (int i = 0; i < SphereSegments; i++)
        {
            float a0 = i * step;
            float a1 = (i + 1) * step;
            float c0 = Mathf.Cos(a0) * radius;
            float s0 = Mathf.Sin(a0) * radius;
            float c1 = Mathf.Cos(a1) * radius;
            float s1 = Mathf.Sin(a1) * radius;
            // XY plane (Z = 0)
            r.Enqueue(center + new Vector3(c0, s0, 0), center + new Vector3(c1, s1, 0), color, lifetime);
            // XZ plane (Y = 0)
            r.Enqueue(center + new Vector3(c0, 0, s0), center + new Vector3(c1, 0, s1), color, lifetime);
            // YZ plane (X = 0)
            r.Enqueue(center + new Vector3(0, c0, s0), center + new Vector3(0, c1, s1), color, lifetime);
        }
    }

    public static void Cross(Vector3 center, float size, Color color, float lifetime = 0f)
    {
        DebugDrawRenderer r = DebugDrawRenderer.EnsureInstance();
        if (r == null)
        {
            return;
        }
        float h = size * 0.5f;
        r.Enqueue(center + new Vector3(-h, 0, 0), center + new Vector3(h, 0, 0), color, lifetime);
        r.Enqueue(center + new Vector3(0, -h, 0), center + new Vector3(0, h, 0), color, lifetime);
        r.Enqueue(center + new Vector3(0, 0, -h), center + new Vector3(0, 0, h), color, lifetime);
    }

    // Line from `from` to `to` with a small two-line arrowhead at the
    // tip. Useful for visualising directions / forces.
    public static void Arrow(Vector3 from, Vector3 to, Color color, float lifetime = 0f)
    {
        DebugDrawRenderer r = DebugDrawRenderer.EnsureInstance();
        if (r == null)
        {
            return;
        }
        r.Enqueue(from, to, color, lifetime);

        Vector3 dir = to - from;
        float length = dir.Length();
        if (length < 0.001f)
        {
            return;
        }
        dir /= length;
        // Pick a stable orthogonal: cross with world up, or world right
        // if dir is too parallel to up.
        Vector3 ortho = dir.Cross(Vector3.Up);
        if (ortho.LengthSquared() < 0.001f)
        {
            ortho = dir.Cross(Vector3.Right);
        }
        ortho = ortho.Normalized();

        float head = length * ArrowHeadFraction;
        Vector3 baseTip = to - dir * head;
        r.Enqueue(to, baseTip + ortho * head * 0.5f, color, lifetime);
        r.Enqueue(to, baseTip - ortho * head * 0.5f, color, lifetime);
    }
}
