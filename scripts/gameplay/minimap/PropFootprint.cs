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
// Two ways in, because the two callers ask at different times:
//   Collect  — a prop that is ALREADY PLACED. Gathers and rasterizes in one
//              go against a shared buffer, so a chunk load allocates nothing.
//              Main-thread only, for that reason.
//   Measure  — a prop SCENE, once, into a reusable PropShape that can then be
//              rasterized at any pose. This is what lets the world-map fill
//              jitter a prop off its column centre without re-instantiating the
//              scene per offset, which is otherwise the whole cost.
public static class PropFootprint
{
    internal enum EKind
    {
        Box,
        Sphere,
        Cylinder,
        Capsule,
        Mesh,
    }

    internal struct Collider
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
        // The same box in the space the collider was GATHERED in, so a shape
        // measured once can have its world bounds re-derived under any pose.
        public Aabb GatherBounds;
    }

    // Shapes sit parallel to the world axes far more often than not, so the
    // line/shape tests treat a direction component below this as zero.
    private const float PARALLEL_EPSILON = 1e-6f;

    private static readonly List<Collider> _colliders = new();

    // One prop scene's static collision, measured once in the scene's OWN space
    // and rasterizable at any pose afterwards. The world-map fill holds one per
    // scene: instantiating a tree is the expensive half of a footprint, and the
    // fill asks for the same tree at eight yaws and a spread of sub-metre
    // offsets. Immutable, so the painter's main thread and the bake's worker
    // may read one at the same time.
    public sealed class Shape
    {
        private readonly Collider[] _shapes;

        // Horizontal reach of what is DRAWN, not of what blocks — a canopy
        // overhangs its trunk many times over, and how much room a prop needs
        // in order to look unplanted is a fact about the canopy.
        public readonly float VisualRadius;

        // The collision's own horizontal reach from the origin, which is what
        // a barrier is made of.
        public readonly float CollisionRadius;

        // How TALL the drawn prop is. What separates a tree from a bush: a
        // collider's width says nothing useful (a pine's trunk collider is as
        // wide as its canopy, a willow's is a tenth of it), but nothing low is
        // a tree and nothing tall is undergrowth.
        public readonly float VisualHeight;

        public bool Blocks => _shapes.Length > 0;

        internal Shape(Collider[] shapes, float visualRadius, float collisionRadius,
            float visualHeight)
        {
            _shapes = shapes;
            VisualRadius = visualRadius;
            CollisionRadius = collisionRadius;
            VisualHeight = visualHeight;
        }

        // The columns this prop covers standing at `pose`. Nothing is cached per
        // pose: the query POINT is carried back into the measured space instead,
        // so an arbitrary offset costs the same as the centred one and no pose
        // allocates.
        public void Rasterize(Transform3D pose, List<Vector2I> columns)
        {
            columns.Clear();
            if (_shapes.Length == 0)
            {
                return;
            }
            Aabb bounds = pose * _shapes[0].GatherBounds;
            for (int i = 1; i < _shapes.Length; i++)
            {
                bounds = bounds.Merge(pose * _shapes[i].GatherBounds);
            }
            Transform3D inverse = pose.AffineInverse();
            int minX = Mathf.FloorToInt(bounds.Position.X);
            int maxX = Mathf.FloorToInt(bounds.Position.X + bounds.Size.X);
            int minZ = Mathf.FloorToInt(bounds.Position.Z);
            int maxZ = Mathf.FloorToInt(bounds.Position.Z + bounds.Size.Z);
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (Covers(inverse, x + 0.5f, z + 0.5f))
                    {
                        columns.Add(new Vector2I(x, z));
                    }
                }
            }
        }

        private bool Covers(in Transform3D inverse, float wx, float wz)
        {
            // The world-vertical line through the column centre, carried into
            // the space the shapes were measured in.
            Vector3 origin = inverse * new Vector3(wx, 0f, wz);
            Vector3 direction = inverse.Basis * Vector3.Up;
            for (int i = 0; i < _shapes.Length; i++)
            {
                if (HitsCollider(_shapes[i], origin, direction))
                {
                    return true;
                }
            }
            return false;
        }
    }

    // A shape for a scene that could not be loaded, so a caller's cache can
    // hold the answer rather than re-trying the load at every query.
    public static Shape EmptyShape() => new(System.Array.Empty<Collider>(), 0f, 0f, 0f);

    // Measure a prop scene, which must be instantiated but need not be in the
    // tree. The result is in the SCENE's own space — its root seated at the
    // origin, unrotated — which is what makes it reusable at any pose.
    public static Shape Measure(Node3D root)
    {
        var shapes = new List<Collider>();
        Gather(root, Transform3D.Identity, false, shapes);
        float collisionRadius = 0f;
        foreach (Collider collider in shapes)
        {
            collisionRadius = Mathf.Max(collisionRadius, HorizontalReach(collider.GatherBounds));
        }
        return new Shape(shapes.ToArray(),
            Mathf.Max(collisionRadius, VisualReach(root, Transform3D.Identity)), collisionRadius,
            VisualTop(root, Transform3D.Identity));
    }

    // How far the DRAWN prop reaches from its origin, horizontally.
    //
    // Foliage is read off the authored FoliageCluster nodes rather than off the
    // MultiMesh they bake into, because the bake is rebuilt at runtime and the
    // copy stored in the .tscn is only the editor's last preview — a bush whose
    // clusters were widened measures at its old size until someone opens it.
    // The authored radii are the honest answer either way.
    private static float VisualReach(Node node, Transform3D xform)
    {
        if (node is Node3D spatial)
        {
            xform *= spatial.Transform;
        }
        float reach = 0f;
        if (node is FoliageCluster cluster)
        {
            Vector3 origin = xform.Origin;
            float spread = Mathf.Max(cluster.ellipsoidRadii.X, cluster.ellipsoidRadii.Z)
                + cluster.cardSizeMax * 0.5f;
            reach = Mathf.Sqrt(origin.X * origin.X + origin.Z * origin.Z) + spread;
        }
        else if (node is VisualInstance3D visual && node is not MultiMeshInstance3D)
        {
            reach = HorizontalReach(xform * visual.GetAabb());
        }
        foreach (Node child in node.GetChildren())
        {
            reach = Mathf.Max(reach, VisualReach(child, xform));
        }
        return reach;
    }

    // How high the DRAWN prop reaches. Read off the authored FoliageCluster
    // nodes for the same reason VisualReach is — the MultiMesh they bake into is
    // only the editor's last preview.
    private static float VisualTop(Node node, Transform3D xform)
    {
        if (node is Node3D spatial)
        {
            xform *= spatial.Transform;
        }
        float top = 0f;
        if (node is FoliageCluster cluster)
        {
            top = xform.Origin.Y + cluster.ellipsoidRadii.Y + cluster.cardSizeMax * 0.5f;
        }
        else if (node is VisualInstance3D visual && node is not MultiMeshInstance3D)
        {
            Aabb box = xform * visual.GetAabb();
            top = box.Position.Y + box.Size.Y;
        }
        foreach (Node child in node.GetChildren())
        {
            top = Mathf.Max(top, VisualTop(child, xform));
        }
        return top;
    }

    // The furthest any corner of a box lies from the vertical axis through the
    // origin — a prop modelled off-centre reaches further than its half-extent.
    private static float HorizontalReach(in Aabb box)
    {
        float x = Mathf.Max(Mathf.Abs(box.Position.X), Mathf.Abs(box.Position.X + box.Size.X));
        float z = Mathf.Max(Mathf.Abs(box.Position.Z), Mathf.Abs(box.Position.Z + box.Size.Z));
        return Mathf.Sqrt(x * x + z * z);
    }

    // Fills `columns` with every 1m world column (voxel XZ coords) covered by
    // `entity`'s static collision, and returns whether it has any at all — the
    // two differ for a prop too thin to reach a column centre (a 0.3m tree
    // trunk usually covers none), which the caller still wants on the map.
    public static bool Collect(Node3D entity, List<Vector2I> columns)
        => Collect(entity, entity.GlobalTransform, columns);

    // The same, for an entity that is NOT in the tree and therefore has no
    // global transform to read — a scene instantiated purely to measure what it
    // would cover if it were placed at `worldXform` (the world-map painter's
    // prop fill). The transform is threaded down the hierarchy rather than
    // re-read per node, which is what makes the two callers one implementation:
    // in the tree the product IS the global transform.
    public static bool Collect(Node3D entity, Transform3D worldXform, List<Vector2I> columns)
    {
        columns.Clear();
        _colliders.Clear();
        Gather(entity, worldXform, false, _colliders);
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
            if (HitsCollider(c, new Vector3(wx, 0f, wz), Vector3.Up))
            {
                return true;
            }
        }
        return false;
    }

    // Does the line through `origin` along `direction` — both in the space the
    // collider was gathered in — pass through the shape?
    private static bool HitsCollider(in Collider c, Vector3 origin, Vector3 direction)
    {
        Vector3 p = c.Inverse * origin;
        Vector3 d = c.Inverse.Basis * direction;
        return c.Kind switch
        {
            EKind.Box => LineHitsBox(p - c.Center, d, c.Half),
            EKind.Sphere => LineHitsSphere(p, d, Vector3.Zero, c.Radius),
            EKind.Cylinder => LineHitsCylinder(p, d, c.Radius, c.HalfHeight),
            EKind.Capsule => LineHitsCylinder(p, d, c.Radius, c.HalfHeight)
                || LineHitsSphere(p, d, new Vector3(0f, c.HalfHeight, 0f), c.Radius)
                || LineHitsSphere(p, d, new Vector3(0f, -c.HalfHeight, 0f), c.Radius),
            _ => LineHitsMesh(p, d, c.Faces),
        };
    }

    // Static collision only: an Area3D is a trigger volume and blocks nothing,
    // so a berry bush's pickup radius must not read as an obstacle.
    //
    // `xform` is the accumulated transform of everything above `node`, INCLUDING
    // node's own — never Node3D.GlobalTransform, which is only maintained for a
    // node inside the tree and silently reads as the local one outside it. That
    // measured every prop at its own origin rather than where it stands.
    private static void Gather(Node node, Transform3D xform, bool insideStaticBody, List<Collider> outColliders)
    {
        if (node is Node3D spatial)
        {
            xform *= spatial.Transform;
        }
        if (node is CollisionObject3D)
        {
            insideStaticBody = node is StaticBody3D;
        }
        if (insideStaticBody && node is CollisionShape3D cs && !cs.Disabled && cs.Shape != null)
        {
            if (TryBuild(cs.Shape, xform, out Collider collider))
            {
                outColliders.Add(collider);
            }
        }
        foreach (Node child in node.GetChildren())
        {
            Gather(child, xform, insideStaticBody, outColliders);
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
        collider.GatherBounds = collider.WorldBounds;
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
