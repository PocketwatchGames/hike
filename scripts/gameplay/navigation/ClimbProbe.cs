using Godot;

// Finds a climbable wall face to ATTACH to, and answers "is this rock
// climbable" for a contact point. Pure query over the voxel grid — it decides
// WHETHER and WHERE, never how, the same division MantleProbe keeps.
//
// It does NOT follow the wall during a climb. Once attached the body is driven
// by capsule sweeps against the real collider (Player.TickClimbAttached), which
// is the only thing that sees the smooth DC surface the player is actually
// touching; this quantizes to axis faces and would fight it.
//
// Distinct from MantleProbe: that one asks the walk field where a body can
// STAND, which is why it caps out at maxRise. This asks whether a wall is
// dressed in something you can hold on to, which has no height limit.
public static class ClimbProbe
{
    // Sampling just under the feet, so a body standing exactly on a surface
    // reads the floor rather than the air column it occupies.
    private const float GroundEpsilon = 0.05f;
    // How far below the player's feet TryFindDescent looks for the lip.
    private const int LipSearchDepth = 2;
    // Where inside the lip voxel the hands grip. Mid-voxel, so the grip cannot
    // round up into the air above the lip or down into the voxel below it.
    private const float LipGripInset = 0.5f;

    public readonly struct Settings
    {
        // How far in front of the player to look for the wall, in metres. Must
        // clear the movement capsule's radius or the probe samples the column
        // the player already stands in.
        public readonly float reach;
        // Height above the player's origin the hands grip at. The wall is
        // sampled here rather than at the feet so a one-voxel lip underfoot
        // isn't mistaken for a climbable face.
        public readonly float gripHeight;

        public Settings(float reach, float gripHeight)
        {
            this.reach = reach;
            this.gripHeight = gripHeight;
        }
    }

    public readonly struct Attachment
    {
        public readonly Vector3I voxel;
        public readonly EVoxelFace face;
        // Outward from the face, horizontal and unit length — points from the
        // wall back toward the player.
        public readonly Vector3 normal;

        public Attachment(Vector3I voxel, EVoxelFace face, Vector3 normal)
        {
            this.voxel = voxel;
            this.face = face;
            this.normal = normal;
        }
    }

    // The whole test, in one place: solid, face-masked in, and climbable by
    // whatever dresses it.
    public static bool IsClimbableFace(WorldState ws, int wx, int wy, int wz, EVoxelFace face)
    {
        if (ws == null)
        {
            return false;
        }
        int id = ws.GetBlockWorld(wx, wy, wz);
        if (!Blocks.IsSolid(id))
        {
            return false;
        }
        if (!VoxelFaces.Has(ws.GetOverlayFacesWorld(wx, wy, wz), face))
        {
            return false;
        }
        return ResolveClimbable(ws, wx, wy, wz, id);
    }

    // Either carrier grants it. This deliberately does NOT follow
    // GroundTypeResolver's most-specific-wins override: what you STAND on is one
    // material, but climbability asks "is there anything here to grip", which is
    // additive. Overriding instead of OR-ing means worldgen's moss — stamped on
    // exactly the air-exposed wall voxels you would climb — silently cancels the
    // ivy underneath it.
    private static bool ResolveClimbable(WorldState ws, int wx, int wy, int wz, int blockId)
    {
        if (Blocks.IsClimbable(blockId))
        {
            return true;
        }
        BlockCatalog catalog = BlockCatalog.Active;
        if (catalog == null)
        {
            return false;
        }
        int overlayId = ws.GetOverlayIdWorld(wx, wy, wz);
        if (overlayId == 0)
        {
            return false;
        }
        // Straight to the SURFACE. An overlay id names an atlas layer, so asking
        // the layer whether it is climbable needs no stand-in block whose top
        // surface happens to be this one.
        BlockSurfaceData surface = catalog.GetSurfaceByLayer(overlayId);
        return surface != null && surface.climbable;
    }

    // Every gate IsClimbableFace applies, spelled out, for the climb trace. The
    // point of a diagnostic is to name which test said no — "not climbable" is
    // the one answer that is never actionable.
    public static string Describe(WorldState ws, int wx, int wy, int wz, EVoxelFace face)
    {
        if (ws == null)
        {
            return "no world";
        }
        int id = ws.GetBlockWorld(wx, wy, wz);
        bool solid = Blocks.IsSolid(id);
        int mask = ws.GetOverlayFacesWorld(wx, wy, wz);
        bool faceOk = VoxelFaces.Has(mask, face);
        int overlayId = ws.GetOverlayIdWorld(wx, wy, wz);
        BlockSurfaceData surface = overlayId != 0 && BlockCatalog.Active != null
            ? BlockCatalog.Active.GetSurfaceByLayer(overlayId)
            : null;
        string verdict = !solid ? "NOT-SOLID"
            : !faceOk ? "FACE-NOT-MARKED"
            : !(Blocks.IsClimbable(id) || (surface != null && surface.climbable)) ? "NOT-CLIMBABLE"
            : "ok";
        return $"blk={id} solid={(solid ? 'T' : 'F')} mask={mask}({VoxelFaces.Resolve(mask)}) "
            + $"need={face} faceOk={(faceOk ? 'T' : 'F')} ovl={overlayId}"
            + $"({(surface != null ? surface.surfaceName.ToString() : "-")}"
            + $"/{(surface != null && surface.climbable ? "climb" : "no")}) "
            + $"blkClimb={(Blocks.IsClimbable(id) ? 'T' : 'F')} => {verdict}";
    }

    // The face of the voxel ahead that looks back at a player travelling along
    // `dir`. Quantized to the dominant horizontal axis: the DC surface is smooth
    // but the channel is per-voxel, so a face is one of four.
    public static EVoxelFace FacingBack(Vector3 dir)
    {
        if (Mathf.Abs(dir.X) >= Mathf.Abs(dir.Z))
        {
            return dir.X > 0f ? EVoxelFace.NegX : EVoxelFace.PosX;
        }
        return dir.Z > 0f ? EVoxelFace.NegZ : EVoxelFace.PosZ;
    }

    // The face whose outward normal a unit vector already is. Quantized like
    // FacingBack, so a normal that has drifted off-axis still resolves.
    public static EVoxelFace FromNormal(Vector3 normal)
    {
        if (Mathf.Abs(normal.X) >= Mathf.Abs(normal.Z))
        {
            return normal.X > 0f ? EVoxelFace.PosX : EVoxelFace.NegX;
        }
        return normal.Z > 0f ? EVoxelFace.PosZ : EVoxelFace.NegZ;
    }

    // Standing at the LIP of a climbable wall, looking out over it.
    //
    // The geometry is inverted from TryFind: there is no wall in front — that is
    // the drop — and the face to grab hangs BELOW the player, on the outward
    // side of the column they are standing on. So the face points ALONG their
    // facing rather than back at them, and the search runs down a column instead
    // of forward into one.
    //
    // `feetY` is where the body ends up: low enough that the grip height lands
    // inside the top climbable voxel, which is what makes the player hang just
    // under the lip rather than float level with it.
    public static bool TryFindDescent(WorldState ws, Vector3 position, Vector3 facing,
        in Settings settings, out Attachment attachment, out float feetY)
    {
        attachment = default;
        feetY = 0f;
        if (ws == null)
        {
            return false;
        }

        Vector3 dir = new(facing.X, 0f, facing.Z);
        if (dir.LengthSquared() < 1e-6f)
        {
            return false;
        }
        dir = dir.Normalized();

        EVoxelFace face = VoxelFaces.Opposite(FacingBack(dir));
        Vector3I step = VoxelFaces.Delta(face);
        int px = Mathf.FloorToInt(position.X);
        int pz = Mathf.FloorToInt(position.Z);
        int standY = Mathf.FloorToInt(position.Y - GroundEpsilon);

        // There must be somewhere to descend INTO. Without this a player facing
        // a solid wall passes the face test on their own column and gets lowered
        // into the ground.
        if (!Blocks.IsEmpty(ws.GetBlockWorld(px + step.X, standY, pz + step.Z)))
        {
            return false;
        }

        // Their own column first (standing hard against the edge, which the
        // ledge barriers make the normal case), then one step out for a player
        // whose lip is the column ahead.
        for (int forward = 0; forward <= 1; forward++)
        {
            int cx = px + step.X * forward;
            int cz = pz + step.Z * forward;
            for (int dy = 0; dy <= LipSearchDepth; dy++)
            {
                int wy = standY - dy;
                if (!Blocks.IsSolid(ws.GetBlockWorld(cx, wy, cz)))
                {
                    continue;
                }
                // First solid going down is the lip. If ITS outward face is not
                // climbable, nothing below it is reachable from here either.
                if (!IsClimbableFace(ws, cx, wy, cz, face))
                {
                    break;
                }
                Vector3I d = VoxelFaces.Delta(face);
                attachment = new Attachment(new Vector3I(cx, wy, cz), face, new Vector3(d.X, d.Y, d.Z));
                feetY = wy + LipGripInset - settings.gripHeight;
                return true;
            }
        }
        return false;
    }

    // `facing` is the horizontal direction the player is looking / moving; its Y
    // is ignored. False is the common case and stays cheap — one voxel read.
    public static bool TryFind(WorldState ws, Vector3 position, Vector3 facing,
        in Settings settings, out Attachment attachment)
    {
        attachment = default;
        if (ws == null)
        {
            return false;
        }

        Vector3 dir = new(facing.X, 0f, facing.Z);
        if (dir.LengthSquared() < 1e-6f)
        {
            return false;
        }
        dir = dir.Normalized();

        Vector3 ahead = position + dir * settings.reach;
        int wx = Mathf.FloorToInt(ahead.X);
        int wy = Mathf.FloorToInt(position.Y + settings.gripHeight);
        int wz = Mathf.FloorToInt(ahead.Z);

        EVoxelFace face = FacingBack(dir);
        if (!IsClimbableFace(ws, wx, wy, wz, face))
        {
            return false;
        }

        Vector3I d = VoxelFaces.Delta(face);
        attachment = new Attachment(new Vector3I(wx, wy, wz), face, new Vector3(d.X, d.Y, d.Z));
        return true;
    }
}
