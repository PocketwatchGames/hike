using System;
using Godot;

// A rideable boat. Unlike land fixtures it must sit on water, and procedural
// terrain doesn't guarantee a pond at the anchor — so the entry ring-scans
// outward from the anchor for the nearest water-topped column and floats the
// boat there (origin riding the water surface). SelfPlaces so a SpawnGroupData
// hands it the anchor directly instead of rejecting it against the grassy
// scatter sampler. Skipped if no water is found within SearchRadius.
[GlobalClass]
public partial class BoatSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;
    // Ring-scan radius (in voxels) for the nearest water-topped column.
    [Export] public int searchRadius = 48;

    public override bool SelfPlaces => true;

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;
        int originX = Mathf.FloorToInt(position.X);
        int originZ = Mathf.FloorToInt(position.Z);

        for (int r = 1; r <= searchRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    // Boundary of the ring only — inner cells were covered by a
                    // smaller radius.
                    if (Mathf.Abs(dx) != r && Mathf.Abs(dz) != r) { continue; }
                    int bx = originX + dx;
                    int bz = originZ + dz;
                    if (bx < worldMinX || bx > worldMaxX || bz < worldMinZ || bz > worldMaxZ) { continue; }
                    // Wherever this column's water actually stands — a painted
                    // lake, a carved channel and the sea are all the same
                    // question, and none of them is guaranteed to sit at a
                    // fixed Y.
                    float? surfaceY = VoxelWater.TopOfColumn(ws, bx, bz);
                    if (!surfaceY.HasValue
                        || ws.GetBlockWorld(bx, Mathf.FloorToInt(surfaceY.Value), bz) != Blocks.AirId)
                    {
                        continue;
                    }
                    var boatPos = new Vector3(bx + 0.5f, surfaceY.Value, bz + 0.5f);
                    ws.AddEntity(new BoatSimState(boatPos, 0f, scene));
                    return;
                }
            }
        }
    }
}
