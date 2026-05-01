using Godot;

// Resolves the EGroundType under a world-space position. Two-stage lookup:
//   1) AUTO Terrain voxels carry a per-voxel KitId; the resolver reads the
//      kit out of ChunkMesh.ActiveKits (the world-scoped palette set up at
//      Main.StartGame) and returns its authored GroundType.
//   2) Non-Terrain authored types (Stone, Desert, Marsh, Water) map directly
//      via a static switch.
//
// The query samples the voxel just below `pos` (a tinyEpsilon under the feet)
// so a body whose origin sits right on the surface picks up the floor, not
// the air column it's standing in. If that voxel is Air, falls through to
// one cell lower as a safety net for slight float drift on slopes.
public static class GroundTypeResolver
{
    public static EGroundType Resolve(WorldState ws, Vector3 worldPos)
    {
        if (ws == null)
        {
            return EGroundType.Stone;
        }

        int fx = Mathf.FloorToInt(worldPos.X);
        int fz = Mathf.FloorToInt(worldPos.Z);
        int fy = Mathf.FloorToInt(worldPos.Y - 0.05f);

        VoxelType v = ws.GetVoxelWorld(fx, fy, fz);
        if (v == VoxelType.Air)
        {
            fy -= 1;
            v = ws.GetVoxelWorld(fx, fy, fz);
        }

        if (v == VoxelType.Terrain)
        {
            int kitId = ws.GetKitIdWorld(fx, fy, fz);
            EnvironmentKitData[] kits = ChunkMesh.ActiveKits;
            if (kits != null && kitId >= 0 && kitId < kits.Length && kits[kitId] != null)
            {
                return kits[kitId].GroundType;
            }
            return EGroundType.Grass;
        }

        return v switch
        {
            VoxelType.Stone => EGroundType.Stone,
            VoxelType.Desert => EGroundType.Sand,
            VoxelType.Marsh => EGroundType.Mud,
            VoxelType.Water => EGroundType.Water,
            _ => EGroundType.Stone,
        };
    }
}
