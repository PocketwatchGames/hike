using Godot;

// Finds the climbable wall face in front of the player, and answers "is this
// face still climbable" for every tick of a climb in progress. Pure query over
// the voxel grid — it decides WHETHER and WHERE, never how, the same division
// MantleProbe keeps.
//
// Distinct from MantleProbe: that one asks the walk field where a body can
// STAND, which is why it caps out at maxRise. This asks whether a wall is
// dressed in something you can hold on to, which has no height limit.
public static class ClimbProbe
{
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
