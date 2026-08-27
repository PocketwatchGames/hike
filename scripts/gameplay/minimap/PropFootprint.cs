using Godot;
using System.Collections.Generic;

// XZ footprint of a prop's STATIC collision, in whole 1m world columns (one
// outdoor minimap pixel each).
//
// A column is covered when the collision contains the column's CENTRE — the
// point the pixel stands for — so a wide rock marks every column you cannot
// walk through instead of only the one its origin happens to sit in. The test
// is "does the world-vertical line through that centre hit the shape", run in
// each shape's local space, so a shape tilted off vertical is still exact.
//
// Main-thread only: the gather buffer is shared to keep chunk loads
// allocation-free.
public static class PropFootprint
{
    private enum EKind
    {
        Box,
        Sphere,
        Cylinder,
        Capsule,
        Mesh,
    }

    private struct Collider
    {
        public EKind Kind;
        // World -> shape local. The column test runs in local space.
        public Transform3D Inverse;
        public Vector3 Center;     // Box only (a fallback shape's own bounds are off-centre).
        public Vector3 Half;       // Box half extents.
        public float Radius;       // Sphere / Cylinder / Capsule.
        public float HalfHeight;   // Cylinder half height; Capsule cap-centre offset.
        public Vector3[] Faces;    // Mesh: triangle soup, 3 verts per triangle.
        public Aabb WorldBounds;   // Quick reject before the exact test.
    }

    // Shapes sit parallel to the world axes far more often than not, so the
    // line/shape tests treat a direction component below this as zero.
    private const float PARALLEL_EPSILON = 1e-6f;

    private static readonly List<Collider> _colliders = new();

    // Fills `columns` with every 1m world column (voxel XZ coords) covered by
    // `entity`'s static collision, and returns whether it has any at all — the
    // two differ for a prop too thin to reach a column centre (a 0.3m tree
    // trunk usually covers none), which the caller still wants on the map.
    public static bool Collect(Node3D entity, List<Vector2I> columns)
    {
        columns.Clear();
        _colliders.Clear();
        Gather(entity, false, _colliders);
        if (_colliders.Count == 0)
        {
            return false;
        }

        Aabb bounds = _colliders[0].WorldBounds;
        for (int i = 1; i < _colliders.Count; i++)
        {
            bounds = bounds.Merge(_colliders[i].WorldBounds);
        }

        int minX = Mathf.FloorToInt(bounds.Position.X);
        int maxX = Mathf.FloorToInt(bounds.Position.X + bounds.Size.X);
        int minZ = Mathf.FloorToInt(bounds.Position.Z);
        int maxZ = Mathf.FloorToInt(bounds.Position.Z + bounds.Size.Z);
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (CoversColumn(x + 0.5f, z + 0.5f))
                {
                    columns.Add(new Vector2I(x, z));
                }
            }
        }
        return true;
    }

    private static bool CoversColumn(float wx, float wz)
    {
        for (int i = 0; i < _colliders.Count; i++)
        {
            Collider c = _colliders[i];
            if (wx < c.WorldBounds.Position.X || wx > c.WorldBounds.Position.X + c.WorldBounds.Size.X
                || wz < c.WorldBounds.Position.Z || wz > c.WorldBounds.Position.Z + c.WorldBounds.Size.Z)
            {
                continue;
            }
            // The world-vertical line through the column centre, in local space.
            Vector3 p = c.Inverse * new Vector3(wx, 0f, wz);
            Vector3 d = c.Inverse.Basis * Vector3.Up;
            bool hit = c.Kind switch
            {
                EKind.Box => LineHitsBox(p - c.Center, d, c.Half),
                EKind.Sphere => LineHitsSphere(p, d, Vector3.Zero, c.Radius),
                EKind.Cylinder => LineHitsCylinder(p, d, c.Radius, c.HalfHeight),
                EKind.Capsule => LineHitsCylinder(p, d, c.Radius, c.HalfHeight)
                    || LineHitsSphere(p, d, new Vector3(0f, c.HalfHeight, 0f), c.Radius)
                    || LineHitsSphere(p, d, new Vector3(0f, -c.HalfHeight, 0f), c.Radius),
                _ => LineHitsMesh(p, d, c.Faces),
            };
            if (hit)
            {
                return true;
            }
        }
        return false;
    }

    // Static collision only: an Area3D is a trigger volume and blocks nothing,
    // so a berry bush's pickup radius must not read as an obstacle.
    private static void Gather(Node node, bool insideStaticBody, List<Collider> outColliders)
    {
        if (node is CollisionObject3D)
        {
            insideStaticBody = node is StaticBody3D;
        }
        if (insideStaticBody && node is CollisionShape3D cs && !cs.Disabled && cs.Shape != null)
        {
            if (TryBuild(cs.Shape, cs.GlobalTransform, out Collider collider))
            {
                outColliders.Add(collider);
            }
        }
        foreach (Node child in node.GetChildren())
        {
            Gather(child, insideStaticBody, outColliders);
        }
    }

    private static bool TryBuild(Shape3D shape, Transform3D xform, out Collider collider)
    {
        collider = new Collider { Inverse = xform.AffineInverse() };
        Aabb local;
        switch (shape)
        {
            case BoxShape3D box:
                collider.Kind = EKind.Box;
                collider.Half = box.Size * 0.5f;
                local = new Aabb(-collider.Half, box.Size);
                break;
            case SphereShape3D sphere:
                collider.Kind = EKind.Sphere;
                collider.Radius = sphere.Radius;
                local = new Aabb(
                    new Vector3(-sphere.Radius, -sphere.Radius, -sphere.Radius),
                    new Vector3(sphere.Radius, sphere.Radius, sphere.Radius) * 2f);
                break;
            case CylinderShape3D cylinder:
                collider.Kind = EKind.Cylinder;
                collider.Radius = cylinder.Radius;
                collider.HalfHeight = cylinder.Height * 0.5f;
                local = new Aabb(
                    new Vector3(-cylinder.Radius, -collider.HalfHeight, -cylinder.Radius),
                    new Vector3(cylinder.Radius * 2f, cylinder.Height, cylinder.Radius * 2f));
                break;
            case CapsuleShape3D capsule:
                collider.Kind = EKind.Capsule;
                collider.Radius = capsule.Radius;
                // Godot's capsule height includes both caps, so the cylindrical
                // mid-section ends where the cap centres are.
                collider.HalfHeight = Mathf.Max(0f, capsule.Height * 0.5f - capsule.Radius);
                local = new Aabb(
                    new Vector3(-capsule.Radius, -capsule.Height * 0.5f, -capsule.Radius),
                    new Vector3(capsule.Radius * 2f, capsule.Height, capsule.Radius * 2f));
                break;
            case ConcavePolygonShape3D mesh:
                collider.Kind = EKind.Mesh;
                // Marshals the whole triangle soup, so it is read once here and
                // never inside the per-column test.
                collider.Faces = mesh.Data;
                if (collider.Faces == null || collider.Faces.Length < 3)
                {
                    return false;
                }
                local = new Aabb(collider.Faces[0], Vector3.Zero);
                for (int i = 1; i < collider.Faces.Length; i++)
                {
                    local = local.Expand(collider.Faces[i]);
                }
                break;
            default:
                // Anything else (convex hulls, heightmaps) falls back to its own
                // bounds — coarse, but the alternative is not marking it at all.
                Mesh debug = shape.GetDebugMesh();
                if (debug == null)
                {
                    return false;
                }
                local = debug.GetAabb();
                collider.Kind = EKind.Box;
                collider.Half = local.Size * 0.5f;
                collider.Center = local.GetCenter();
                break;
        }
        collider.WorldBounds = xform * local;
        return true;
    }

    // Infinite line vs an origin-centred box: the slab test with no bound on t.
    private static bool LineHitsBox(Vector3 p, Vector3 d, Vector3 half)
    {
        float tMin = float.NegativeInfinity;
        float tMax = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float dv = d[axis];
            float pv = p[axis];
            float h = half[axis];
            if (Mathf.Abs(dv) < PARALLEL_EPSILON)
            {
                if (Mathf.Abs(pv) > h)
                {
                    return false;
                }
                continue;
            }
            float t1 = (-h - pv) / dv;
            float t2 = (h - pv) / dv;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }
            tMin = Mathf.Max(tMin, t1);
            tMax = Mathf.Min(tMax, t2);
            if (tMin > tMax)
            {
                return false;
            }
        }
        return true;
    }

    private static bool LineHitsSphere(Vector3 p, Vector3 d, Vector3 center, float radius)
    {
        Vector3 rel = p - center;
        float dd = d.LengthSquared();
        if (dd < PARALLEL_EPSILON)
        {
            return rel.LengthSquared() <= radius * radius;
        }
        Vector3 perp = rel - d * (rel.Dot(d) / dd);
        return perp.LengthSquared() <= radius * radius;
    }

    // Infinite line vs a Y-axis cylinder: solve the radius in XZ, then check the
    // resulting t range still lies within the cylinder's height.
    private static bool LineHitsCylinder(Vector3 p, Vector3 d, float radius, float halfHeight)
    {
        float a = d.X * d.X + d.Z * d.Z;
        float c = p.X * p.X + p.Z * p.Z - radius * radius;
        if (a < PARALLEL_EPSILON)
        {
            // Line runs along the axis: inside the radius means it passes
            // through the whole height (or, if fully degenerate, at p.Y).
            return c <= 0f && (Mathf.Abs(d.Y) >= PARALLEL_EPSILON || Mathf.Abs(p.Y) <= halfHeight);
        }
        float b = 2f * (p.X * d.X + p.Z * d.Z);
        float disc = b * b - 4f * a * c;
        if (disc < 0f)
        {
            return false;
        }
        if (Mathf.Abs(d.Y) < PARALLEL_EPSILON)
        {
            // Horizontal line: it crosses the radius somewhere, all at one height.
            return Mathf.Abs(p.Y) <= halfHeight;
        }
        float sqrt = Mathf.Sqrt(disc);
        float t1 = (-b - sqrt) / (2f * a);
        float t2 = (-b + sqrt) / (2f * a);
        float y1 = p.Y + t1 * d.Y;
        float y2 = p.Y + t2 * d.Y;
        return Mathf.Max(y1, y2) >= -halfHeight && Mathf.Min(y1, y2) <= halfHeight;
    }

    // Möller-Trumbore with no bound on t — any triangle the line passes through
    // means the column is inside the mesh's silhouette.
    private static bool LineHitsMesh(Vector3 p, Vector3 d, Vector3[] faces)
    {
        if (faces == null)
        {
            return false;
        }
        for (int i = 0; i + 2 < faces.Length; i += 3)
        {
            Vector3 v0 = faces[i];
            Vector3 edge1 = faces[i + 1] - v0;
            Vector3 edge2 = faces[i + 2] - v0;
            Vector3 pv = d.Cross(edge2);
            float det = edge1.Dot(pv);
            if (Mathf.Abs(det) < PARALLEL_EPSILON)
            {
                continue;
            }
            float invDet = 1f / det;
            Vector3 tv = p - v0;
            float u = tv.Dot(pv) * invDet;
            if (u < 0f || u > 1f)
            {
                continue;
            }
            Vector3 qv = tv.Cross(edge1);
            float v = d.Dot(qv) * invDet;
            if (v < 0f || u + v > 1f)
            {
                continue;
            }
            return true;
        }
        return false;
    }
}
