using System;
using System.Collections.Generic;
using Godot;

public static class LightEngine
{
    public const int MAX_LIGHT = 15;

    private static readonly Vector3I[] Neighbors =
    {
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, 1), new(0, 0, -1),
    };

    public static void ComputeLighting(WorldData world, List<(Vector3 position, int level)> blockLightSources)
    {
        ComputeSunlight(world);
        ComputeBlockLight(world, blockLightSources);
    }

    private static void ComputeSunlight(WorldData world)
    {
        int minWx = world.Min.X * ChunkData.SIZE;
        int maxWx = (world.Max.X + 1) * ChunkData.SIZE;
        int minWy = world.Min.Y * ChunkData.SIZE;
        int topWy = (world.Max.Y + 1) * ChunkData.SIZE - 1;
        int minWz = world.Min.Z * ChunkData.SIZE;
        int maxWz = (world.Max.Z + 1) * ChunkData.SIZE;

        var queue = new Queue<(int x, int y, int z)>();

        // Cast sunlight rays downward from the top of the world.
        // Sunlight propagates straight down with no decay.
        for (int wx = minWx; wx < maxWx; wx++)
        {
            for (int wz = minWz; wz < maxWz; wz++)
            {
                for (int wy = topWy; wy >= minWy; wy--)
                {
                    if (world.GetVoxelWorld(wx, wy, wz) != VoxelType.Air)
                    {
                        break;
                    }
                    world.SetSunlightWorld(wx, wy, wz, MAX_LIGHT);
                    queue.Enqueue((wx, wy, wz));
                }
            }
        }

        // BFS spread sunlight sideways (decays 1 per block)
        SpreadLight(world, queue, isSunlight: true);
    }

    private static void ComputeBlockLight(WorldData world, List<(Vector3 position, int level)> sources)
    {
        var queue = new Queue<(int x, int y, int z)>();

        foreach (var (pos, level) in sources)
        {
            int wx = Mathf.FloorToInt(pos.X);
            int wy = Mathf.FloorToInt(pos.Y);
            int wz = Mathf.FloorToInt(pos.Z);

            if (!world.IsInBounds(wx, wy, wz))
            {
                continue;
            }
            if (world.GetVoxelWorld(wx, wy, wz) != VoxelType.Air)
            {
                continue;
            }

            world.SetBlockLightWorld(wx, wy, wz, level);
            queue.Enqueue((wx, wy, wz));
        }

        SpreadLight(world, queue, isSunlight: false);
    }

    private static void SpreadLight(WorldData world, Queue<(int x, int y, int z)> queue, bool isSunlight)
    {
        while (queue.Count > 0)
        {
            var (x, y, z) = queue.Dequeue();
            int currentLevel = isSunlight
                ? world.GetSunlightWorld(x, y, z)
                : world.GetBlockLightWorld(x, y, z);

            if (currentLevel <= 1)
            {
                continue;
            }

            int newLevel = currentLevel - 1;

            foreach (Vector3I offset in Neighbors)
            {
                int nx = x + offset.X;
                int ny = y + offset.Y;
                int nz = z + offset.Z;

                if (!world.IsInBounds(nx, ny, nz))
                {
                    continue;
                }
                if (world.GetVoxelWorld(nx, ny, nz) != VoxelType.Air)
                {
                    continue;
                }

                int neighborLevel = isSunlight
                    ? world.GetSunlightWorld(nx, ny, nz)
                    : world.GetBlockLightWorld(nx, ny, nz);

                if (newLevel > neighborLevel)
                {
                    if (isSunlight)
                    {
                        world.SetSunlightWorld(nx, ny, nz, newLevel);
                    }
                    else
                    {
                        world.SetBlockLightWorld(nx, ny, nz, newLevel);
                    }
                    queue.Enqueue((nx, ny, nz));
                }
            }
        }
    }
}
