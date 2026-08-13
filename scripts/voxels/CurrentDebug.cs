using System;
using System.Text;
using Godot;

// Diagnostic: dump the water-current field as an arrow grid around the player.
// Console: `water_current_probe`.
//
// Exists because every question about flow so far — "is it uniform?", "does it
// follow the channel?", "why is there nothing mid-river?" — has been argued from
// screenshots, and the rendered surface is several layers removed from the data.
// This prints the numbers the shader actually samples, through the SAME
// trilinear path (WorldState.SampleWaterCurrent), so a disagreement between this
// and the visuals localizes the fault to the shader rather than the field.
//
//   A  — flow direction per column as an 8-way arrow, '.' where still and
//        ' ' where the column holds no water at all
//   S  — speed as a 0-9 ramp over the patch maximum, so within-channel
//        variation is visible even when every value is small
//
// A patch where every arrow agrees while the channel visibly bends means the
// field is wrong (worldgen); arrows that follow the bend while the surface does
// not means the field is fine and the shader is the problem.
public static class CurrentDebug
{
    private const int RADIUS = 12;
    // Below this the column is printed as still rather than given an arrow —
    // an arrow drawn from near-zero components is direction-from-rounding-error.
    private const float STILL_SPEED = 0.02f;

    // Indexed by octant of atan2(z, x): 0 = +X (right), advancing toward +Z,
    // which prints DOWN the page. Get this table wrong and the dump quietly
    // reports a flow field rotated from the real one.
    private static readonly char[] Arrows = { '>', '\\', 'v', '/', '<', '\\', '^', '/' };

    public static void Dump()
    {
        WorldState ws = Sim.Current?.WorldState;
        if (ws == null)
        {
            GD.Print("[current] no world loaded");
            return;
        }
        Player player = Sim.Current?.player;
        if (player == null)
        {
            GD.Print("[current] no player to centre on");
            return;
        }
        Vector3 p = player.GlobalPosition;
        int cx = Mathf.FloorToInt(p.X);
        int cz = Mathf.FloorToInt(p.Z);

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;

        int side = RADIUS * 2 + 1;
        var speed = new float[side, side];
        var dir = new Vector2[side, side];
        var wet = new bool[side, side];
        float maxSpeed = 0f;

        for (int dz = -RADIUS; dz <= RADIUS; dz++)
        {
            for (int dx = -RADIUS; dx <= RADIUS; dx++)
            {
                int wx = cx + dx;
                int wz = cz + dz;
                int ix = dx + RADIUS;
                int iz = dz + RADIUS;

                // Topmost water voxel in the column — the surface the shader
                // shades, and the height the current was stamped around.
                int surfaceY = int.MinValue;
                for (int wy = worldMaxY; wy >= worldMinY; wy--)
                {
                    if (ws.GetBlockWorld(wx, wy, wz) == Blocks.WaterId)
                    {
                        surfaceY = wy;
                        break;
                    }
                }
                if (surfaceY == int.MinValue)
                {
                    continue;
                }
                wet[ix, iz] = true;
                // Sampled at the surface fragment's own position, matching what
                // the shader reads — not at the cell centre.
                Vector3 c = ws.SampleWaterCurrent(new Vector3(wx + 0.5f, surfaceY + 0.5f, wz + 0.5f));
                var xz = new Vector2(c.X, c.Z);
                dir[ix, iz] = xz;
                speed[ix, iz] = xz.Length();
                if (speed[ix, iz] > maxSpeed)
                {
                    maxSpeed = speed[ix, iz];
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[current] centre ({cx}, {cz})  radius {RADIUS}  max speed {maxSpeed:F3} m/s");
        sb.AppendLine($"[current] +Z is DOWN the page, +X is RIGHT; arrows are flow direction");
        sb.AppendLine("A (direction)");
        for (int iz = 0; iz < side; iz++)
        {
            for (int ix = 0; ix < side; ix++)
            {
                if (!wet[ix, iz]) { sb.Append(' '); continue; }
                if (speed[ix, iz] < STILL_SPEED) { sb.Append('.'); continue; }
                Vector2 v = dir[ix, iz];
                // v.Y holds the world Z component (see the Vector2 built above).
                float ang = Mathf.Atan2(v.Y, v.X);
                int oct = Mathf.PosMod(Mathf.RoundToInt(ang / (Mathf.Pi / 4f)), 8);
                sb.Append(Arrows[oct]);
            }
            sb.Append('\n');
        }
        sb.AppendLine("S (speed, 0-9 over patch max)");
        for (int iz = 0; iz < side; iz++)
        {
            for (int ix = 0; ix < side; ix++)
            {
                if (!wet[ix, iz]) { sb.Append(' '); continue; }
                int ramp = maxSpeed > 1e-5f
                    ? Math.Clamp((int)(speed[ix, iz] / maxSpeed * 9f), 0, 9)
                    : 0;
                sb.Append((char)('0' + ramp));
            }
            sb.Append('\n');
        }
        GD.Print(sb.ToString());
    }
}
