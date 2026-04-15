using System;

// Shape channel for Dual Contouring. VoxelType stays as the material channel
// (what to draw); Density tells the mesher where surfaces lie.
//
// Derived from VoxelType on the fly via the min-rule: a shared corner is
// "inside" if any of the 8 voxels touching it is solid. Not stored —
// recomputing is cheap and keeps Voxels[] as the single source of truth.
public static class Density
{
    public const sbyte INSIDE = -127;
    public const sbyte OUTSIDE = 127;

    public static sbyte VoxelCornerDensity(VoxelType type)
    {
        if (!VoxelTypeInfo.IsSolid(type) || type == VoxelType.Barrier)
        {
            return OUTSIDE;
        }
        return INSIDE;
    }

    // Shared-corner density at world corner (cx,cy,cz). Min over the 8
    // touching voxels at (cx-1..cx, cy-1..cy, cz-1..cz).
    public static sbyte CornerDensity(int cx, int cy, int cz, Func<int, int, int, VoxelType> getVoxel)
    {
        sbyte d = OUTSIDE;
        for (int ox = 0; ox < 2; ox++)
        {
            for (int oy = 0; oy < 2; oy++)
            {
                for (int oz = 0; oz < 2; oz++)
                {
                    VoxelType v = getVoxel(cx - 1 + ox, cy - 1 + oy, cz - 1 + oz);
                    sbyte c = VoxelCornerDensity(v);
                    if (c < d)
                    {
                        d = c;
                    }
                }
            }
        }
        return d;
    }
}
