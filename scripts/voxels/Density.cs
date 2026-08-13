using System;

// Shape channel for Dual Contouring. int stays as the material channel
// (what to draw); Density tells the mesher where surfaces lie. Not stored —
// recomputing is cheap and keeps Voxels[] as the single source of truth.
//
// Two lattices, selected by CVars.voxelCenterSampling:
//   CornerDensity — samples at voxel CORNERS via the min-rule. Dilates the
//     solid phase by one voxel, so 1-voxel-thin AIR (doorways, slits, narrow
//     tunnels) has no sign change anywhere and vanishes.
//   VoxelDensity  — samples at voxel CENTRES, one sign per voxel. Lossless:
//     thin air and thin solid both survive.
public static class Density
{
    public const sbyte INSIDE = -127;
    public const sbyte OUTSIDE = 127;

    public static sbyte TypeDensity(int type)
    {
        if (!Blocks.IsSolid(type) || type == Blocks.BarrierId)
        {
            return OUTSIDE;
        }
        return INSIDE;
    }

    // Centre-lattice density: the voxel's own sign, no neighbourhood rule.
    public static sbyte VoxelDensity(int vx, int vy, int vz, Func<int, int, int, int> getVoxel)
    {
        return TypeDensity(getVoxel(vx, vy, vz));
    }

    // Shared-corner density at world corner (cx,cy,cz). Min over the 8
    // touching voxels at (cx-1..cx, cy-1..cy, cz-1..cz).
    public static sbyte CornerDensity(int cx, int cy, int cz, Func<int, int, int, int> getVoxel)
    {
        sbyte d = OUTSIDE;
        for (int ox = 0; ox < 2; ox++)
        {
            for (int oy = 0; oy < 2; oy++)
            {
                for (int oz = 0; oz < 2; oz++)
                {
                    int v = getVoxel(cx - 1 + ox, cy - 1 + oy, cz - 1 + oz);
                    sbyte c = TypeDensity(v);
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
