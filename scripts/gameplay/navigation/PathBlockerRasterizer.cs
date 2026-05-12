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

    private static void CollectFromNode(Node node, int floorY, List<Vector3I> outCells)
    {
        // Only environment-layer bodies count as path blockers — hurtboxes
        // (layer 32) and interactive areas (layer 4) sit on the same entity
        // but mustn't inflate the walkable grid.
        if (node is CollisionObject3D body && (body.CollisionLayer & (uint)ECollisionLayer.Environment) != 0)
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
        }
        // Other shape types (ConcavePolygon, ConvexPolygon, Heightmap,
        // Separation, World3D) are silently skipped — none are used as
        // path blockers in this codebase. Add a case if that changes.
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
}
