using System;
using System.Collections.Generic;
using Godot;

// Rasterizes every sun-occluding NON-VOXEL entity so the next ComputeSunlight
// pass (or any incremental UpdateSunlightAt) sees it: foliage canopies here,
// roofs via RoofSunStamper. Still named for foliage because that is what it
// started as and what most of it still does; it owns the Clear() for both
// fields, which is why roofs ride this walk instead of a second pass.
//
// Stamps prop foliage clusters into WorldState.CanopyAttenuation as
// sun-attenuating volumes. Walks the entity dictionary for
// every PropSimState, looks up each scene's cached occluder list via
// FoliageOccluderCache, transforms by the prop's world position + Y
// rotation, and stamps each ellipsoid + downward shadow column whose
// authored FoliageCluster.CastsSunShadow is true. Per-cluster opt-in,
// so decorative foliage (tall grass, ground cover, low bushes) doesn't
// silently shelter the player just by sharing the FoliageCluster type.
public static class FoliageStamper
{
    public static void Stamp(WorldState world)
    {
        if (world == null)
        {
            return;
        }
        // Clean slate — caller may rebuild after a tree-change. Drops
        // every chunk's canopy array; rebuilding is O(n_clusters * n_voxels).
        world.CanopyAttenuation.Clear();
        world.SunOpaque.Clear();
        world.ClearRoofDust();

        // Tuning lives on SimData (Foliage Canopy Shadow group). baseDensity
        // is authored as a 0..1 float so overlapping clusters stack toward
        // the byte ceiling — at the default 0.4, two clusters land near
        // saturated, three+ peg at 255.
        int baseDensity = Mathf.Clamp((int)Math.Round(world.SimData.canopyDensity * 255f), 0, 255);
        int shadowDepthVoxels = world.SimData.canopyShadowDepthVoxels;

        int propsScanned = 0;
        int clustersStamped = 0;
        foreach (List<EntitySimState> bucket in world._entities.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                // Roofs occlude sun through the same field, and ride this walk
                // so there is one clear and one entity pass — a separate
                // stamper would have to run strictly after this one's Clear().
                if (bucket[i] is RoofSimState roof)
                {
                    RoofSunStamper.Stamp(world, roof);
                    continue;
                }
                if (bucket[i] is not PropSimState prop)
                {
                    continue;
                }
                propsScanned++;
                FoliageOccluder[] occluders = FoliageOccluderCache.GetOccluders(prop.Scene);
                if (occluders.Length == 0)
                {
                    continue;
                }
                float cos = Mathf.Cos(prop.RotationY);
                float sin = Mathf.Sin(prop.RotationY);
                for (int o = 0; o < occluders.Length; o++)
                {
                    FoliageOccluder occ = occluders[o];
                    if (!occ.CastsSunShadow)
                    {
                        continue;
                    }
                    // Rotate occluder's local position around Y by prop's
                    // rotation, then translate to world.
                    float rx = cos * occ.CenterLocal.X + sin * occ.CenterLocal.Z;
                    float rz = -sin * occ.CenterLocal.X + cos * occ.CenterLocal.Z;
                    Vector3 centerWorld = new Vector3(
                        prop.WorldPosition.X + rx,
                        prop.WorldPosition.Y + occ.CenterLocal.Y,
                        prop.WorldPosition.Z + rz);
                    // Floor the shadow column at one voxel below the prop's
                    // base so the player's standing voxel is always covered,
                    // even when the canopy sits 10+ voxels above ground.
                    int columnFloorY = Mathf.FloorToInt(prop.WorldPosition.Y) - 1;
                    StampEllipsoid(world, centerWorld, occ.Radii, cos, sin, columnFloorY, baseDensity, shadowDepthVoxels);
                    clustersStamped++;
                }
            }
        }
        GD.Print($"[FoliageStamper] props={propsScanned} clusters={clustersStamped} canopyChunks={world.CanopyAttenuation.Count}");
    }

    private static void StampEllipsoid(WorldState world, Vector3 center, Vector3 radii, float cos, float sin, int columnFloorY, int baseDensity, int shadowDepthVoxels)
    {
        if (radii.X <= 0f || radii.Y <= 0f || radii.Z <= 0f)
        {
            return;
        }
        // AABB of the Y-rotated ellipsoid in world XZ. Y is unrotated.
        float aabbX = Mathf.Sqrt(radii.X * radii.X * cos * cos + radii.Z * radii.Z * sin * sin);
        float aabbZ = Mathf.Sqrt(radii.X * radii.X * sin * sin + radii.Z * radii.Z * cos * cos);

        int minX = (int)Mathf.Floor(center.X - aabbX);
        int maxX = (int)Mathf.Ceil(center.X + aabbX);
        int minZ = (int)Mathf.Floor(center.Z - aabbZ);
        int maxZ = (int)Mathf.Ceil(center.Z + aabbZ);
        int maxY = (int)Mathf.Ceil(center.Y + radii.Y);
        int shadowBottomConst = (int)Mathf.Floor(center.Y - radii.Y) - shadowDepthVoxels;
        int minY = Math.Min(shadowBottomConst, columnFloorY);

        float invRx = 1f / radii.X;
        float invRy = 1f / radii.Y;
        float invRz = 1f / radii.Z;

        for (int wy = minY; wy <= maxY; wy++)
        {
            float ly = wy + 0.5f - center.Y;
            // Above the ellipsoid — no occlusion (sun hasn't reached the
            // canopy yet from this column's angle).
            if (ly > radii.Y)
            {
                continue;
            }
            for (int wz = minZ; wz <= maxZ; wz++)
            {
                for (int wx = minX; wx <= maxX; wx++)
                {
                    float dx = wx + 0.5f - center.X;
                    float dz = wz + 0.5f - center.Z;
                    // Rotate world-space delta into ellipsoid-local space
                    // (inverse of the prop's Y rotation).
                    float lx = cos * dx + sin * dz;
                    float lzL = -sin * dx + cos * dz;

                    float nxSq = (lx * invRx) * (lx * invRx);
                    float nzSq = (lzL * invRz) * (lzL * invRz);
                    float xzNorm = nxSq + nzSq;
                    if (xzNorm > 1f)
                    {
                        // Outside the ellipse silhouette — no canopy here
                        // and no shadow column either.
                        continue;
                    }

                    if (ly >= -radii.Y)
                    {
                        // Inside the actual ellipsoid (full 3D test).
                        float ny = ly * invRy;
                        if (xzNorm + ny * ny > 1f)
                        {
                            continue;
                        }
                    }
                    // Below the ellipsoid bottom (ly < -radii.Y): minY's
                    // floor already bounded the loop, so any voxel reaching
                    // this point is inside the shadow column.

                    world.AddCanopyAttenuationWorld(wx, wy, wz, baseDensity);
                }
            }
        }
    }
}
