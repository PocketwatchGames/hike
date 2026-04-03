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

    public static void ComputeLighting(WorldState world, List<(Vector3 position, int level)> blockLightSources)
    {
        ComputeSunlight(world);
        ComputeBlockLight(world, blockLightSources);
    }

    private static void ComputeSunlight(WorldState world)
    {
        int minWx = world.Min.X * ChunkState.SIZE;
        int maxWx = (world.Max.X + 1) * ChunkState.SIZE;
        int minWy = world.Min.Y * ChunkState.SIZE;
        int topWy = (world.Max.Y + 1) * ChunkState.SIZE - 1;
        int minWz = world.Min.Z * ChunkState.SIZE;
        int maxWz = (world.Max.Z + 1) * ChunkState.SIZE;

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

    private static void ComputeBlockLight(WorldState world, List<(Vector3 position, int level)> sources)
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

    private static void SpreadLight(WorldState world, Queue<(int x, int y, int z)> queue, bool isSunlight)
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

    /// <summary>
    /// Incremental light update after voxels change.
    /// Phase 1: BFS removal — zero out light that passed through the changed voxels.
    /// Phase 2: BFS fill — re-propagate from neighbors that still have light.
    /// </summary>
    public static void UpdateLighting(WorldState world, List<Vector3I> changedPositions)
    {
        UpdateChannel(world, changedPositions, isSunlight: true);
        UpdateChannel(world, changedPositions, isSunlight: false);
    }

    /// <summary>
    /// Propagate light outward from positions that already have a light value set.
    /// Use after placing a new light source.
    /// </summary>
    public static void PropagateLighting(WorldState world, List<Vector3I> sourcePositions)
    {
        var queue = new Queue<(int x, int y, int z)>();
        foreach (Vector3I pos in sourcePositions)
        {
            queue.Enqueue((pos.X, pos.Y, pos.Z));
        }
        SpreadLight(world, queue, isSunlight: false);
    }

    private static void UpdateChannel(WorldState world, List<Vector3I> changedPositions, bool isSunlight)
    {
        var removeQueue = new Queue<(int x, int y, int z, int level)>();
        var refillQueue = new Queue<(int x, int y, int z)>();

        foreach (Vector3I pos in changedPositions)
        {
            bool isNowAir = world.GetVoxelWorld(pos.X, pos.Y, pos.Z) == VoxelType.Air;
            int level = isSunlight
                ? world.GetSunlightWorld(pos.X, pos.Y, pos.Z)
                : world.GetBlockLightWorld(pos.X, pos.Y, pos.Z);

            if (isNowAir && level > 0)
            {
                // Air voxel whose light was reduced (e.g. light source removed) —
                // run removal BFS to clear propagated light, then refill.
                removeQueue.Enqueue((pos.X, pos.Y, pos.Z, level));
                if (isSunlight)
                {
                    world.SetSunlightWorld(pos.X, pos.Y, pos.Z, 0);
                }
                else
                {
                    world.SetBlockLightWorld(pos.X, pos.Y, pos.Z, 0);
                }
            }
            else if (isNowAir)
            {
                // Voxel became transparent — seed refill from lit neighbors
                foreach (Vector3I offset in Neighbors)
                {
                    int nx = pos.X + offset.X;
                    int ny = pos.Y + offset.Y;
                    int nz = pos.Z + offset.Z;

                    if (!world.IsInBounds(nx, ny, nz))
                    {
                        continue;
                    }

                    int neighborLevel = isSunlight
                        ? world.GetSunlightWorld(nx, ny, nz)
                        : world.GetBlockLightWorld(nx, ny, nz);

                    if (neighborLevel > 0)
                    {
                        refillQueue.Enqueue((nx, ny, nz));
                    }
                }
            }
            else if (level > 0)
            {
                // Voxel became solid — remove light that was passing through
                removeQueue.Enqueue((pos.X, pos.Y, pos.Z, level));
                if (isSunlight)
                {
                    world.SetSunlightWorld(pos.X, pos.Y, pos.Z, 0);
                }
                else
                {
                    world.SetBlockLightWorld(pos.X, pos.Y, pos.Z, 0);
                }
            }
        }

        // Phase 1: Removal BFS — zero out light downstream of newly-solid voxels
        while (removeQueue.Count > 0)
        {
            var (x, y, z, level) = removeQueue.Dequeue();

            foreach (Vector3I offset in Neighbors)
            {
                int nx = x + offset.X;
                int ny = y + offset.Y;
                int nz = z + offset.Z;

                if (!world.IsInBounds(nx, ny, nz))
                {
                    continue;
                }

                int neighborLevel = isSunlight
                    ? world.GetSunlightWorld(nx, ny, nz)
                    : world.GetBlockLightWorld(nx, ny, nz);

                if (neighborLevel > 0 && neighborLevel < level)
                {
                    removeQueue.Enqueue((nx, ny, nz, neighborLevel));
                    if (isSunlight)
                    {
                        world.SetSunlightWorld(nx, ny, nz, 0);
                    }
                    else
                    {
                        world.SetBlockLightWorld(nx, ny, nz, 0);
                    }
                }
                else if (neighborLevel >= level)
                {
                    refillQueue.Enqueue((nx, ny, nz));
                }
            }
        }

        // Phase 2: Re-propagate from remaining lit neighbors
        SpreadLight(world, refillQueue, isSunlight);
    }
}
