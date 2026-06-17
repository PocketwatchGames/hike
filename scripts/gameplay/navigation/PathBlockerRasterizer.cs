using Godot;
using System.Collections.Generic;

// Walks an entity for every environment-layer CollisionShape3D and emits
// the voxel cells each one physically occupies at the entity's ground Y.
// World refcounts each emitted cell, so multiple colliders on the same
// entity (compound props) and overlapping entities (a chest tucked against
// a tree) compose correctly.
//
// Why this exists: a tree's CylinderShape3D has radius ~1.3m — its physical
// footprint is a 3×3 cell disc, not the single cell at the trunk's origin.
// Registering only one cell let A* route mobs through cells the cylinder
// still blocks, and they'd collide with the trunk in physics. Same problem
// applies to any non-trivial collider; this rasterizer is the single place
// shape-aware footprint resolution lives.
//
// Y semantics: each shape's world AABB Y range is checked against the mob
// walking volume at floorY (height MobWalkingHeight). Shapes whose vertical
// extent doesn't overlap that range contribute nothing — a sign hung high
// in the air, for instance, won't register a ground-level blocker.
public static class PathBlockerRasterizer
{
    // Vertical extent of a mob's standing column above floorY, used to gate
    // whether a shape's Y range can actually obstruct a mob walking past.
    // Matches the conservative end of MobData clearance heights.
    private const float MobWalkingHeight = 2f;

    public static void Rasterize(Node3D entity, int floorY, List<Vector3I> outCells)
    {
        if (entity == null)
        {
            return;
        }
        CollectFromNode(entity, floorY, outCells);
    }

    // Footprint of a flat disc centered at `center` with the given radius, at
    // the disc's floor row. Used by the hazard grid (a fire trap's danger
    // zone, a campfire, a spike field) where the avoided region is an authored
    // radius rather than a physical collider on the Solid layer.
    public static void RasterizeDisc(Vector3 center, float radius, List<Vector3I> outCells)
    {
        if (radius <= 0f)
        {
            return;
        }
        RasterizeDisc(center.X, center.Z, radius, Mathf.FloorToInt(center.Y), outCells);
    }

    private static void CollectFromNode(Node node, int floorY, List<Vector3I> outCells)
    {
        // Only solid-world bodies count as path blockers — terrain/walls
        // (Environment) and porous props (Porous, e.g. tree trunks). Hurtboxes
        // (layer 32) and interactive areas (layer 4) sit on the same entity
        // but mustn't inflate the walkable grid. Porous props still block
        // ground movement, so they belong here even though smell/sight pass.
        if (node is CollisionObject3D body && (body.CollisionLayer & (uint)ECollisionLayer.Solid) != 0)
        {
            foreach (Node child in body.GetChildren())
            {
                if (child is CollisionShape3D cs && cs.Shape != null && !cs.Disabled)
                {
                    RasterizeShape(cs, floorY, outCells);
                }
            }
        }
        // Recurse so compound props with nested bodies still get visited.
        foreach (Node child in node.GetChildren())
        {
            CollectFromNode(child, floorY, outCells);
        }
    }

    private static void RasterizeShape(CollisionShape3D cs, int floorY, List<Vector3I> outCells)
    {
        Transform3D worldXform = cs.GlobalTransform;
        Vector3 origin = worldXform.Origin;
        Shape3D shape = cs.Shape;

        switch (shape)
        {
            case CylinderShape3D cyl:
            {
                if (!YRangeOverlaps(origin.Y, cyl.Height * 0.5f, floorY))
                {
                    return;
                }
                RasterizeDisc(origin.X, origin.Z, cyl.Radius, floorY, outCells);
                break;
            }
            case SphereShape3D sph:
            {
                if (!YRangeOverlaps(origin.Y, sph.Radius, floorY))
                {
                    return;
                }
                // Exact cross-section at floorY would be smaller than the
                // full sphere disc, but we err on the side of "conservative
                // overestimate" — the mob bumps the sphere from any side
                // its full radius covers, including the rim that pokes up
                // past floorY.
                RasterizeDisc(origin.X, origin.Z, sph.Radius, floorY, outCells);
                break;
            }
            case CapsuleShape3D cap:
            {
                // Capsule local axis is Y. Vertical capsule (the common case)
                // has its full XZ footprint everywhere along its length;
                // treat as a disc. A horizontal capsule would technically
                // sweep an elongated footprint, but no project asset uses
                // one — revisit if that changes.
                if (!YRangeOverlaps(origin.Y, cap.Height * 0.5f, floorY))
                {
                    return;
                }
                RasterizeDisc(origin.X, origin.Z, cap.Radius, floorY, outCells);
                break;
            }
            case BoxShape3D box:
            {
                RasterizeBox(worldXform, box.Size, floorY, outCells);
                break;
            }
            case ConcavePolygonShape3D concave:
            {
                // MeshAutoCollider bakes FBX props (rocks, the well, etc.) into
                // a trimesh via Mesh.CreateTrimeshShape() — this is the common
                // prop collider, NOT a rare case. Without this branch those
                // props register zero blocker cells and mobs path straight
                // through them.
                RasterizeTrimesh(worldXform, concave.Data, floorY, outCells);
                break;
            }
        }
        // Remaining shape types (ConvexPolygon, Heightmap, Separation, World3D)
        // are silently skipped — none are used as path blockers in this
        // codebase. Add a case if that changes.
    }

    private static bool YRangeOverlaps(float centerY, float halfHeight, int floorY)
    {
        float low = centerY - halfHeight;
        float high = centerY + halfHeight;
        return high >= floorY && low <= floorY + MobWalkingHeight;
    }

    private static void RasterizeDisc(float cx, float cz, float radius, int floorY, List<Vector3I> outCells)
    {
        float r2 = radius * radius;
        int minX = Mathf.FloorToInt(cx - radius);
        int maxX = Mathf.FloorToInt(cx + radius);
        int minZ = Mathf.FloorToInt(cz - radius);
        int maxZ = Mathf.FloorToInt(cz + radius);
        for (int wx = minX; wx <= maxX; wx++)
        {
            for (int wz = minZ; wz <= maxZ; wz++)
            {
                // Cell-center distance — a cell whose middle sits inside
                // the disc is a cell a mob would clip the collider in.
                float dx = (wx + 0.5f) - cx;
                float dz = (wz + 0.5f) - cz;
                if (dx * dx + dz * dz <= r2)
                {
                    outCells.Add(new Vector3I(wx, floorY, wz));
                }
            }
        }
    }

    private static void RasterizeBox(Transform3D worldXform, Vector3 size, int floorY, List<Vector3I> outCells)
    {
        Vector3 origin = worldXform.Origin;
        Vector3 halfExtent = size * 0.5f;
        Basis b = worldXform.Basis;

        // World-space AABB extents of the (possibly rotated) box, computed
        // as the sum of |basis col_i * halfExtent_i| per world axis — the
        // standard OBB-to-AABB projection. Used both for the Y gate and to
        // bound the XZ scan range below.
        float xExtent = Mathf.Abs(b.X.X) * halfExtent.X + Mathf.Abs(b.Y.X) * halfExtent.Y + Mathf.Abs(b.Z.X) * halfExtent.Z;
        float yExtent = Mathf.Abs(b.X.Y) * halfExtent.X + Mathf.Abs(b.Y.Y) * halfExtent.Y + Mathf.Abs(b.Z.Y) * halfExtent.Z;
        float zExtent = Mathf.Abs(b.X.Z) * halfExtent.X + Mathf.Abs(b.Y.Z) * halfExtent.Y + Mathf.Abs(b.Z.Z) * halfExtent.Z;
        if (!YRangeOverlaps(origin.Y, yExtent, floorY))
        {
            return;
        }

        int minX = Mathf.FloorToInt(origin.X - xExtent);
        int maxX = Mathf.FloorToInt(origin.X + xExtent);
        int minZ = Mathf.FloorToInt(origin.Z - zExtent);
        int maxZ = Mathf.FloorToInt(origin.Z + zExtent);

        // For rotated boxes the AABB overshoots the actual OBB; transform
        // each candidate cell center into local space and accept only the
        // cells that fall inside ±halfExtent. Tests at floorY + 0.5 so the
        // probe sits at the same height a mob's feet would.
        Transform3D inv = worldXform.AffineInverse();
        float probeY = floorY + 0.5f;
        for (int wx = minX; wx <= maxX; wx++)
        {
            for (int wz = minZ; wz <= maxZ; wz++)
            {
                Vector3 worldP = new(wx + 0.5f, probeY, wz + 0.5f);
                Vector3 localP = inv * worldP;
                if (Mathf.Abs(localP.X) <= halfExtent.X &&
                    Mathf.Abs(localP.Y) <= halfExtent.Y &&
                    Mathf.Abs(localP.Z) <= halfExtent.Z)
                {
                    outCells.Add(new Vector3I(wx, floorY, wz));
                }
            }
        }
    }

    // Trimesh footprint: a triangle soup with no primitive silhouette, so we
    // project every triangle onto XZ and union the cells they cover. For a
    // closed mesh the top/bottom cap faces project over the interior, so the
    // union fills the solid silhouette rather than tracing a hollow outline.
    // Gated once on the whole shape's world-space Y AABB (does the prop occupy
    // the mob's standing band at all); like every other shape here it then
    // registers its footprint at the single floorY row. Runs at spawn, not
    // per frame, so the per-triangle cost is fine.
    private static void RasterizeTrimesh(Transform3D worldXform, Vector3[] verts, int floorY, List<Vector3I> outCells)
    {
        if (verts == null || verts.Length < 3)
        {
            return;
        }

        float minY = float.MaxValue;
        float maxY = float.MinValue;
        for (int i = 0; i < verts.Length; i++)
        {
            float wy = (worldXform * verts[i]).Y;
            minY = Mathf.Min(minY, wy);
            maxY = Mathf.Max(maxY, wy);
        }
        if (!YRangeOverlaps((minY + maxY) * 0.5f, (maxY - minY) * 0.5f, floorY))
        {
            return;
        }

        // Many triangles cover the same cell; dedup here so the refcounted
        // blocker list (and its mirror removal on TreeExiting) stays small.
        var cells = new HashSet<Vector3I>();
        // ConcavePolygonShape3D.Data is a flat soup: 3 consecutive verts per
        // triangle, already triangulated.
        for (int t = 0; t + 2 < verts.Length; t += 3)
        {
            Vector3 a = worldXform * verts[t];
            Vector3 b = worldXform * verts[t + 1];
            Vector3 c = worldXform * verts[t + 2];
            RasterizeTriangleXz(a.X, a.Z, b.X, b.Z, c.X, c.Z, floorY, cells);
        }
        foreach (Vector3I cell in cells)
        {
            outCells.Add(cell);
        }
    }

    // Fill cells whose center lies inside the XZ-projected triangle (a,b,c).
    private static void RasterizeTriangleXz(float ax, float az, float bx, float bz, float cx, float cz, int floorY, HashSet<Vector3I> outCells)
    {
        int minX = Mathf.FloorToInt(Mathf.Min(ax, Mathf.Min(bx, cx)));
        int maxX = Mathf.FloorToInt(Mathf.Max(ax, Mathf.Max(bx, cx)));
        int minZ = Mathf.FloorToInt(Mathf.Min(az, Mathf.Min(bz, cz)));
        int maxZ = Mathf.FloorToInt(Mathf.Max(az, Mathf.Max(bz, cz)));
        for (int wx = minX; wx <= maxX; wx++)
        {
            for (int wz = minZ; wz <= maxZ; wz++)
            {
                if (PointInTriangleXz(wx + 0.5f, wz + 0.5f, ax, az, bx, bz, cx, cz))
                {
                    outCells.Add(new Vector3I(wx, floorY, wz));
                }
            }
        }
    }

    // Half-plane sign test, winding-agnostic. A point on an edge (a zero
    // cross) is treated as inside, so cells straddling a shared edge are
    // claimed by both triangles — harmless after the HashSet dedup.
    private static bool PointInTriangleXz(float px, float pz, float ax, float az, float bx, float bz, float cx, float cz)
    {
        float d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
        float d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
        float d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
        bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(hasNeg && hasPos);
    }
}
