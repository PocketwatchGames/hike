using Godot;
using System.Text;

// Console dump of what the walkability sampler sees in and around the player's
// column, next to the raw voxel stack it is derived from.
//
// Exists to answer one question the nav_grid overlay cannot: when the field
// reports a surface metres below where the player is demonstrably standing,
// WHICH gate discarded the real one. The overlay draws the conclusion; this
// prints the reasoning.
//
// The candidate walk below MIRRORS WalkabilityGrid.SampleColumn's gate order.
// If that changes, change this with it — a diagnostic that disagrees with the
// sampler is worse than none.
public static class NavColumnDebug
{
    // Columns either side of the player to include. 1 -> a 3x3 block, which is
    // enough to show the edge the player is standing on without flooding the
    // console.
    private const int DefaultRadius = 1;
    // Vertical span of the raw voxel strip, relative to the player's feet.
    private const int VoxelsAbove = 4;
    private const int VoxelsBelow = 6;

    public static void Dump()
    {
        Sim sim = Sim.Current;
        Player player = sim?.player;
        WorldState ws = sim?.WorldState;
        if (player == null || ws == null)
        {
            GD.Print("[nav_column] no running game");
            return;
        }

        Vector3 p = player.GlobalPosition;
        TraversalProfile profile = player.TraversalProfileForQuery();
        int px = Mathf.FloorToInt(p.X);
        int pz = Mathf.FloorToInt(p.Z);
        int anchorY = Mathf.FloorToInt(p.Y);

        GD.Print($"[nav_column] player=({p.X:F2},{p.Y:F2},{p.Z:F2}) cell=({px},{pz}) "
            + $"grounded={player.IsGrounded} "
            + $"profile(step={profile.maxStepHeight} clearance={profile.clearanceRadius:F2} "
            + $"headroom={profile.verticalClearance} swimDepth={profile.swimDepthThreshold:F1})");

        WalkabilityCell[] column = new WalkabilityCell[WalkabilityGrid.MaxColumnLayers];
        for (int dz = -DefaultRadius; dz <= DefaultRadius; dz++)
        {
            for (int dx = -DefaultRadius; dx <= DefaultRadius; dx++)
            {
                int wx = px + dx;
                int wz = pz + dz;
                bool isPlayerColumn = dx == 0 && dz == 0;

                WalkabilityGrid.SampleColumn(ws, sim, profile, wx, anchorY, wz, column, 0);
                GD.Print($"[nav_column] ({wx},{wz}){(isPlayerColumn ? "  <-- PLAYER" : "")}");
                GD.Print($"    voxels {VoxelStrip(ws, wx, wz, anchorY)}");
                GD.Print($"    stored {StoredLayers(column)}");
                GD.Print($"    candidates:");
                WalkCandidates(ws, sim, profile, wx, wz, anchorY);
            }
        }
    }

    // Raw block column as a compact strip, highest Y first. '#' solid, '~'
    // water, '.' air, '?' outside a loaded chunk.
    private static string VoxelStrip(WorldState ws, int wx, int wz, int anchorY)
    {
        StringBuilder sb = new();
        int top = anchorY + VoxelsAbove;
        int bottom = anchorY - VoxelsBelow;
        sb.Append($"y{top}..{bottom} [");
        for (int y = top; y >= bottom; y--)
        {
            if (!ws.IsInBounds(wx, y, wz))
            {
                sb.Append('?');
                continue;
            }
            int id = ws.GetBlockWorld(wx, y, wz);
            sb.Append(Blocks.IsWater(id) ? '~' : Blocks.IsSolid(id) ? '#' : '.');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string StoredLayers(WalkabilityCell[] column)
    {
        StringBuilder sb = new();
        bool any = false;
        for (int layer = 0; layer < WalkabilityGrid.MaxColumnLayers; layer++)
        {
            WalkabilityCell c = column[layer];
            if (!c.Walkable)
            {
                break;
            }
            any = true;
            sb.Append($"[{layer}] y={c.surfaceY}{(c.IsWater ? " water" : "")} cost={c.cost:F1}  ");
        }
        if ((column[0].flags & CellFlags.OutOfBounds) != 0)
        {
            return "OUT OF BOUNDS";
        }
        return any ? sb.ToString() : "(none)";
    }

    // Re-walks the column top-down applying SampleColumn's gates in the same
    // order, printing the verdict for every air-over-solid candidate. This is
    // what identifies the discarded surface.
    private static void WalkCandidates(WorldState ws, Sim sim, in TraversalProfile profile,
        int wx, int wz, int anchorY)
    {
        int top = anchorY + VoxelsAbove;
        int bottom = anchorY - VoxelsBelow;
        int lastStored = int.MaxValue;
        int stored = 0;

        for (int y = top; y >= bottom; y--)
        {
            if (!ws.IsInBounds(wx, y, wz))
            {
                GD.Print($"      y={y}: out of bounds (scan would stop here)");
                return;
            }
            int here = ws.GetBlockWorld(wx, y, wz);
            if (Blocks.IsSolid(here))
            {
                continue;
            }
            if (Blocks.IsWater(here))
            {
                GD.Print($"      y={y}: water surface");
                continue;
            }
            if (!ws.IsInBounds(wx, y - 1, wz) || !Blocks.IsSolid(ws.GetBlockWorld(wx, y - 1, wz)))
            {
                continue; // air over air — not a surface
            }

            // Headroom.
            int headBlockedAt = int.MinValue;
            for (int h = 1; h < profile.verticalClearance; h++)
            {
                if (!ws.IsInBounds(wx, y + h, wz) || Blocks.IsSolid(ws.GetBlockWorld(wx, y + h, wz)))
                {
                    headBlockedAt = y + h;
                    break;
                }
            }
            if (headBlockedAt != int.MinValue)
            {
                GD.Print($"      y={y}: REJECTED headroom (solid at y={headBlockedAt}, needs {profile.verticalClearance})");
                continue;
            }

            // Path-blocking entity.
            int blockerAt = int.MinValue;
            if (sim != null)
            {
                for (int h = 0; h < profile.verticalClearance; h++)
                {
                    if (sim.IsPathBlocked(wx, y + h, wz))
                    {
                        blockerAt = y + h;
                        break;
                    }
                }
            }
            if (blockerAt != int.MinValue)
            {
                GD.Print($"      y={y}: REJECTED path-blocking entity at y={blockerAt}");
                continue;
            }

            // Layer separation against whatever was stored above.
            if (stored > 0 && lastStored - y < WalkabilityGrid.MinLayerSeparation)
            {
                GD.Print($"      y={y}: REJECTED too close to stored y={lastStored} "
                    + $"(needs {WalkabilityGrid.MinLayerSeparation} apart)");
                continue;
            }

            // Body fit / wall proximity.
            if (!WalkabilityGrid.ColumnFits(ws, profile, wx, y, wz, out float wallCost))
            {
                GD.Print($"      y={y}: REJECTED body does not fit (disk r={profile.clearanceRadius:F2} overlaps a wall)");
                continue;
            }

            GD.Print($"      y={y}: STANDABLE (wallCost={wallCost:F1}) -> slot {stored}");
            lastStored = y;
            stored++;
            if (stored >= WalkabilityGrid.MaxColumnLayers)
            {
                GD.Print($"      (layer budget {WalkabilityGrid.MaxColumnLayers} exhausted; deeper surfaces dropped)");
                return;
            }
        }
    }
}
