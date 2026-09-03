using Godot;

// Where a coiled rope's line hangs once it is thrown over the edge: pure query
// over the voxel grid, no state, the division ClimbProbe and MantleProbe keep.
//
// Two halves that must not be confused. The rope hangs against the LIP column —
// the last supported cell before the ground falls away — offset just clear of
// its face, and NOT down the middle of the open cell beyond it. That offset is
// what lets a climber reaching the top find the ledge: Player.TryClimbTopOut
// probes climbReach horizontally along the hold's inward normal, so a rope hung
// a whole cell out over the drop puts the ledge out of that reach and a climber
// arriving at the top has nothing to top out onto.
//
// Every exit names the gate it failed, because every one of them looks identical
// from the game: a coil that resolves nothing offers no prompt at all, so
// "aimed at a wall", "edge too far", "drop too short" and "feature broken" are
// one symptom. `rope_probe` prints these (CoiledRope.Probe).
public static class RopeDrop
{
    // How far above and below its seat to look for the ground the coil rests on.
    //
    // The seat cannot be trusted to a voxel: the editor places an entity at the
    // terrain RAYCAST hit, which is the dual-contoured surface, and DC puts one
    // vertex per cell at the density minimizer — so the drawn ground can sit as
    // much as a whole voxel off the air/solid boundary, and it leans furthest
    // exactly at a lip, where these are placed. Flooring the seat and demanding
    // that cell be solid therefore failed on most real placements, whichever way
    // the coil faced. This is the same reason Player.TryResolveContactVoxel
    // marches instead of stepping a fixed distance.
    private const int GroundSearchUp = 1;
    private const int GroundSearchDown = 2;

    public readonly struct Result
    {
        // Horizontal line the rope hangs on, and the two ends of it. TopY is the
        // walkable surface at the lip; BottomY is the surface it lands on.
        public readonly Vector3 Line;
        public readonly float TopY;
        public readonly float BottomY;
        // Outward from the wall, horizontal and unit length — the direction a
        // climber hangs off the rope, and the way the coil was authored facing.
        public readonly Vector3 Outward;

        public Result(Vector3 line, float topY, float bottomY, Vector3 outward)
        {
            Line = line;
            TopY = topY;
            BottomY = bottomY;
            Outward = outward;
        }

        public float Length => TopY - BottomY;
    }

    // `facing` is the coil's authored yaw direction; its Y is ignored. Fails
    // when there is no edge within `searchCells`, or nothing to land on within
    // `maxDrop` — a rope ending in mid-air is worse than no rope, because the
    // climber only finds out at the bottom. `reason` is filled on every path,
    // success included.
    public static bool Resolve(WorldState ws, Vector3 coilPosition, Vector3 facing,
        int searchCells, float maxDrop, float wallClearance, float minDrop,
        out Result result, out string reason)
    {
        result = default;
        if (ws == null)
        {
            reason = "no world";
            return false;
        }

        Vector3 flat = new(facing.X, 0f, facing.Z);
        if (flat.LengthSquared() < 1e-6f)
        {
            reason = "coil has no horizontal facing";
            return false;
        }
        flat = flat.Normalized();

        // Quantized to the dominant axis, like every other query against this
        // grid: the cells are axis-aligned, so "the way it faces" is one of four.
        EVoxelFace outFace = VoxelFaces.Opposite(ClimbProbe.FacingBack(flat));
        Vector3I step = VoxelFaces.Delta(outFace);
        Vector3 outward = new(step.X, 0f, step.Z);

        int x = Mathf.FloorToInt(coilPosition.X);
        int z = Mathf.FloorToInt(coilPosition.Z);
        if (!TryResolveGround(ws, x, Mathf.FloorToInt(coilPosition.Y), z, out int standY))
        {
            reason = $"no ground under the coil at ({x},{z}) within "
                + $"{GroundSearchUp} up / {GroundSearchDown} down of y={Mathf.FloorToInt(coilPosition.Y)}";
            return false;
        }

        // Walk outward for the lip: the last cell with ground under it before
        // one that has none. Starting at the coil's own cell covers a coil set
        // right at the edge, which is how they are meant to be placed.
        int lipX = x;
        int lipZ = z;
        bool foundEdge = false;
        int walked = 0;
        for (int i = 0; i <= searchCells; i++)
        {
            int cx = x + step.X * i;
            int cz = z + step.Z * i;
            // The column has to be open at coil height to walk into at all; a
            // wall in the way means the coil faces the wrong way entirely.
            if (!Blocks.IsEmpty(ws.GetBlockWorld(cx, standY + 1, cz)))
            {
                reason = $"blocked {i} cell(s) out at ({cx},{standY + 1},{cz}) — the coil faces into "
                    + $"{(i == 0 ? "something standing on its own cell" : "a wall")}, not over an edge";
                return false;
            }
            if (!Blocks.IsSolid(ws.GetBlockWorld(cx, standY, cz)))
            {
                // Ground gone: the cell BEFORE this one is the lip.
                foundEdge = i > 0;
                lipX = cx - step.X;
                lipZ = cz - step.Z;
                break;
            }
            // Still supported — this becomes the lip if the next cell is open.
            lipX = cx;
            lipZ = cz;
            walked = i;
        }
        if (!foundEdge)
        {
            reason = $"no edge within {searchCells} cell(s) of ({x},{z}) facing "
                + $"({step.X},{step.Z}) — ground still solid {walked + 1} cell(s) out; "
                + "turn the coil to face out over the drop, or move it nearer the lip";
            return false;
        }

        float topY = standY + 1f;

        // Down the open column just past the lip for the landing. Scanning the
        // lip's own column instead would find the lip itself.
        int dropX = lipX + step.X;
        int dropZ = lipZ + step.Z;
        int lowest = Mathf.FloorToInt(topY - maxDrop);
        float bottomY = float.NaN;
        for (int wy = standY - 1; wy >= lowest; wy--)
        {
            int id = ws.GetBlockWorld(dropX, wy, dropZ);
            // Water counts as a bottom: a rope into a pool ends at the surface,
            // and the climb refuses to descend past the waterline anyway.
            if (Blocks.IsSolid(id) || Blocks.IsWater(id))
            {
                bottomY = wy + 1f;
                break;
            }
        }
        if (float.IsNaN(bottomY))
        {
            reason = $"nothing to land on within {maxDrop:F0}m below the lip at ({dropX},{dropZ})";
            return false;
        }
        if (topY - bottomY < minDrop)
        {
            reason = $"drop is only {topY - bottomY:F1}m at ({dropX},{dropZ}), under the {minDrop:F1}m "
                + "minimum — short enough to climb down without a rope";
            return false;
        }

        // Against the lip's outer face, clear of the rock by a hair.
        Vector3 line = new(
            lipX + 0.5f + outward.X * (0.5f + wallClearance),
            0f,
            lipZ + 0.5f + outward.Z * (0.5f + wallClearance));
        result = new Result(line, topY, bottomY, outward);
        reason = $"ok — lip ({lipX},{lipZ}) top {topY:F1}, landing ({dropX},{dropZ}) bottom {bottomY:F1}, "
            + $"{result.Length:F1}m";
        return true;
    }

    // The cell the coil is resting ON — the first solid one at or below its
    // seat. See GroundSearchUp/Down for why the seat is not simply floored.
    private static bool TryResolveGround(WorldState ws, int x, int seatY, int z, out int standY)
    {
        for (int y = seatY + GroundSearchUp; y >= seatY - GroundSearchDown; y--)
        {
            if (Blocks.IsSolid(ws.GetBlockWorld(x, y, z)))
            {
                standY = y;
                return true;
            }
        }
        standY = 0;
        return false;
    }
}
