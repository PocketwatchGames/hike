using System.Collections.Generic;
using Godot;

// Rasterizes roofs into WorldState.CanopyAttenuation so the next sunlight pass
// sees them as sun-blocking cover, exactly as FoliageStamper does for tree
// canopies.
//
// This exists because a roof's shadow and its VOLUMETRIC shadow come from two
// different places. Surfaces darken via the directional shadow atlas, which the
// roof's shadow proxy feeds. But fog_volumetric reads sun visibility per air
// sample from the voxel light map's R channel — a field only voxels and canopy
// attenuation write to. Without this pass a roof casts a perfectly good shadow
// on the floor while sun shafts pour straight through it and the air beneath
// glows, which reads as badly wrong precisely because a roof is a large
// overhead occluder.
//
// Only the roof's BASE is stamped, as a flat sheet of columns — never the
// sloped volume. ComputeSunlight walks each column top-down and stops at the
// first thing that blocks the sky, so marking the base is enough; everything
// below is sheltered. Filling the wedge would cost far more voxels for an
// identical result.
public static class RoofSunStamper
{
    // Called by FoliageStamper as part of the same rebuild, so there is one
    // clear and one entity walk and no ordering trap between the two passes.
    public static void Stamp(WorldState world, RoofSimState roof)
    {
        RoofStyleData style = roof.Style;
        if (style == null)
        {
            return;
        }
        // Full occlusion needs one sheet; partial cover needs depth, because
        // attenuation only compounds as the column crosses successive voxels.
        int depth = style.blocksSun ? 1 : style.partialSunOcclusionDepthVoxels;
        int amount = style.blocksSun ? 0 : Mathf.Clamp(Mathf.RoundToInt(style.partialSunOcclusion * 255f), 0, 255);
        int dust = Mathf.Clamp(Mathf.RoundToInt(style.interiorDust * 255f), 0, 255);
        bool wantsDust = dust > 0 && style.interiorDustDepthVoxels > 0;
        if (depth <= 0 || (!style.blocksSun && amount <= 0))
        {
            if (!wantsDust)
            {
                return;
            }
        }

        // Deliberately wider than the visual — see brokenSunBias. The bias is
        // applied to the FRACTION, not to the threshold: the noise CDF is steep,
        // so scaling the threshold is wildly nonlinear (1.6x opened 31% of the
        // roof, 2.0x opened 66%). In fraction space the knob reads as "the sun
        // treats the roof as this much more broken", which composes predictably.
        float sunThreshold = RoofBrokenNoise.ThresholdFor(Mathf.Min(roof.Broken * style.brokenSunBias, 1f));

        var size = new RoofDimensions(style, roof.SizeX, roof.SizeZ, roof.SeamAxis, roof.SlopeDegrees, roof.Form);
        // The roof's full sky coverage, overhangs included — what physically
        // stands between the sun and the ground, rather than just the walls.
        float halfSeam = size.HalfSeam;
        float halfAcross = size.HalfAcross;

        // Roof-local axes in world XZ, carrying the entity's Y rotation.
        float cos = Mathf.Cos(roof.RotationY);
        float sin = Mathf.Sin(roof.RotationY);
        Vector2 seamAxis = Rotate(new Vector2(size.Seam.X, size.Seam.Z), cos, sin);
        Vector2 acrossAxis = Rotate(new Vector2(size.Across.X, size.Across.Z), cos, sin);

        // Conservative world AABB of the rotated footprint, then an exact
        // per-column test inside it.
        float reach = Mathf.Abs(seamAxis.X) * halfSeam + Mathf.Abs(acrossAxis.X) * halfAcross;
        float reachZ = Mathf.Abs(seamAxis.Y) * halfSeam + Mathf.Abs(acrossAxis.Y) * halfAcross;
        int minX = Mathf.FloorToInt(roof.WorldPosition.X - reach);
        int maxX = Mathf.FloorToInt(roof.WorldPosition.X + reach);
        int minZ = Mathf.FloorToInt(roof.WorldPosition.Z - reachZ);
        int maxZ = Mathf.FloorToInt(roof.WorldPosition.Z + reachZ);
        int baseY = Mathf.FloorToInt(roof.WorldPosition.Y);

        for (int wx = minX; wx <= maxX; wx++)
        {
            for (int wz = minZ; wz <= maxZ; wz++)
            {
                // Column centre, so a footprint edge lands consistently rather
                // than by which corner of the voxel got tested.
                var offset = new Vector2(
                    wx + 0.5f - roof.WorldPosition.X,
                    wz + 0.5f - roof.WorldPosition.Z);
                if (Mathf.Abs(offset.Dot(seamAxis)) > halfSeam || Mathf.Abs(offset.Dot(acrossAxis)) > halfAcross)
                {
                    continue;
                }
                // Dust fills the room whether or not this column is holed —
                // the beam coming through a hole needs air to light up several
                // metres away from the hole itself.
                if (wantsDust)
                {
                    for (int d = 0; d < style.interiorDustDepthVoxels; d++)
                    {
                        world.RaiseRoofDustWorld(wx, baseY - d, wz, dust);
                    }
                }
                // A hole leaves the column open, so the sun reaches straight
                // down it and the raymarcher gets a lit shaft through the dust.
                // Evaluated with the SAME noise the shader discards on, at the
                // column centre, so the beam lands under the gap you can see.
                if (RoofBrokenNoise.IsHole(wx + 0.5f, wz + 0.5f, sunThreshold, style.brokenScale, style.brokenScale * style.brokenEdgeRatio, style.brokenEdgeJagged))
                {
                    continue;
                }
                for (int d = 0; d < depth; d++)
                {
                    if (style.blocksSun)
                    {
                        world.SetSunOpaqueWorld(wx, baseY - d, wz);
                    }
                    else
                    {
                        world.AddCanopyAttenuationWorld(wx, baseY - d, wz, amount);
                    }
                }
            }
        }
    }

    // Cells a stamp touches, so the editor can relight just those columns
    // instead of recomputing the whole world after placing one roof.
    public static void CollectCells(RoofSimState roof, List<Vector3I> into)
    {
        RoofStyleData style = roof.Style;
        if (style == null)
        {
            return;
        }
        int depth = Mathf.Max(1, style.blocksSun ? 1 : style.partialSunOcclusionDepthVoxels);
        var size = new RoofDimensions(style, roof.SizeX, roof.SizeZ, roof.SeamAxis, roof.SlopeDegrees, roof.Form);
        float cos = Mathf.Cos(roof.RotationY);
        float sin = Mathf.Sin(roof.RotationY);
        Vector2 seamAxis = Rotate(new Vector2(size.Seam.X, size.Seam.Z), cos, sin);
        Vector2 acrossAxis = Rotate(new Vector2(size.Across.X, size.Across.Z), cos, sin);
        float reach = Mathf.Abs(seamAxis.X) * size.HalfSeam + Mathf.Abs(acrossAxis.X) * size.HalfAcross;
        float reachZ = Mathf.Abs(seamAxis.Y) * size.HalfSeam + Mathf.Abs(acrossAxis.Y) * size.HalfAcross;
        int baseY = Mathf.FloorToInt(roof.WorldPosition.Y);
        for (int wx = Mathf.FloorToInt(roof.WorldPosition.X - reach); wx <= Mathf.FloorToInt(roof.WorldPosition.X + reach); wx++)
        {
            for (int wz = Mathf.FloorToInt(roof.WorldPosition.Z - reachZ); wz <= Mathf.FloorToInt(roof.WorldPosition.Z + reachZ); wz++)
            {
                for (int d = 0; d < depth; d++)
                {
                    into.Add(new Vector3I(wx, baseY - d, wz));
                }
            }
        }
    }

    private static Vector2 Rotate(Vector2 v, float cos, float sin)
    {
        return new Vector2(cos * v.X + sin * v.Y, -sin * v.X + cos * v.Y);
    }
}
