using Godot;

// The six faces of a voxel, one bit each. Stamped per voxel in
// ChunkState.OverlayFaces to say which faces the voxel's OverlayId actually
// dresses — ivy on the north wall of a building's corner column without also
// dressing (and making climbable) the east wall of that same column.
//
// A stored mask of 0 means ALL faces, not none. "No overlay here" is already
// said by OverlayId == 0, which leaves zero free to mean unrestricted — and
// that is what every world written before this channel existed decodes as, so
// moss keeps wrapping every exposed face. Do not invert this.
[System.Flags]
public enum EVoxelFace : byte
{
    None = 0,
    PosX = 1,
    NegX = 2,
    PosY = 4,
    NegY = 8,
    PosZ = 16,
    NegZ = 32,
    All = PosX | NegX | PosY | NegY | PosZ | NegZ,
}

public static class VoxelFaces
{
    // Yaw order about +Y, matching SubsceneRotator's cell mapping
    // (dx, dz) -> (dz, -dx). One quarter turn steps one place along this.
    private static readonly EVoxelFace[] YawCycle =
    {
        EVoxelFace.PosX, EVoxelFace.NegZ, EVoxelFace.NegX, EVoxelFace.PosZ,
    };

    // Expands the stored 0 = "all faces" shorthand. Always resolve before
    // testing an individual bit.
    public static EVoxelFace Resolve(int stored)
    {
        return stored == 0 ? EVoxelFace.All : (EVoxelFace)stored;
    }

    public static bool Has(int stored, EVoxelFace face)
    {
        return (Resolve(stored) & face) != 0;
    }

    // The face of a voxel that looks toward its neighbour one step along
    // `delta`. Delta is a unit step on exactly one axis.
    public static EVoxelFace FromDelta(int dx, int dy, int dz)
    {
        if (dx > 0) { return EVoxelFace.PosX; }
        if (dx < 0) { return EVoxelFace.NegX; }
        if (dy > 0) { return EVoxelFace.PosY; }
        if (dy < 0) { return EVoxelFace.NegY; }
        if (dz > 0) { return EVoxelFace.PosZ; }
        if (dz < 0) { return EVoxelFace.NegZ; }
        return EVoxelFace.None;
    }

    public static Vector3I Delta(EVoxelFace face)
    {
        switch (face)
        {
            case EVoxelFace.PosX: return new Vector3I(1, 0, 0);
            case EVoxelFace.NegX: return new Vector3I(-1, 0, 0);
            case EVoxelFace.PosY: return new Vector3I(0, 1, 0);
            case EVoxelFace.NegY: return new Vector3I(0, -1, 0);
            case EVoxelFace.PosZ: return new Vector3I(0, 0, 1);
            case EVoxelFace.NegZ: return new Vector3I(0, 0, -1);
            default: return Vector3I.Zero;
        }
    }

    public static EVoxelFace Opposite(EVoxelFace face)
    {
        Vector3I d = Delta(face);
        return FromDelta(-d.X, -d.Y, -d.Z);
    }

    // Rotates a whole mask about +Y. Y faces sit on the rotation axis and never
    // move. Both None and All are fixed points, which is what keeps the
    // 0 = all-faces shorthand surviving a subscene turn unchanged.
    public static EVoxelFace RotateQuarterTurns(EVoxelFace faces, int turns)
    {
        int t = ((turns % 4) + 4) % 4;
        if (t == 0)
        {
            return faces;
        }

        EVoxelFace result = faces & (EVoxelFace.PosY | EVoxelFace.NegY);
        for (int i = 0; i < YawCycle.Length; i++)
        {
            if ((faces & YawCycle[i]) != 0)
            {
                result |= YawCycle[(i + t) % YawCycle.Length];
            }
        }
        return result;
    }
}
