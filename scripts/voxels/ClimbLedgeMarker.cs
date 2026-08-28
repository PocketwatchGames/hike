// Finds the lip of every wall the player can mantle, so the terrain mesher can
// dress that voxel in the block's climb-growth overlay — the SAME overlay
// WorldFinish.StampClimbSurfaces paints down a tall climbable cliff. One visual
// language for both affordances, and no second blend path to keep in sync.
//
// WHAT grows comes from the rock (BlockData.climbGrowthSurface); this only says
// where the lip is. The mark is the top voxel of the wall, so dressing it covers
// the surface you mantle ONTO and the face below it at once.
//
// APPEARANCE ONLY. ChunkMesherDC injects the overlay into its own cell arrays
// and never into WorldState, so ClimbProbe cannot see it and a two-voxel step
// stays a mantle rather than becoming a climbable wall.
//
// The rule is the mantle affordance's, restated in voxels, and the mantle has
// two bands. From dry ground MantleProbe offers a climb between
// PlayerData.mantleMinRise (1.05) and mantleMaxRise (2.05), so exactly one rise
// — two voxels — qualifies: a ledge IS a two-voxel wall standing between two
// standable columns. Out of water there is no walking alternative, so
// Player.Locomotion drops the minimum to zero and a bank ONE voxel above the
// surface is a mantle too.
//
// Sibling to LedgeBarrierMesher, deliberately not folded into it: that answers
// the complementary question (which drops a body may not walk off) over a
// different band — everything taller than a step gets a barrier, only these
// rises get a mark.
public static class ClimbLedgeMarker
{
    // Tallest rise, in voxels, that a mantle takes. THE tie to
    // PlayerData.mantleMaxRise: widen that band to admit a 3m wall and this
    // stops describing it, and marks appear on ledges that are no longer
    // climbable (or stop appearing on ones that are).
    public const int ClimbRiseVoxels = 2;

    // Clear voxels a column needs above its floor to count as somewhere the
    // player can stand — or, over water, to float at the surface. Two covers the
    // 1.5m movement capsule with slack.
    private const int Headroom = 2;

    // Water this deep is a SWIM rather than a wade. THE tie to
    // PlayerData.swimDepthThreshold (2m, floored to voxels the way
    // WalkabilityGrid.SampleColumn floors it): the no-minimum mantle exists only
    // where the walk field calls the column a swim cell.
    private const int SwimDepthVoxels = 2;

    // Direction bits returned by FindClimbLip, in this order. The mesher needs
    // WHICH side qualifies, not just that one does: the lip edge it measures
    // distance to is the top edge of that particular face.
    public const int DirPosX = 1;
    public const int DirNegX = 2;
    public const int DirPosZ = 4;
    public const int DirNegZ = 8;

    private static readonly (int dx, int dz, int bit)[] Horizontal =
    {
        (1, 0, DirPosX), (-1, 0, DirNegX), (0, 1, DirPosZ), (0, -1, DirNegZ),
    };

    // Bitmask of the sides from which the voxel at (wx, wy, wz) is the top of a
    // mantleable wall; 0 when it is not a lip at all. World-indexed, like
    // getVoxel itself. riseVoxels comes back as the tallest qualifying face's
    // height, which is how far down the mesher may dress — a shelf standing over
    // water is one voxel proud of it, not two.
    public static int FindClimbLip(System.Func<int, int, int, int> getVoxel, int wx, int wy, int wz,
        out int riseVoxels)
    {
        riseVoxels = 0;
        if (!Blocks.IsSolid(getVoxel(wx, wy, wz)))
        {
            return 0;
        }

        // The mantle lands the player on top of this voxel, so its own column
        // has to be clear AND dry. Cheapest rejection there is — it drops every
        // buried voxel in the window on the first iteration.
        for (int d = 1; d <= Headroom; d++)
        {
            int above = getVoxel(wx, wy + d, wz);
            if (Blocks.IsSolid(above) || Blocks.IsWater(above))
            {
                return 0;
            }
        }

        int mask = 0;
        for (int i = 0; i < Horizontal.Length; i++)
        {
            int dx = Horizontal[i].dx;
            int dz = Horizontal[i].dz;
            int nx = wx + dx;
            int nz = wz + dz;

            int rise = DryLandingRise(getVoxel, wy, nx, nz, dx, dz);
            if (rise == 0)
            {
                rise = WaterLandingRise(getVoxel, wy, nx, nz);
            }
            if (rise == 0)
            {
                continue;
            }

            // Solid for the whole rise, so the face is a wall rather than an
            // overhanging lip with a void under it. A one-voxel rise has nothing
            // beneath it to check, which is what lets a shelf of rock jutting out
            // over water mark.
            bool wall = true;
            for (int d = 1; d < rise; d++)
            {
                if (!Blocks.IsSolid(getVoxel(wx, wy - d, wz)))
                {
                    wall = false;
                    break;
                }
            }
            if (!wall)
            {
                continue;
            }

            // Reject ramps and staircases. Everything above looks only at the
            // two columns either side of the face, and natural terrain is full
            // of 2-voxel steps that the mesher smooths into a walkable slope —
            // marking those puts lichen along every contour line. A ledge the
            // player reads AS a ledge has flat ground continuing behind it, so
            // require one more voxel of it: on a staircase the tread behind
            // steps up again and fails here.
            int bx = wx - dx;
            int bz = wz - dz;
            if (!Blocks.IsSolid(getVoxel(bx, wy, bz)) || Blocks.IsSolid(getVoxel(bx, wy + 1, bz)))
            {
                continue;
            }

            mask |= Horizontal[i].bit;
            if (rise > riseVoxels)
            {
                riseVoxels = rise;
            }
        }
        return mask;
    }

    // Rise, in voxels, of a dry ledge standing over the column at (nx, nz), or 0
    // when that column is not standable one full rise down. Only the full rise
    // qualifies — anything shorter is a walk-up that step-up owns.
    private static int DryLandingRise(System.Func<int, int, int, int> getVoxel, int wy,
        int nx, int nz, int dx, int dz)
    {
        int floorY = wy - ClimbRiseVoxels;
        if (!Blocks.IsSolid(getVoxel(nx, floorY, nz)))
        {
            return 0;
        }
        // Clear AND dry. Testing solidity alone reads a river's BED as a
        // standable column — the water standing on it is not solid — so a deep
        // channel beside a bank comes back as dry ground one rise down. That
        // landing is a real mantle, but it is the water case below, which
        // measures from the surface the swimmer is actually at.
        for (int d = 1; d <= Headroom; d++)
        {
            int v = getVoxel(nx, floorY + d, nz);
            if (Blocks.IsSolid(v) || Blocks.IsWater(v))
            {
                return 0;
            }
        }
        // Flat ground continuing away from the face, the far half of the
        // anti-staircase rule: on a staircase the tread beyond the footing steps
        // down again and fails here.
        int fx = nx + dx;
        int fz = nz + dz;
        if (!Blocks.IsSolid(getVoxel(fx, floorY, fz)) || Blocks.IsSolid(getVoxel(fx, floorY + 1, fz)))
        {
            return 0;
        }
        return ClimbRiseVoxels;
    }

    // Rise, in voxels, from the free surface of a swimmable water column beside
    // the lip, or 0 when there is no such surface within the mantle band.
    //
    // Hauling out of water has no minimum rise, so a bank one voxel above the
    // surface is a mantle even though a dry one-voxel step is only a walk. The
    // depth gate is what stops that marking every metre of coast: a shelving
    // beach never reaches swim depth, so it never offers the affordance and
    // never wears the crust.
    private static int WaterLandingRise(System.Func<int, int, int, int> getVoxel, int wy,
        int nx, int nz)
    {
        for (int rise = 1; rise <= ClimbRiseVoxels; rise++)
        {
            int surfaceTop = wy - rise;
            if (!Blocks.IsWater(getVoxel(nx, surfaceTop, nz)))
            {
                continue;
            }
            // Free surface with room to float in: anything solid or wet above
            // means this is not the top of the body, or the swimmer does not fit.
            bool open = true;
            for (int d = 1; d <= Headroom; d++)
            {
                int v = getVoxel(nx, surfaceTop + d, nz);
                if (Blocks.IsSolid(v) || Blocks.IsWater(v))
                {
                    open = false;
                    break;
                }
            }
            if (!open)
            {
                continue;
            }
            // Too shallow to swim in is too shallow to haul out of, and every
            // shore is shallow somewhere. Bail rather than keep descending: a
            // deeper surface below a wadeable one does not exist.
            for (int d = 1; d < SwimDepthVoxels; d++)
            {
                if (!Blocks.IsWater(getVoxel(nx, surfaceTop - d, nz)))
                {
                    return 0;
                }
            }
            return rise;
        }
        return 0;
    }
}
