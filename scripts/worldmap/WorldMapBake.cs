using System.Collections.Generic;
using Godot;

// Deterministic bake: WorldMapData + its layer images -> WorldState (-> .hike).
// The elevation image REPLACES WorldGen's noise-derived height field; the rest
// of WorldGen's per-column logic (ramps, shore, tunnels, kit blending) is out
// of v1 scope, so this is a clean focused stamp rather than a fork of the
// 3100-line WorldGen. The region image stamps per-chunk RegionIndex.
//
// Same write seam the in-game WorldEditor uses: SetVoxelWorld into chunks that
// must already exist (SetVoxelWorld no-ops on a missing chunk), then the caller
// drives UpdateLighting / RebuildNearbyChunkMeshes for incremental edits.
public static class WorldMapBake
{
    // Surface kit = palette index 0 (BuildKitPalette puts zone 0's SurfaceKit
    // first), which is also the per-voxel TerrainId default, so the stamp never
    // has to set TerrainId explicitly in v1.
    private const byte SURFACE_TERRAIN_ID = 0;

    // Full bake from scratch: build the WorldState, create every chunk, stamp
    // all columns + regions, and propagate sunlight. Used for the initial
    // preview and for the headless .hike export.
    public static WorldState Build(WorldMapData data, Image elevation, Image region)
    {
        var ws = new WorldState(data.MinChunk, data.MaxChunk, data.GenData.SimData);

        // Mirror WorldGen/CreateEmptyWorld zone + region setup so sky/zone
        // blend and the region banner have something to read.
        ZoneGenData[] zones = data.GenData.Zones ?? [];
        ws.Zones = new ZoneState[zones.Length];
        for (int i = 0; i < zones.Length; i++)
        {
            ws.Zones[i] = new ZoneState
            {
                Data = zones[i]?.Zone,
                WindDirection = new Vector3(0.7f, 0f, 0.7f),
                Elevation = 0f,
            };
        }
        RegionData[] regions = data.GenData.Regions ?? [];
        ws.Regions = new RegionState[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            ws.Regions[i] = new RegionState { Data = regions[i] };
        }

        // Create every chunk and stamp its region index up front.
        for (int cx = data.MinChunk.X; cx <= data.MaxChunk.X; cx++)
        {
            for (int cy = data.MinChunk.Y; cy <= data.MaxChunk.Y; cy++)
            {
                for (int cz = data.MinChunk.Z; cz <= data.MaxChunk.Z; cz++)
                {
                    var coord = new Vector3I(cx, cy, cz);
                    var chunk = new ChunkState(coord);
                    chunk.RegionIndex = SampleRegion(data, region, cx, cz);
                    ws._chunks[coord] = chunk;
                }
            }
        }

        StampColumns(ws, data, elevation, new Rect2I(0, 0, data.ImageWidth, data.ImageHeight), null);

        // Spawn over the centre column, just above its surface.
        int spawnPx = -data.WorldMinX;
        int spawnPz = -data.WorldMinZ;
        int spawnH = data.ColumnHeight(SamplePixel(elevation, spawnPx, spawnPz));
        ws.Spawn = new Vector3(0.5f, spawnH + 2f, 0.5f);

        LightEngine.ComputeSunlight(ws);
        return ws;
    }

    // Re-stamp the columns under a painted elevation rect (texel coords),
    // recording every voxel that actually changed so the caller can relight +
    // remesh just that band.
    public static void RebakeElevation(WorldState ws, WorldMapData data, Image elevation, Rect2I pixelRect, List<Vector3I> changed)
    {
        StampColumns(ws, data, elevation, pixelRect, changed);
    }

    // Re-stamp RegionIndex for a painted chunk rect (chunk-texel coords). No
    // voxel/light/mesh change — region is metadata; the in-world tint overlay
    // (future) would remesh, but v1 only updates persisted data + the 2D map.
    public static void RebakeRegion(WorldState ws, WorldMapData data, Image region, Rect2I chunkRect)
    {
        int x0 = Mathf.Max(0, chunkRect.Position.X);
        int z0 = Mathf.Max(0, chunkRect.Position.Y);
        int x1 = Mathf.Min(data.SizeChunksX, chunkRect.Position.X + chunkRect.Size.X);
        int z1 = Mathf.Min(data.SizeChunksZ, chunkRect.Position.Y + chunkRect.Size.Y);
        for (int lcx = x0; lcx < x1; lcx++)
        {
            for (int lcz = z0; lcz < z1; lcz++)
            {
                int cx = data.MinChunk.X + lcx;
                int cz = data.MinChunk.Z + lcz;
                byte idx = (byte)Mathf.RoundToInt(region.GetPixel(lcx, lcz).R * 255f);
                for (int cy = data.MinChunk.Y; cy <= data.MaxChunk.Y; cy++)
                {
                    ChunkState chunk = ws.GetChunk(new Vector3I(cx, cy, cz));
                    if (chunk != null)
                    {
                        chunk.RegionIndex = idx;
                    }
                }
            }
        }
    }

    private static void StampColumns(WorldState ws, WorldMapData data, Image elevation, Rect2I pixelRect, List<Vector3I> changed)
    {
        int x0 = Mathf.Max(0, pixelRect.Position.X);
        int z0 = Mathf.Max(0, pixelRect.Position.Y);
        int x1 = Mathf.Min(data.ImageWidth, pixelRect.Position.X + pixelRect.Size.X);
        int z1 = Mathf.Min(data.ImageHeight, pixelRect.Position.Y + pixelRect.Size.Y);
        int yFloor = data.WorldMinY;
        int yCeil = data.WorldMaxY;
        int sea = data.SeaLevel;

        for (int px = x0; px < x1; px++)
        {
            for (int pz = z0; pz < z1; pz++)
            {
                int wx = data.WorldMinX + px;
                int wz = data.WorldMinZ + pz;
                int height = data.ColumnHeight(SamplePixel(elevation, px, pz));

                for (int wy = yFloor; wy <= yCeil; wy++)
                {
                    VoxelType desired = wy <= height
                        ? VoxelType.Terrain
                        : (wy <= sea ? VoxelType.Water : VoxelType.Air);

                    if (changed != null)
                    {
                        if (ws.GetVoxelWorld(wx, wy, wz) == desired)
                        {
                            continue;
                        }
                        changed.Add(new Vector3I(wx, wy, wz));
                    }

                    if (desired == VoxelType.Terrain)
                    {
                        ws.SetVoxelWorld(wx, wy, wz, VoxelType.Terrain, VoxelTypeInfo.SharpAxes.Y);
                    }
                    else
                    {
                        ws.SetVoxelWorld(wx, wy, wz, desired);
                    }
                }
            }
        }
    }

    private static float SamplePixel(Image img, int px, int pz)
    {
        int x = Mathf.Clamp(px, 0, img.GetWidth() - 1);
        int z = Mathf.Clamp(pz, 0, img.GetHeight() - 1);
        return img.GetPixel(x, z).R;
    }

    private static byte SampleRegion(WorldMapData data, Image region, int cx, int cz)
    {
        int lcx = cx - data.MinChunk.X;
        int lcz = cz - data.MinChunk.Z;
        if (lcx < 0 || lcx >= region.GetWidth() || lcz < 0 || lcz >= region.GetHeight())
        {
            return 0;
        }
        return (byte)Mathf.RoundToInt(region.GetPixel(lcx, lcz).R * 255f);
    }
}
