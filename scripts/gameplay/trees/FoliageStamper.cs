using System;
using System.Collections.Generic;
using Godot;

// Rasterizes every sun-occluding NON-VOXEL entity so the next ComputeSunlight
// pass (or any incremental UpdateSunlightAt) sees it: foliage canopies here,
// roofs via RoofSunStamper. Still named for foliage because that is what it
// started as and what most of it still does; it owns the Clear() for both
// fields, which is why roofs ride this walk instead of a second pass.
//
// Stamps prop foliage clusters as sun-attenuating volumes. Walks the entity
// dictionary for every PropSimState, looks up each scene's cached occluder list
// via FoliageOccluderCache, transforms by the prop's world position + Y
// rotation, and stamps each ellipsoid whose authored
// FoliageCluster.CastsSunShadow is true. Per-cluster opt-in, so decorative
// foliage (tall grass, ground cover, low bushes) doesn't silently shelter the
// player just by sharing the FoliageCluster type.
//
// The leaves land in WorldState.CanopyAttenuation and the shadow beneath them in
// WorldState.CanopyShade — see StampProp for why those are two fields and not
// one.
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
        world.CanopyShade.Clear();
        world.SunOpaque.Clear();

        // Tuning lives on SimData (Foliage Canopy Shadow group). baseDensity is
        // the density of ONE NOMINAL BLOB, authored as a 0..1 float and scaled
        // per cluster by FoliageCluster.ShadowDensity; densities add where
        // clusters (or trees) genuinely overlap, saturating at the byte ceiling.
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
                clustersStamped += StampProp(world, prop, baseDensity, shadowDepthVoxels, null);
                StampPropCover(world, prop, null);
            }
        }
        GD.Print($"[FoliageStamper] props={propsScanned} clusters={clustersStamped} canopyChunks={world.CanopyAttenuation.Count} shadeChunks={world.CanopyShade.Count}");
    }

    // Rebuild the occlusion fields inside one region only, leaving the rest of
    // the world's stamps alone. What an editor edit to non-voxel cover wants: a
    // roof occludes its own footprint, and re-rasterizing every tree in the
    // world to establish that is what made an editor undo cost seconds.
    //
    // Clearing and re-stamping the SAME box is what makes this exact — every
    // occluder overlapping the region contributes again, clipped, so overlapping
    // canopies re-stack to the value a whole-world pass would have produced.
    public static void RestampRegion(WorldState world, VoxelBox region)
    {
        if (world == null || region.IsEmpty)
        {
            return;
        }
        for (int wx = region.Min.X; wx <= region.Max.X; wx++)
        {
            for (int wy = region.Min.Y; wy <= region.Max.Y; wy++)
            {
                for (int wz = region.Min.Z; wz <= region.Max.Z; wz++)
                {
                    world.ClearSunOcclusionWorld(wx, wy, wz);
                }
            }
        }

        int baseDensity = Mathf.Clamp((int)Math.Round(world.SimData.canopyDensity * 255f), 0, 255);
        int shadowDepthVoxels = world.SimData.canopyShadowDepthVoxels;
        foreach (List<EntitySimState> bucket in world._entities.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                if (bucket[i] is RoofSimState roof)
                {
                    RoofSunStamper.Stamp(world, roof, region);
                }
                else if (bucket[i] is PropSimState prop)
                {
                    StampProp(world, prop, baseDensity, shadowDepthVoxels, region);
                    StampPropCover(world, prop, region);
                }
            }
        }
    }

    // Returns the number of clusters stamped.
    // Prop cover sheets — a prop that IS a ceiling (an arch, a platform, an
    // awning) declaring itself via a PropCover node. Stamped into SunOpaque, the
    // same field roofs use, so everything that reads cover off the world rather
    // than off physics sees it: the cutaway's probes, the sunlight walk, fog.
    //
    // Full opacity rather than canopy attenuation, because unlike foliage this is
    // authored as solid architecture and there is nothing to see through.
    private static void StampPropCover(WorldState world, PropSimState prop, VoxelBox? clip)
    {
        PropCoverPatch[] patches = PropCoverCache.GetPatches(prop.Scene);
        if (patches.Length == 0)
        {
            return;
        }
        float cos = Mathf.Cos(prop.RotationY);
        float sin = Mathf.Sin(prop.RotationY);
        for (int p = 0; p < patches.Length; p++)
        {
            PropCoverPatch patch = patches[p];
            float rx = cos * patch.CenterLocal.X + sin * patch.CenterLocal.Z;
            float rz = -sin * patch.CenterLocal.X + cos * patch.CenterLocal.Z;
            float centerX = prop.WorldPosition.X + rx;
            float centerZ = prop.WorldPosition.Z + rz;
            int sheetY = Mathf.FloorToInt(prop.WorldPosition.Y + patch.CenterLocal.Y);

            // Conservative world AABB of the rotated rectangle, then an exact
            // per-column test inside it — same shape as RoofSunStamper.
            float reachX = Mathf.Abs(cos) * patch.HalfX + Mathf.Abs(sin) * patch.HalfZ;
            float reachZ = Mathf.Abs(sin) * patch.HalfX + Mathf.Abs(cos) * patch.HalfZ;
            int minX = Mathf.FloorToInt(centerX - reachX);
            int maxX = Mathf.FloorToInt(centerX + reachX);
            int minZ = Mathf.FloorToInt(centerZ - reachZ);
            int maxZ = Mathf.FloorToInt(centerZ + reachZ);
            if (clip.HasValue)
            {
                VoxelBox box = clip.Value;
                if (sheetY < box.Min.Y || sheetY > box.Max.Y)
                {
                    continue;
                }
                minX = Mathf.Max(minX, box.Min.X);
                maxX = Mathf.Min(maxX, box.Max.X);
                minZ = Mathf.Max(minZ, box.Min.Z);
                maxZ = Mathf.Min(maxZ, box.Max.Z);
            }

            for (int wx = minX; wx <= maxX; wx++)
            {
                for (int wz = minZ; wz <= maxZ; wz++)
                {
                    // Column centre, so a rectangle edge lands consistently
                    // rather than by which corner of the voxel got tested.
                    float dx = wx + 0.5f - centerX;
                    float dz = wz + 0.5f - centerZ;
                    float localX = cos * dx - sin * dz;
                    float localZ = sin * dx + cos * dz;
                    if (Mathf.Abs(localX) > patch.HalfX || Mathf.Abs(localZ) > patch.HalfZ)
                    {
                        continue;
                    }
                    world.SetSunOpaqueWorld(wx, sheetY, wz);
                }
            }
        }
    }

    // Rasterize one prop's canopy, then derive the shadow column beneath it.
    //
    // TWO fields, because these are two different physical things. A cluster's
    // ellipsoid is LEAVES — a medium, stamped into CanopyAttenuation, which
    // charges light once per voxel of leaf it actually crosses. Everything below
    // the canopy is SHADOW: air the sun already paid to get through, stamped into
    // CanopyShade, which only the lateral spread reads. Charging the column as a
    // medium made a tree's darkness a function of its TRUNK HEIGHT rather than of
    // its foliage, because the same leaves were re-tolled at every voxel on the
    // way down.
    //
    // The column's value is DERIVED — the total leaf density that column passes
    // through — rather than stamped per cluster. Per cluster, four overlapping
    // clusters deposited four full-strength columns for the one canopy they
    // share; as an integral they contribute it exactly once, so densities can add
    // everywhere (overlapping blobs, neighbouring trees) under a single rule.
    // Setting it to the canopy integral is also what keeps lateral refill from
    // ever exceeding the vertical answer: one step into the shadow costs the same
    // as coming down through the canopy did.
    //
    // Bounds are deliberately NOT narrowed by `clip` — the column integral needs
    // the whole canopy above it even when a regional restamp only rewrites a
    // slice — so the clip is applied at each write instead, and props that miss
    // the region are rejected up front.
    private static int StampProp(WorldState world, PropSimState prop, int baseDensity, int shadowDepthVoxels, VoxelBox? clip)
    {
        FoliageOccluder[] occluders = FoliageOccluderCache.GetOccluders(prop.Scene);
        if (occluders.Length == 0)
        {
            return 0;
        }
        float cos = Mathf.Cos(prop.RotationY);
        float sin = Mathf.Sin(prop.RotationY);

        // Prop-wide bounds over the shadow-casting clusters.
        int minX = int.MaxValue, maxX = int.MinValue;
        int minZ = int.MaxValue, maxZ = int.MinValue;
        int canopyBottomY = int.MaxValue, canopyTopY = int.MinValue;
        int stamped = 0;
        for (int o = 0; o < occluders.Length; o++)
        {
            if (!IsStampable(occluders[o], baseDensity))
            {
                continue;
            }
            FoliageOccluder occ = occluders[o];
            Vector3 center = OccluderCenterWorld(prop, occ, cos, sin);
            EllipsoidBoundsXZ(occ.Radii, cos, sin, out float aabbX, out float aabbZ);
            minX = Math.Min(minX, (int)Mathf.Floor(center.X - aabbX));
            maxX = Math.Max(maxX, (int)Mathf.Ceil(center.X + aabbX));
            minZ = Math.Min(minZ, (int)Mathf.Floor(center.Z - aabbZ));
            maxZ = Math.Max(maxZ, (int)Mathf.Ceil(center.Z + aabbZ));
            canopyBottomY = Math.Min(canopyBottomY, (int)Mathf.Floor(center.Y - occ.Radii.Y));
            canopyTopY = Math.Max(canopyTopY, (int)Mathf.Ceil(center.Y + occ.Radii.Y));
            stamped++;
        }
        if (stamped == 0)
        {
            return 0;
        }

        // Floor the shadow column at one voxel below the prop's base so the
        // player's standing voxel is always covered, even when the canopy sits
        // 10+ voxels above ground.
        int columnFloorY = Mathf.FloorToInt(prop.WorldPosition.Y) - 1;
        int shadowFloorY = Math.Min(canopyBottomY - shadowDepthVoxels, columnFloorY);
        if (clip.HasValue && !IntersectsClip(clip.Value, minX, maxX, shadowFloorY, canopyTopY, minZ, maxZ))
        {
            return 0;
        }

        // Per-column canopy integral, and the lowest leaf voxel in each column
        // (the shadow starts one voxel under it). Indexed [x - minX, z - minZ].
        int width = maxX - minX + 1;
        int depth = maxZ - minZ + 1;
        int[,] columnDensity = new int[width, depth];
        int[,] columnLeafBottom = new int[width, depth];
        for (int ix = 0; ix < width; ix++)
        {
            for (int iz = 0; iz < depth; iz++)
            {
                columnLeafBottom[ix, iz] = int.MaxValue;
            }
        }

        for (int o = 0; o < occluders.Length; o++)
        {
            if (!IsStampable(occluders[o], baseDensity))
            {
                continue;
            }
            FoliageOccluder occ = occluders[o];
            StampLeaves(world, OccluderCenterWorld(prop, occ, cos, sin), occ.Radii, cos, sin,
                ClusterDensity(baseDensity, occ), clip, minX, minZ, columnDensity, columnLeafBottom);
        }

        StampShadowColumns(world, clip, minX, minZ, shadowFloorY, columnDensity, columnLeafBottom);
        return stamped;
    }

    // Rasterize one cluster's ellipsoid into CanopyAttenuation, accumulating each
    // column's total leaf density and lowest leaf voxel for the shadow pass.
    private static void StampLeaves(WorldState world, Vector3 center, Vector3 radii, float cos, float sin, int density, VoxelBox? clip, int originX, int originZ, int[,] columnDensity, int[,] columnLeafBottom)
    {
        EllipsoidBoundsXZ(radii, cos, sin, out float aabbX, out float aabbZ);
        int minX = (int)Mathf.Floor(center.X - aabbX);
        int maxX = (int)Mathf.Ceil(center.X + aabbX);
        int minZ = (int)Mathf.Floor(center.Z - aabbZ);
        int maxZ = (int)Mathf.Ceil(center.Z + aabbZ);
        int minY = (int)Mathf.Floor(center.Y - radii.Y);
        int maxY = (int)Mathf.Ceil(center.Y + radii.Y);

        float invRx = 1f / radii.X;
        float invRy = 1f / radii.Y;
        float invRz = 1f / radii.Z;

        for (int wy = minY; wy <= maxY; wy++)
        {
            float ly = wy + 0.5f - center.Y;
            if (ly > radii.Y || ly < -radii.Y)
            {
                continue;
            }
            float ny = ly * invRy;
            float nySq = ny * ny;
            for (int wz = minZ; wz <= maxZ; wz++)
            {
                for (int wx = minX; wx <= maxX; wx++)
                {
                    float dx = wx + 0.5f - center.X;
                    float dz = wz + 0.5f - center.Z;
                    // Rotate world-space delta into ellipsoid-local space
                    // (inverse of the prop's Y rotation). Exact at any rotation
                    // — nothing is resampled, so arbitrary RotationY costs no
                    // accuracy.
                    float lx = cos * dx + sin * dz;
                    float lzL = -sin * dx + cos * dz;
                    float nxSq = (lx * invRx) * (lx * invRx);
                    float nzSq = (lzL * invRz) * (lzL * invRz);
                    if (nxSq + nzSq + nySq > 1f)
                    {
                        continue;
                    }

                    // The column integral is accumulated UNCLIPPED — a regional
                    // restamp still needs the whole canopy overhead to derive the
                    // shadow correctly, even where it only rewrites a slice.
                    columnDensity[wx - originX, wz - originZ] += density;
                    ref int leafBottom = ref columnLeafBottom[wx - originX, wz - originZ];
                    if (wy < leafBottom)
                    {
                        leafBottom = wy;
                    }
                    if (!clip.HasValue || clip.Value.Contains(wx, wy, wz))
                    {
                        world.AddCanopyAttenuationWorld(wx, wy, wz, density);
                    }
                }
            }
        }
    }

    // Write each column's derived shadow, from just below its lowest leaf voxel
    // down to the shadow floor.
    private static void StampShadowColumns(WorldState world, VoxelBox? clip, int originX, int originZ, int shadowFloorY, int[,] columnDensity, int[,] columnLeafBottom)
    {
        int width = columnDensity.GetLength(0);
        int depth = columnDensity.GetLength(1);
        for (int ix = 0; ix < width; ix++)
        {
            for (int iz = 0; iz < depth; iz++)
            {
                int density = columnDensity[ix, iz];
                if (density <= 0)
                {
                    continue;
                }
                if (density > 255)
                {
                    density = 255;
                }
                int wx = originX + ix;
                int wz = originZ + iz;
                for (int wy = columnLeafBottom[ix, iz] - 1; wy >= shadowFloorY; wy--)
                {
                    if (clip.HasValue && !clip.Value.Contains(wx, wy, wz))
                    {
                        continue;
                    }
                    world.AddCanopyShadeWorld(wx, wy, wz, density);
                }
            }
        }
    }

    private static bool IsStampable(in FoliageOccluder occ, int baseDensity)
    {
        return occ.CastsSunShadow
            && occ.Radii.X > 0f && occ.Radii.Y > 0f && occ.Radii.Z > 0f
            && ClusterDensity(baseDensity, occ) > 0;
    }

    // Per-cluster leaf density: the nominal blob density scaled by the cluster's
    // authored ShadowDensity.
    private static int ClusterDensity(int baseDensity, in FoliageOccluder occ)
    {
        return Mathf.Clamp((int)Math.Round(baseDensity * occ.ShadowDensity), 0, 255);
    }

    // Rotate the occluder's local position around Y by the prop's rotation, then
    // translate to world.
    private static Vector3 OccluderCenterWorld(PropSimState prop, in FoliageOccluder occ, float cos, float sin)
    {
        float rx = cos * occ.CenterLocal.X + sin * occ.CenterLocal.Z;
        float rz = -sin * occ.CenterLocal.X + cos * occ.CenterLocal.Z;
        return new Vector3(
            prop.WorldPosition.X + rx,
            prop.WorldPosition.Y + occ.CenterLocal.Y,
            prop.WorldPosition.Z + rz);
    }

    // Half-extents of the Y-rotated ellipsoid in world XZ. Y is unrotated.
    private static void EllipsoidBoundsXZ(Vector3 radii, float cos, float sin, out float aabbX, out float aabbZ)
    {
        aabbX = Mathf.Sqrt(radii.X * radii.X * cos * cos + radii.Z * radii.Z * sin * sin);
        aabbZ = Mathf.Sqrt(radii.X * radii.X * sin * sin + radii.Z * radii.Z * cos * cos);
    }

    private static bool IntersectsClip(VoxelBox box, int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        return maxX >= box.Min.X && minX <= box.Max.X
            && maxY >= box.Min.Y && minY <= box.Max.Y
            && maxZ >= box.Min.Z && minZ <= box.Max.Z;
    }
}
