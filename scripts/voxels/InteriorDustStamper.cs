using Godot;

// Bakes each space class's dustFloor into the SERIALIZED fog field — the one
// air-density channel in the world.
//
// Writing FogDensity rather than a separate overlay is what makes the air a
// single system. FogDensity is read by three consumers that would otherwise
// need three mechanisms:
//   * LightEngine's column scan and block-light flood, which is what makes a
//     lantern genuinely dim in a dusty cave rather than only looking dim;
//   * the fog raymarch, so shafts have something to light;
//   * MoteEffect, so the particle field thickens indoors.
// An upload-time overlay reaches only the last two. This replaces both the old
// per-roof dust stamp and worldgen's separate cave-dust pass, which existed
// because neither the class system nor a shared channel did.
//
// MAX, never a reduction — authored mist already sitting in a cell must not be
// thinned by a class that happens to be drier.
//
// Runs after EnvTagGen (needs the class) and before ComputeSunlight (fog
// attenuates sun). Being baked rather than overlaid means a repainted cell
// needs a re-bake to take effect; that is EditorRefresh's job, the same batch
// that already relights and re-meshes.
public static class InteriorDustStamper
{
    public static void Bake(WorldState world)
    {
        SimData simData = world?.SimData;
        if (simData?.interiorAmbiences == null || simData.interiorAmbiences.Length == 0)
        {
            return;
        }

        // Resolve the palette to bytes once — the inner loop runs per voxel
        // across every resident chunk, and most classes carry no dust at all.
        var dustByIndex = new byte[simData.interiorAmbiences.Length];
        bool anyDust = false;
        for (int i = 0; i < dustByIndex.Length; i++)
        {
            InteriorAmbienceData data = simData.interiorAmbiences[i];
            if (data == null)
            {
                continue;
            }
            dustByIndex[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(data.dustFloor * 255f), 0, 255);
            anyDust |= dustByIndex[i] > 0;
        }
        if (!anyDust)
        {
            return;
        }

        const int CELL = ChunkState.ENV_VOXELS_PER_CELL;
        int cellsStamped = 0;
        foreach (ChunkState chunk in world._chunks.Values)
        {
            bool chunkChanged = false;
            for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
            {
                for (int sy = 0; sy < ChunkState.ENV_SUBGRID_SIZE; sy++)
                {
                    for (int sz = 0; sz < ChunkState.ENV_SUBGRID_SIZE; sz++)
                    {
                        int index = chunk.EnvTag[sx, sy, sz];
                        byte dust = index < dustByIndex.Length ? dustByIndex[index] : (byte)0;
                        if (dust == 0)
                        {
                            continue;
                        }
                        // Ramp by how COVERED the cell is, rather than stamping
                        // the class value flat. Classification is a threshold,
                        // so without this the fog field steps from nothing to
                        // full across one cell boundary — and AmbienceState
                        // samples fog at a SINGLE voxel to drive the reverb
                        // lowpass, so a flat stamp snaps the cutoff by over a
                        // kilohertz as you cross a cave mouth.
                        //
                        // Same job the old caveDustDepthFadeVoxels ramp did,
                        // from the signal classification already reads instead
                        // of a second depth measure of its own.
                        float covered = 1f - CellOpenness01(chunk, sx, sy, sz);
                        dust = (byte)(dust * covered);
                        if (dust == 0)
                        {
                            continue;
                        }
                        cellsStamped++;
                        // Air voxels only — dust is airborne, and leaving
                        // solids clear lets the fog volume's linear filter fall
                        // off across a wall face instead of hazing through it.
                        for (int x = 0; x < CELL; x++)
                        {
                            int lx = sx * CELL + x;
                            for (int y = 0; y < CELL; y++)
                            {
                                int ly = sy * CELL + y;
                                for (int z = 0; z < CELL; z++)
                                {
                                    int lz = sz * CELL + z;
                                    if (chunk.Voxels[lx, ly, lz] != VoxelType.Air)
                                    {
                                        continue;
                                    }
                                    if (dust > chunk.FogDensity[lx, ly, lz])
                                    {
                                        chunk.FogDensity[lx, ly, lz] = dust;
                                        chunkChanged = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            if (chunkChanged)
            {
                world.FogChunkDirty.Add(chunk.ChunkCoord);
            }
        }
        GD.Print($"[InteriorDustStamper] cells={cellsStamped}");
    }

    // Mean flooded sunlight over the cell's air voxels, normalized to [0,1].
    // The exact aggregate EnvTagGen thresholds to pick the class, reused here
    // as its continuous form — so the ramp and the classification can never
    // disagree about how enclosed a cell is. A cell one voxel inside a cave
    // mouth is nearly fully lit and takes almost no dust; one buried deep takes
    // all of it.
    private static float CellOpenness01(ChunkState chunk, int sx, int sy, int sz)
    {
        const int CELL = ChunkState.ENV_VOXELS_PER_CELL;
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
        if (airCount == 0)
        {
            return 0f;
        }
        return Mathf.Clamp(sum / (float)airCount / LightEngine.MAX_LIGHT, 0f, 1f);
    }
}
