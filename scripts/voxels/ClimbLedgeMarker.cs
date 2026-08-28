// Finds the lip of every wall the player can mantle, so the terrain mesher can
// dress that voxel in the block's climb-growth overlay — the SAME overlay
// WorldFinish.StampClimbSurfaces paints down a tall climbable cliff. One visual
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
// Water is one of those standable columns, at WaterStandDrop below its own free
// surface — so a bank standing a metre proud of a lake is a two-voxel wall like
// any other and needs no case of its own.
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

    // How far BELOW its free surface a body stands or floats in water. THE tie
    // to WalkabilityGrid.SampleColumn, which stores a dry surface as the solid's
    // top face but a water surface as the index of the top water VOXEL — one
    // metre lower. Everything the mantle measures is against that convention, so
    // treating water as ground a metre down is what makes the ordinary two-voxel
    // rule come out right on a shore: a bank one voxel proud of a lake is a
    // 2.0 rise and mantleable, two voxels proud is 3.0 and refused.
    //
    // Depth does not enter into it. Wading and swimming differ in how a body is
    // held up, not in where the surface it is held at sits.
    private const int WaterStandDrop = 1;

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
        if (!Clear(getVoxel, wx, wz, wy + 1, Headroom))
        {
            return 0;
        }

        // Filled for the whole rise, so the face is a wall rather than an
        // overhanging lip with a void under it. Water counts as filled: a shelf
        // undercut by the lake it stands over is still a wall to climb out onto.
        for (int d = 1; d < ClimbRiseVoxels; d++)
        {
            int below = getVoxel(wx, wy - d, wz);
            if (!Blocks.IsSolid(below) && !Blocks.IsWater(below))
            {
                return 0;
            }
        }

        // Top face the landing must present for this to be exactly one mantle
        // down. The lip's own top face is wy + 1.
        int landingTop = wy + 1 - ClimbRiseVoxels;

        int mask = 0;
        for (int i = 0; i < Horizontal.Length; i++)
        {
            int dx = Horizontal[i].dx;
            int dz = Horizontal[i].dz;
            int nx = wx + dx;
            int nz = wz + dz;
            if (!Standable(getVoxel, nx, nz, landingTop, Headroom))
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
            int bx = wx - dx;
            int bz = wz - dz;
            if (!Blocks.IsSolid(getVoxel(bx, wy, bz)) || Blocks.IsSolid(getVoxel(bx, wy + 1, bz)))
            {
                continue;
            }
            // One voxel of headroom, not the full body fit: this only has to say
            // the surface KEEPS GOING at the same level, and the column itself
            // was never a candidate landing.
            if (!Standable(getVoxel, nx + dx, nz + dz, landingTop, 1))
            {
                continue;
            }

            mask |= Horizontal[i].bit;
        }
        return mask;
    }

    // Does this column present a surface to stand on whose top face is exactly
    // landingTop, with `headroom` voxels of usable space over it?
    //
    // Two ways to present one, which is the whole water rule: dry ground ends AT
    // that face, while water — of any depth — carries a body WaterStandDrop
    // below its own free surface, so its surface sits that much higher.
    private static bool Standable(System.Func<int, int, int, int> getVoxel, int cx, int cz,
        int landingTop, int headroom)
    {
        if (Blocks.IsSolid(getVoxel(cx, landingTop - 1, cz))
            && Clear(getVoxel, cx, cz, landingTop, headroom))
        {
            return true;
        }
        return Blocks.IsWater(getVoxel(cx, landingTop, cz))
            && Clear(getVoxel, cx, cz, landingTop + WaterStandDrop, headroom);
    }

    // Nothing solid and nothing wet in [fromY, fromY + count). Water counts as
    // blocking because a column with more water above it has its free surface
    // somewhere else, and because the space over a landing has to be air.
    private static bool Clear(System.Func<int, int, int, int> getVoxel, int cx, int cz,
        int fromY, int count)
    {
        for (int d = 0; d < count; d++)
        {
            int v = getVoxel(cx, fromY + d, cz);
            if (Blocks.IsSolid(v) || Blocks.IsWater(v))
            {
                return false;
            }
        }
        return true;
    }
}
