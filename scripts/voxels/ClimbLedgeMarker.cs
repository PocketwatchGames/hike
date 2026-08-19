// Finds the lip of every wall the player can mantle, so the terrain mesher can
// dress that voxel in the block's climb-growth overlay — the SAME overlay
// WorldGen.StampClimbSurfaces paints down a tall climbable cliff. One visual
// language for both affordances, and no second blend path to keep in sync.
//
// WHAT grows comes from the rock (BlockData.climbGrowthSurface); this only says
// where the lip is. The mark is the top voxel of a two-voxel wall, so dressing
// it covers the surface you mantle ONTO and the face below it at once.
//
// APPEARANCE ONLY. ChunkMesherDC injects the overlay into its own cell arrays
// and never into WorldState, so ClimbProbe cannot see it and a two-voxel step
// stays a mantle rather than becoming a climbable wall.
//
// The rule is the mantle affordance's, restated in voxels. MantleProbe offers a
// climb when the surface ahead sits between PlayerData.mantleMinRise (1.05) and
// mantleMaxRise (2.05) above the one under the player; voxels are 1m, so
// exactly one rise falls inside that band. A climbable ledge IS a two-voxel wall
// standing between two standable columns.
//
// Sibling to LedgeBarrierMesher, deliberately not folded into it: that answers
// the complementary question (which drops a body may not walk off) over a
// different band — everything taller than a step gets a barrier, only a
// two-voxel rise gets a mark.
public static class ClimbLedgeMarker
{
    // Rise, in voxels, that a mantle takes. THE tie to PlayerData.mantleMinRise
    // / mantleMaxRise: widen that band to admit a 3m wall and this stops
    // describing it, and marks appear on ledges that are no longer climbable
    // (or stop appearing on ones that are).
    // Public because the mesher dresses the whole rise, not just the lip voxel,
    // and must walk exactly this many rows down to cover the wall.
    public const int ClimbRiseVoxels = 2;

    // Clear voxels a column needs above its floor to count as somewhere the
    // player can stand. Two covers the 1.5m movement capsule with slack.
    private const int Headroom = 2;

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
    // getVoxel itself.
    public static int FindClimbLip(System.Func<int, int, int, int> getVoxel, int wx, int wy, int wz)
    {
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

        // Solid for the whole rise, so the face is a wall rather than an
        // overhanging lip with a void under it.
        for (int d = 1; d < ClimbRiseVoxels; d++)
        {
            if (!Blocks.IsSolid(getVoxel(wx, wy - d, wz)))
            {
                return 0;
            }
        }

        int mask = 0;
        for (int i = 0; i < Horizontal.Length; i++)
        {
            int nx = wx + Horizontal[i].dx;
            int nz = wz + Horizontal[i].dz;
            int floorY = wy - ClimbRiseVoxels;
            if (!Blocks.IsSolid(getVoxel(nx, floorY, nz)))
            {
                continue;
            }
            // Clear AND dry. Testing solidity alone marks every shoreline: a
            // river's BED is solid and the water standing on it is not, so a 2m
            // channel beside a bank reads as a standable column one rise down.
            // Climbing out of water is a real mantle (it runs on minRise 0), but
            // it is not this affordance and marking every metre of coast for it
            // drowns the signal.
            bool standable = true;
            for (int d = 1; d <= Headroom; d++)
            {
                int v = getVoxel(nx, floorY + d, nz);
                if (Blocks.IsSolid(v) || Blocks.IsWater(v))
                {
                    standable = false;
                    break;
                }
            }
            if (!standable)
            {
                continue;
            }

            // Reject ramps and staircases. Everything above looks only at the
            // two columns either side of the face, and natural terrain is full
            // of 2-voxel steps that the mesher smooths into a walkable slope —
            // marking those puts lichen along every contour line. A ledge the
            // player reads AS a ledge has flat ground continuing on both sides,
            // so require one more voxel of it each way: on a staircase the tread
            // beyond the footing steps down again and fails here.
            int bx = wx - Horizontal[i].dx;
            int bz = wz - Horizontal[i].dz;
            if (!Blocks.IsSolid(getVoxel(bx, wy, bz)) || Blocks.IsSolid(getVoxel(bx, wy + 1, bz)))
            {
                continue;
            }
            int fx = nx + Horizontal[i].dx;
            int fz = nz + Horizontal[i].dz;
            if (!Blocks.IsSolid(getVoxel(fx, floorY, fz)) || Blocks.IsSolid(getVoxel(fx, floorY + 1, fz)))
            {
                continue;
            }

            mask |= Horizontal[i].bit;
        }
        return mask;
    }
}
