using Godot;
using System.Collections.Generic;

// Console commands backing `climb_mark <height>` and `climb_probe`: stamp a
// climbable face up the wall in front of the player, and explain gate by gate
// why the climb probe accepted or refused it.
//
// The stamp writes the real OverlayFaces channel, so what it produces
// round-trips through save and subscene stamping exactly like authored data.
// The scaffolding part is the climbable flag: worldgen dresses cliffs with the
// authored lichen overlay, but this command works on ANY wall, so the blocks the
// column is made of are flipped climbable for the session instead.
public static class ClimbMarkDebug
{
    // Voxels above the player's feet to scan for the wall face. Starts AT the
    // feet, not below: one voxel down is the ground the player is standing on,
    // which is solid and would be mistaken for the wall.
    private const int ScanAbove = 3;
    // How far up a column `climb_mark 0` walks looking for voxels to clear.
    private const int ClearScanHeight = 48;

    public static void Apply(int height)
    {
        if (!TryResolveWall(out WorldState ws, out Vector3I wall, out EVoxelFace face))
        {
            return;
        }

        if (height <= 0)
        {
            int cleared = 0;
            for (int i = 0; i < ClearScanHeight; i++)
            {
                int y = wall.Y + i;
                if (!Blocks.IsSolid(ws.GetBlockWorld(wall.X, y, wall.Z)))
                {
                    break;
                }
                ws.SetOverlayFacesWorld(wall.X, y, wall.Z, 0);
                cleared++;
            }
            GD.Print($"[climb_mark] cleared {cleared} voxels at ({wall.X},{wall.Y},{wall.Z})");
            return;
        }

        // Every distinct block in the column gets flipped, not just the first.
        // A wall is routinely more than one material (rock over dirt), and
        // flagging only the bottom one leaves the grip height unclimbable —
        // which looks exactly like the feature not working.
        var blocks = new HashSet<int>();
        int marked = 0;
        for (int i = 0; i < height; i++)
        {
            int y = wall.Y + i;
            int id = ws.GetBlockWorld(wall.X, y, wall.Z);
            if (!Blocks.IsSolid(id))
            {
                break;
            }
            ws.SetOverlayFacesWorld(wall.X, y, wall.Z, (int)face);
            blocks.Add(id);
            marked++;
        }
        foreach (int id in blocks)
        {
            Blocks.SetClimbableForDebug(id, true);
        }

        GD.Print($"[climb_mark] {marked} voxels at ({wall.X},{wall.Y},{wall.Z}) face={face} "
            + $"blocks=[{string.Join(",", blocks)}] climbable for this session");
    }

    // Walks the same gates ClimbProbe and Player.TryFindClimb apply, in the same
    // order, and prints each verdict. Mirrors NavColumnDebug's job: the failure
    // is never "it does not work", it is one specific gate, and this names it.
    public static void Probe()
    {
        Sim sim = Sim.Current;
        Player player = sim?.player;
        WorldState ws = sim?.WorldState;
        if (player == null || ws == null)
        {
            GD.Print("[climb_probe] no running game");
            return;
        }
        PlayerData data = player.data;
        Vector3 p = player.GlobalPosition;
        Vector3 dir = player.BodyForwardForDebug();

        GD.Print($"[climb_probe] pos=({p.X:F2},{p.Y:F2},{p.Z:F2}) "
            + $"facing=({dir.X:F2},{dir.Z:F2}) grounded={player.IsGrounded} "
            + $"climb_movement={CVars.climbMovement.Value}");

        Vector3 ahead = p + dir * data.climbReach;
        int wx = Mathf.FloorToInt(ahead.X);
        int wy = Mathf.FloorToInt(p.Y + data.climbGripHeight);
        int wz = Mathf.FloorToInt(ahead.Z);
        EVoxelFace face = ClimbProbe.FacingBack(dir);

        int id = ws.GetBlockWorld(wx, wy, wz);
        bool solid = Blocks.IsSolid(id);
        int mask = ws.GetOverlayFacesWorld(wx, wy, wz);
        bool faceOk = VoxelFaces.Has(mask, face);
        int overlayId = ws.GetOverlayIdWorld(wx, wy, wz);
        bool blockClimbable = Blocks.IsClimbable(id);

        // Must resolve the same way ClimbProbe does — straight to the SURFACE.
        // A diagnostic that disagrees with the code it explains is worse than none.
        BlockSurfaceData overlay = overlayId != 0 && BlockCatalog.Active != null
            ? BlockCatalog.Active.GetSurfaceByLayer(overlayId)
            : null;

        GD.Print($"  target=({wx},{wy},{wz}) face={face} block={id} solid={solid}");
        GD.Print($"  overlayFaces={mask} ({VoxelFaces.Resolve(mask)}) faceAllowed={faceOk}");
        GD.Print($"  overlayId={overlayId} overlaySurface={(overlay != null ? overlay.surfaceName.ToString() : "n/a")} "
            + $"overlayClimbable={(overlay != null ? overlay.climbable.ToString() : "n/a")} "
            + $"blockClimbable={blockClimbable}");

        float dot = player.BodyForwardForDebug().Dot(new Vector3(VoxelFaces.Delta(face).X, 0f, VoxelFaces.Delta(face).Z));
        float need = -Mathf.Cos(data.climbFacingAngle);
        GD.Print($"  facingDot={dot:F3} needs<={need:F3} pass={dot <= need}");

        // A short wall offers a MANTLE, and TryTraversalPress ranks that above a
        // climb, so a perfectly climbable face can still never be reached by the
        // Dash press.
        bool canMantle = player.CanMantle();
        GD.Print($"  VERDICT canClimb={player.CanClimb()} canMantle={canMantle}"
            + (canMantle ? "  <- mantle takes the Dash press; climb is not reached" : ""));
    }

    // The first solid voxel in the column ahead, scanning up from the player's
    // feet, plus the face of it that looks back at them.
    private static bool TryResolveWall(out WorldState ws, out Vector3I wall, out EVoxelFace face)
    {
        ws = null;
        wall = default;
        face = EVoxelFace.None;

        Sim sim = Sim.Current;
        Player player = sim?.player;
        ws = sim?.WorldState;
        if (player == null || ws == null)
        {
            GD.Print("[climb_mark] no running game");
            return false;
        }

        Vector3 p = player.GlobalPosition;
        Vector3 dir = player.BodyForwardForDebug();
        face = ClimbProbe.FacingBack(dir);
        Vector3I step = VoxelFaces.Delta(VoxelFaces.Opposite(face));

        int x = Mathf.FloorToInt(p.X) + step.X;
        int z = Mathf.FloorToInt(p.Z) + step.Z;
        int baseY = Mathf.FloorToInt(p.Y);
        for (int dy = 0; dy <= ScanAbove; dy++)
        {
            if (Blocks.IsSolid(ws.GetBlockWorld(x, baseY + dy, z)))
            {
                wall = new Vector3I(x, baseY + dy, z);
                return true;
            }
        }

        GD.Print($"[climb_mark] no wall in front of the player (looked at column ({x},{z}) "
            + $"from y={baseY} up {ScanAbove})");
        return false;
    }
}
