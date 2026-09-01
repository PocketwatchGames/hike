using System;
using Godot;

// Draws the RECEIVING half of combat — every HurtBox near the player — as a
// wireframe of the volume an attack has to overlap. Shares the `debug_melee`
// cvar with the swing volume drawn in ItemEventHandlers, because the question
// that needs both is one question: a mob's swing looks like it connects and
// doesn't, and the culprit is either the reach of the fan or the size of the
// box. Seeing one without the other answers half of it.
//
// Found by a sphere query rather than by walking Sim's entity dictionaries, so
// it covers every hurtbox that exists — props and environmental damageables
// included, which no entity list holds.
public static class HurtBoxDebug
{
    // Half-window drawn around the player, in metres. Comfortably past any
    // authored swing reach, so a mob standing back from its own attack is still
    // drawn when that swing whiffs.
    private const float RadiusMeters = 24f;

    // Enough for a crowded camp. The cap drops the boxes the query returns
    // last, which are the far ones.
    private const int MaxBoxes = 64;

    // Cool against the swing volume's warm orange, so attacker and receiver
    // never read as the same thing.
    private static readonly Color LiveColor = new(0.25f, 1f, 0.7f, 0.6f);

    // A corpse's or a burrowed mob's box. On its own collision layer and so
    // invisible to an ordinary weapon — drawn dim and grey precisely so it
    // cannot be mistaken for a target the swing should have hit.
    private static readonly Color InactiveColor = new(0.45f, 0.5f, 0.55f, 0.35f);

    // Marks HurtBox.Center — the point that stands for the box in spatial
    // queries (what chain lightning hops to, where an impact resolves). NOT the
    // node origin, which sits at the owner's feet.
    private const float CenterMarkSize = 0.25f;

    // Same budget as the swing's rings (ItemEventHandlers.SweepRingPoints), so
    // the attacker's wireframe and the receiver's read as one overlay.
    private const int RingSegments = 12;
    private const int ArcSegments = 6;

    public static void Draw(Node3D worldOwner, Vector3 center)
    {
        World3D world = worldOwner?.GetWorld3D();
        if (world == null)
        {
            return;
        }
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new SphereShape3D { Radius = RadiusMeters },
            Transform = new Transform3D(Basis.Identity, center),
            CollisionMask = (uint)(ECollisionLayer.HurtBox | ECollisionLayer.BurrowedHurtBox | ECollisionLayer.DeadHurtBox),
            CollideWithAreas = true,
            CollideWithBodies = false,
        };
        var results = world.DirectSpaceState.IntersectShape(query, MaxBoxes);
        foreach (var result in results)
        {
            if (result["collider"].Obj is not HurtBox hurtBox)
            {
                continue;
            }
            bool live = (hurtBox.CollisionLayer & (uint)ECollisionLayer.HurtBox) != 0;
            Color color = live ? LiveColor : InactiveColor;
            DrawShape(hurtBox.Shape, color);
            DebugDraw.Cross(hurtBox.Center, CenterMarkSize, color);
        }
    }

    // The shape as a readable wireframe, in world space.
    //
    // Deliberately NOT Shape3D.GetDebugMesh, which was the first cut: exact and
    // type-agnostic, but it spends ~570 segments on one capsule. Ten of those
    // on screen is a solid green blob costing real frame time, and where the
    // box's edge sits relative to the swing — the one thing this exists to
    // show — is exactly what a blob hides.
    private static void DrawShape(CollisionShape3D node, Color color)
    {
        Shape3D shape = node?.Shape;
        if (shape == null)
        {
            return;
        }
        Transform3D xf = node.GlobalTransform;
        switch (shape)
        {
            case CapsuleShape3D capsule:
            {
                // Godot's capsule height spans the whole shape, caps included.
                float capY = Mathf.Max(0f, capsule.Height * 0.5f - capsule.Radius);
                Vector3 top = Vector3.Up * capY;
                Vector3 bottom = Vector3.Down * capY;
                DrawRing(xf, top, Vector3.Right, Vector3.Back, capsule.Radius, color);
                DrawRing(xf, bottom, Vector3.Right, Vector3.Back, capsule.Radius, color);
                DrawSideLines(xf, top, bottom, capsule.Radius, color);
                // Rounded ends as half-circles, so a cap doesn't draw a full
                // circle back down through the body.
                DrawArc(xf, top, Vector3.Right, Vector3.Up, capsule.Radius, color);
                DrawArc(xf, top, Vector3.Back, Vector3.Up, capsule.Radius, color);
                DrawArc(xf, bottom, Vector3.Right, Vector3.Down, capsule.Radius, color);
                DrawArc(xf, bottom, Vector3.Back, Vector3.Down, capsule.Radius, color);
                break;
            }
            case SphereShape3D sphere:
            {
                DrawRing(xf, Vector3.Zero, Vector3.Right, Vector3.Back, sphere.Radius, color);
                DrawRing(xf, Vector3.Zero, Vector3.Right, Vector3.Up, sphere.Radius, color);
                DrawRing(xf, Vector3.Zero, Vector3.Back, Vector3.Up, sphere.Radius, color);
                break;
            }
            case CylinderShape3D cylinder:
            {
                Vector3 top = Vector3.Up * (cylinder.Height * 0.5f);
                DrawRing(xf, top, Vector3.Right, Vector3.Back, cylinder.Radius, color);
                DrawRing(xf, -top, Vector3.Right, Vector3.Back, cylinder.Radius, color);
                DrawSideLines(xf, top, -top, cylinder.Radius, color);
                break;
            }
            case BoxShape3D box:
            {
                DrawBox(xf, box.Size * 0.5f, color);
                break;
            }
            default:
            {
                // A shape a hurtbox has not been authored with yet (a convex
                // hull, say). Exactness beats density here: this is the one
                // case where guessing the silhouette is worse than a blob.
                DrawDebugMesh(shape, xf, color);
                break;
            }
        }
    }

    // Corner indices are a sign pattern: bit 0 = x, bit 1 = y, bit 2 = z.
    // Four edges along x, four along y, four along z.
    private static readonly int[] BoxEdges =
    {
        0, 1, 2, 3, 4, 5, 6, 7,
        0, 2, 1, 3, 4, 6, 5, 7,
        0, 4, 1, 5, 2, 6, 3, 7,
    };

    private static void DrawBox(Transform3D xf, Vector3 extents, Color color)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = xf * new Vector3(
                (i & 1) == 0 ? -extents.X : extents.X,
                (i & 2) == 0 ? -extents.Y : extents.Y,
                (i & 4) == 0 ? -extents.Z : extents.Z);
        }
        for (int e = 0; e < BoxEdges.Length; e += 2)
        {
            DebugDraw.Line(corners[BoxEdges[e]], corners[BoxEdges[e + 1]], color);
        }
    }

    private static void DrawRing(Transform3D xf, Vector3 center, Vector3 u, Vector3 v, float radius, Color color)
    {
        Vector3 prev = xf * (center + u * radius);
        for (int i = 1; i <= RingSegments; i++)
        {
            float a = Mathf.Tau * i / RingSegments;
            Vector3 p = xf * (center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radius);
            DebugDraw.Line(prev, p, color);
            prev = p;
        }
    }

    // Half-circle from +u round through +v to -u — one hemisphere seam.
    private static void DrawArc(Transform3D xf, Vector3 center, Vector3 u, Vector3 v, float radius, Color color)
    {
        Vector3 prev = xf * (center + u * radius);
        for (int i = 1; i <= ArcSegments; i++)
        {
            float a = Mathf.Pi * i / ArcSegments;
            Vector3 p = xf * (center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radius);
            DebugDraw.Line(prev, p, color);
            prev = p;
        }
    }

    // The four verticals joining two coaxial rings, on the cardinal axes.
    private static void DrawSideLines(Transform3D xf, Vector3 top, Vector3 bottom, float radius, Color color)
    {
        for (int i = 0; i < 4; i++)
        {
            float a = Mathf.Tau * i / 4f;
            Vector3 offset = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
            DebugDraw.Line(xf * (top + offset), xf * (bottom + offset), color);
        }
    }

    private static void DrawDebugMesh(Shape3D shape, Transform3D xf, Color color)
    {
        ArrayMesh mesh = shape.GetDebugMesh();
        if (mesh == null)
        {
            return;
        }
        int surfaces = mesh.GetSurfaceCount();
        for (int s = 0; s < surfaces; s++)
        {
            // A line list is the only surface shape this can read. A solid fill
            // surface (Godot draws one for its own collision debug) comes
            // through as triangles and must be skipped, not read as segments.
            if (mesh.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Lines)
            {
                continue;
            }
            Vector3[] verts = mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            for (int i = 0; i + 1 < verts.Length; i += 2)
            {
                DebugDraw.Line(xf * verts[i], xf * verts[i + 1], color);
            }
        }
    }
}
