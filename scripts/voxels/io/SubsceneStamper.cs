using System;
using Godot;

// Stamps a SubsceneState into a WorldState. Two-phase API:
//
//   StampVoxels(ws, sub, anchor)         — voxel channels + entities
//   StampEnvOverrides(ws, sub, anchor)   — optional Wind/EnvTag overrides
//
// Phasing matters during WorldGen: voxels must land BEFORE the sunlight /
// wind / envtag default bake (so the bake sees the final geometry), while
// env overrides must land AFTER the default bake (so authored values like
// "this dungeon is a Tunnel regardless of wind" win over the bake's
// inferred tag). At runtime — after worldgen has finished — call
// StampAll() to do both back-to-back.
//
// Sunlight, BlockLight, and FogDensity are NOT touched here — sunlight
// and wind/envtag default bakes run at the end of WorldGen and resample
// the post-stamp geometry; block light rebuilds itself when torch entities
// spawn and register with LightEngine.
//
// Entity instances in `sub.Entities` are mutated (WorldPosition set in
// world-space) and added to the world directly. After StampVoxels, the
// supplied SubsceneState's entity list is empty — load a fresh state from
// disk if you need to stamp the same source again.
public static class SubsceneStamper
{
    public static void StampVoxels(WorldState ws, SubsceneState sub, Vector3 worldAnchor)
    {
        Vector3I worldOrigin = ComputeWorldOrigin(sub, worldAnchor);
        Vector3I size = sub.Size;

        for (int lx = 0; lx < size.X; lx++)
        {
            for (int ly = 0; ly < size.Y; ly++)
            {
                for (int lz = 0; lz < size.Z; lz++)
                {
                    if (!sub.PresenceMask[lx, ly, lz])
                    {
                        continue;
                    }
                    int wx = worldOrigin.X + lx;
                    int wy = worldOrigin.Y + ly;
                    int wz = worldOrigin.Z + lz;
                    ws.SetVoxelWorld(wx, wy, wz, sub.Voxels[lx, ly, lz], (VoxelTypeInfo.SharpAxes)sub.Shape[lx, ly, lz]);
                    ws.SetTerrainIdWorld(wx, wy, wz, sub.TerrainId[lx, ly, lz]);
                    ws.SetOverlayIdWorld(wx, wy, wz, sub.OverlayId[lx, ly, lz]);
                    ws.SetDetailGroupWorld(wx, wy, wz, sub.DetailGroup[lx, ly, lz]);
                    ws.SetDetailStrengthWorld(wx, wy, wz, sub.DetailStrength[lx, ly, lz]);
                }
            }
        }

        Vector3 worldOffset = new Vector3(
            worldAnchor.X - sub.Anchor.X,
            worldAnchor.Y - sub.Anchor.Y,
            worldAnchor.Z - sub.Anchor.Z);
        if (sub.Entities != null)
        {
            foreach (EntitySimState e in sub.Entities)
            {
                e.WorldPosition += worldOffset;
                // MobSimState carries a SpawnPosition that needs the same
                // translation so spawn-anchored behaviors (return-to-spawn,
                // burrow-from-spawn) work in the destination world.
                if (e is MobSimState mob)
                {
                    mob.SpawnPosition += worldOffset;
                }
                ws.AddEntity(e);
            }
            sub.Entities.Clear();
        }
    }

    public static void StampEnvOverrides(WorldState ws, SubsceneState sub, Vector3 worldAnchor)
    {
        if (sub.EnvTag == null)
        {
            return;
        }

        Vector3I worldOrigin = ComputeWorldOrigin(sub, worldAnchor);
        Vector3I size = sub.Size;
        Vector3I envSize = sub.EnvSize;

        const int S = ChunkState.ENV_VOXELS_PER_CELL;
        // World env-cell range overlapped by the bbox.
        int cellW0X = FloorDiv(worldOrigin.X, S);
        int cellW0Y = FloorDiv(worldOrigin.Y, S);
        int cellW0Z = FloorDiv(worldOrigin.Z, S);
        int cellW1X = FloorDiv(worldOrigin.X + size.X - 1, S);
        int cellW1Y = FloorDiv(worldOrigin.Y + size.Y - 1, S);
        int cellW1Z = FloorDiv(worldOrigin.Z + size.Z - 1, S);

        for (int cwx = cellW0X; cwx <= cellW1X; cwx++)
        {
            for (int cwy = cellW0Y; cwy <= cellW1Y; cwy++)
            {
                for (int cwz = cellW0Z; cwz <= cellW1Z; cwz++)
                {
                    // World voxel at this cell's center.
                    int vcx = cwx * S + S / 2;
                    int vcy = cwy * S + S / 2;
                    int vcz = cwz * S + S / 2;

                    // Subscene env-cell containing that center.
                    int lcx = FloorDiv(vcx - worldOrigin.X, S);
                    int lcy = FloorDiv(vcy - worldOrigin.Y, S);
                    int lcz = FloorDiv(vcz - worldOrigin.Z, S);
                    if (lcx < 0 || lcx >= envSize.X || lcy < 0 || lcy >= envSize.Y || lcz < 0 || lcz >= envSize.Z)
                    {
                        continue;
                    }

                    int chunkX = FloorDiv(cwx, ChunkState.ENV_SUBGRID_SIZE);
                    int chunkY = FloorDiv(cwy, ChunkState.ENV_SUBGRID_SIZE);
                    int chunkZ = FloorDiv(cwz, ChunkState.ENV_SUBGRID_SIZE);
                    ChunkState chunk = ws.GetChunk(new Vector3I(chunkX, chunkY, chunkZ));
                    if (chunk == null)
                    {
                        continue;
                    }
                    int dsx = Mod(cwx, ChunkState.ENV_SUBGRID_SIZE);
                    int dsy = Mod(cwy, ChunkState.ENV_SUBGRID_SIZE);
                    int dsz = Mod(cwz, ChunkState.ENV_SUBGRID_SIZE);

                    if (sub.EnvTag != null)
                    {
                        chunk.EnvTag[dsx, dsy, dsz] = sub.EnvTag[lcx, lcy, lcz];
                    }
                }
            }
        }
    }

    // Convenience for callers who don't need the two-phase ordering — does
    // voxels + entities, then env overrides immediately. Safe at runtime
    // because the default bakes have long since finished; not safe during
    // WorldGen.Generate (call StampVoxels then StampEnvOverrides at the
    // right phases instead).
    public static void StampAll(WorldState ws, SubsceneState sub, Vector3 worldAnchor)
    {
        StampVoxels(ws, sub, worldAnchor);
        StampEnvOverrides(ws, sub, worldAnchor);
    }

    public static void StampAll(WorldState ws, string path, Vector3 worldAnchor)
    {
        SubsceneState sub = SubsceneFile.Read(path);
        StampAll(ws, sub, worldAnchor);
    }

    private static Vector3I ComputeWorldOrigin(SubsceneState sub, Vector3 worldAnchor)
    {
        // Floor against the anchor so an integer-aligned anchor lands on
        // integer voxel boundaries even when the anchor is a midpoint
        // (e.g. 7.5 for a doorway centered between two voxels).
        return new Vector3I(
            Mathf.FloorToInt(worldAnchor.X - sub.Anchor.X),
            Mathf.FloorToInt(worldAnchor.Y - sub.Anchor.Y),
            Mathf.FloorToInt(worldAnchor.Z - sub.Anchor.Z));
    }

    private static int FloorDiv(int a, int b)
    {
        return (int)Math.Floor((double)a / b);
    }

    private static int Mod(int a, int m)
    {
        return ((a % m) + m) % m;
    }
}
