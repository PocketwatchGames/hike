using Godot;

// Classifies every env cell into a space class — an index into
// SimData.interiorAmbiences — as the default for procedural worlds. Cells open
// to the sky become outdoor; sheltered cells become the authored enclosed
// default.
//
// Reads the FLOODED Sunlight field, because enclosure is not a vertical
// question. Sunlight's BFS spreads sideways at ~4 per voxel, so light reaches
// anywhere with a lateral opening — and that is exactly what separates "inside
// the walls" from "under cover but out in the open".
//
// The eave case is why. RoofSunStamper marks a roof's FULL extent sun-opaque,
// overhang included, and correctly so: an eave really does block sun and
// shelter you from rain. But a purely vertical field (SkyExposure) then reads
// the strip under the eave exactly like the middle of the room, and a cottage's
// interior ambience leaks out past its walls. Flooded sunlight doesn't: open
// ground a metre away floods the eave strip, while walls stop it dead.
//
// NOT WindFactor, though WindGen derives from the same Sunlight. A space class
// carries windSuppression which WindGen applies while baking, so classifying
// off wind would mean wind picks the class and the class then damps wind.
// Reading Sunlight directly keeps enclosure → class → wind one-directional.
//
// Roofs reach this through the same path a voxel ceiling does — SunOpaque —
// so a roof marks an area interior exactly as a cave roof does. It never
// chooses WHICH class, which is why nothing here is roof-aware.
//
// Only two classes come out of this, out of however many the palette holds:
// vertical cover is the one signal worldgen has, and it cannot tell a tidy
// hall from a dusty cellar. Everything finer is painted.
//
// Runs after the first ComputeSunlight and before WindGen. Disk-loaded chunks skip it
// entirely and use their serialized bytes — a painted class must survive a
// round trip, so this is a first-generation default, never a re-derivation.
public static class EnvTagGen
{
    public static void ComputeEnvTagGrid(WorldState ws)
    {
        SimData simData = ws.SimData;
        if (simData == null)
        {
            return;
        }
        // Index 0 is the palette's pinned outdoor entry; the enclosed default
        // is authored, so a world can ship "sealed means damp cellar" without
        // touching code.
        byte enclosed = (byte)simData.IndexOfInteriorAmbience(simData.worldgenEnclosedAmbience);
        float threshold = simData.interiorEnclosureThreshold * LightEngine.MAX_LIGHT;

        for (int cz = ws.Min.Z; cz <= ws.Max.Z; cz++)
        {
            for (int cy = ws.Min.Y; cy <= ws.Max.Y; cy++)
            {
                for (int cx = ws.Min.X; cx <= ws.Max.X; cx++)
                {
                    ChunkState chunk = ws.GetChunk(new Vector3I(cx, cy, cz));
                    if (chunk == null) { continue; }
                    ComputeChunkEnvTag(chunk, threshold, enclosed);
                }
            }
        }
    }

    public static void ComputeChunkEnvTag(ChunkState chunk, float threshold, byte enclosed)
    {
        const int CELL = ChunkState.ENV_VOXELS_PER_CELL;
        for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
        {
            for (int sy = 0; sy < ChunkState.ENV_SUBGRID_SIZE; sy++)
            {
                for (int sz = 0; sz < ChunkState.ENV_SUBGRID_SIZE; sz++)
                {
                    // Mean over the cell's AIR voxels. Solid voxels carry no
                    // light, so including them would drag any cell holding a
                    // wall toward interior; a cell that is ENTIRELY solid stays
                    // interior on purpose, since a room's walls should read as
                    // part of the room when a neighbouring cell samples them.
                    int sum = 0;
                    int airCount = 0;
                    for (int x = 0; x < CELL; x++)
                    {
                        for (int y = 0; y < CELL; y++)
                        {
                            for (int z = 0; z < CELL; z++)
                            {
                                int lx = sx * CELL + x;
                                int ly = sy * CELL + y;
                                int lz = sz * CELL + z;
                                if (chunk.Voxels[lx, ly, lz] != VoxelType.Air)
                                {
                                    continue;
                                }
                                sum += chunk.Sunlight[lx, ly, lz];
                                airCount++;
                            }
                        }
                    }
                    bool open = airCount > 0 && (sum / (float)airCount) > threshold;
                    chunk.SetEnvTag(sx, sy, sz, open ? (byte)0 : enclosed);
                }
            }
        }
    }
}
