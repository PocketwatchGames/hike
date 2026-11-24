using System;
using System.Collections.Generic;
using Godot;

// `debug_mesh_reach` — rebuilds every loaded chunk's geometry through recording
// accessors and reports how far outside the chunk the build actually read, per
// side, against ChunkMeshReach's declaration. An edit re-meshes exactly the
// chunks whose declared reach covers it, so a mesher that starts reading
// further than declared leaves stale seams with nothing else to say so.
//
// Measured reach is a lower bound on the true one (a march stops at the first
// blocker, so the worst case needs the worst geometry), which is why the
// declaration is derived from the meshers' own constants and this only checks
// it. The water column probe is excluded: it reads up a water run without
// bound and is covered separately (ChunkMeshReach.AddWaterRunBelow).
public static class MeshReachProbe
{
    private const int N = ChunkState.SIZE;

    public static void Run()
    {
        Sim sim = Sim.Current;
        if (sim?.ChunkManager == null)
        {
            GD.PrintErr("debug_mesh_reach: no running game.");
            return;
        }
        WorldState world = sim.WorldState;
        ChunkMeshReach.Resolve(out Vector3I declaredBelow, out Vector3I declaredAbove);

        var below = Vector3I.Zero;
        var above = Vector3I.Zero;
        Vector3I worstBelowChunk = Vector3I.Zero;
        Vector3I worstAboveChunk = Vector3I.Zero;
        int chunks = 0;
        var coords = new List<Vector3I>(sim.ChunkManager.LoadedChunkCoords);
        foreach (Vector3I coord in coords)
        {
            ChunkState data = world.GetChunk(coord);
            if (data == null)
            {
                continue;
            }
            Vector3I origin = coord * N;
            var lo = Vector3I.Zero;
            var hi = new Vector3I(N - 1, N - 1, N - 1);
            // Water's column probe is recognized by what it reads: a cell
            // above the chunk whose column below it, down to the chunk top, is
            // all water. Everything else counts.
            void Note(int x, int y, int z)
            {
                int ly = y - origin.Y;
                if (ly > N - 1 && IsWaterBelowTo(world, x, y, z, origin.Y + N - 1))
                {
                    return;
                }
                lo = lo.Min(new Vector3I(x - origin.X, ly, z - origin.Z));
                hi = hi.Max(new Vector3I(x - origin.X, ly, z - origin.Z));
            }
            int GetBlock(int x, int y, int z) { Note(x, y, z); return world.GetBlockWorld(x, y, z); }
            SharpAxes GetShape(int x, int y, int z) { Note(x, y, z); return world.GetShapeWorld(x, y, z); }
            int GetTerrain(int x, int y, int z) { Note(x, y, z); return world.GetTerrainIdWorld(x, y, z); }
            int GetOverlay(int x, int y, int z) { Note(x, y, z); return world.GetOverlayIdWorld(x, y, z); }
            int GetSun(int x, int y, int z) { Note(x, y, z); return world.GetSunlightWorld(x, y, z); }
            bool GetSunOpaque(int x, int y, int z) { Note(x, y, z); return world.GetSunOpaqueWorld(x, y, z); }
            void CollectLips(Vector3I min, Vector3I max, List<ClimbLip> output)
            {
                Note(min.X, min.Y, min.Z);
                Note(max.X, max.Y, max.Z);
                world.CollectClimbLipsWorld(min, max, output);
            }

            ChunkMesh.BuildGeometry(data, GetBlock, GetShape, GetTerrain, GetOverlay, GetSun, GetSunOpaque, world.IsInBounds, CollectLips,
                buildCollision: true, buildDetails: true, outOfLightWindow: false);
            chunks++;

            Vector3I chunkBelow = -lo;
            Vector3I chunkAbove = hi - new Vector3I(N - 1, N - 1, N - 1);
            if (chunkBelow.X + chunkBelow.Y + chunkBelow.Z > below.X + below.Y + below.Z)
            {
                worstBelowChunk = coord;
            }
            if (chunkAbove.X + chunkAbove.Y + chunkAbove.Z > above.X + above.Y + above.Z)
            {
                worstAboveChunk = coord;
            }
            below = below.Max(chunkBelow);
            above = above.Max(chunkAbove);
        }

        bool ok = below.X <= declaredBelow.X && below.Y <= declaredBelow.Y && below.Z <= declaredBelow.Z
            && above.X <= declaredAbove.X && above.Y <= declaredAbove.Y && above.Z <= declaredAbove.Z;
        GD.Print($"[mesh_reach] {chunks} chunks. measured below {below} (worst {worstBelowChunk}), above {above} (worst {worstAboveChunk}); declared below {declaredBelow}, above {declaredAbove}");
        if (ok)
        {
            GD.Print("[mesh_reach] ok");
        }
        else
        {
            GD.PrintErr("[mesh_reach] FAIL: a chunk build read past ChunkMeshReach — edits near a seam will leave the neighbour's mesh stale. Widen the declaration from the mesher that grew.");
        }
    }

    private static bool IsWaterBelowTo(WorldState world, int x, int y, int z, int floorY)
    {
        for (int wy = y - 1; wy >= floorY; wy--)
        {
            if (!Blocks.IsWater(world.GetBlockWorld(x, wy, z)))
            {
                return false;
            }
        }
        return true;
    }
}
