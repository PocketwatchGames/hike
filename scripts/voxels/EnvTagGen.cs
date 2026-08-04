using System.Collections.Generic;
using Godot;

// Classifies every env cell into a space class — an index into
// SimData.interiorAmbiences — as the default for procedural worlds. Cells open
// to the sky become outdoor; sheltered cells become the authored enclosed
// default.
//
// Reads Interiorness, the aperture-weighted flood baked by InteriornessGen.
// That field already answers "how enclosed is this", including every case a
// light-based measure got wrong (eaves, windows, roof holes), so all this pass
// does is pick a side of a threshold.
//
// Deliberately only decides WHICH class. How strongly the class applies rides
// the continuous interiorness value at sample time, so a cell landing just
// either side of the threshold is not a visible boundary — which is why the
// threshold can be a blunt number here.
//
// Only two classes come out of this, out of however many the palette holds:
// vertical cover is the one signal worldgen has, and it cannot tell a tidy
// hall from a dusty cellar. Everything finer is painted.
//
// Runs after InteriornessGen. Disk-loaded chunks skip it
// entirely and use their serialized bytes — a painted class must survive a
// round trip, so this is a first-generation default, never a re-derivation.
public static class EnvTagGen
{
    public static void ComputeEnvTagGrid(WorldState ws)
    {
        if (!TryResolveTuning(ws, out float threshold, out byte enclosed))
        {
            return;
        }
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

    // Re-derive named chunks only. An editor edit that changes cover changes
    // enclosure under it and nowhere else, and re-deriving the world would
    // overwrite authored classes everywhere with the two-way worldgen default.
    public static void ComputeEnvTagGrid(WorldState ws, IEnumerable<Vector3I> chunkCoords)
    {
        if (!TryResolveTuning(ws, out float threshold, out byte enclosed))
        {
            return;
        }
        foreach (Vector3I coord in chunkCoords)
        {
            ChunkState chunk = ws.GetChunk(coord);
            if (chunk == null) { continue; }
            ComputeChunkEnvTag(chunk, threshold, enclosed);
        }
    }

    private static bool TryResolveTuning(WorldState ws, out float threshold, out byte enclosed)
    {
        threshold = 0f;
        enclosed = 0;
        SimData simData = ws.SimData;
        if (simData == null)
        {
            return false;
        }
        // Index 0 is the palette's pinned outdoor entry; the enclosed default
        // is authored, so a world can ship "sealed means damp cellar" without
        // touching code.
        enclosed = (byte)simData.IndexOfInteriorAmbience(simData.worldgenEnclosedAmbience);
        threshold = simData.interiorEnclosureThreshold * 255f;
        return true;
    }

    public static void ComputeChunkEnvTag(ChunkState chunk, float threshold, byte enclosed)
    {
        for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
        {
            for (int sy = 0; sy < ChunkState.ENV_SUBGRID_SIZE; sy++)
            {
                for (int sz = 0; sz < ChunkState.ENV_SUBGRID_SIZE; sz++)
                {
                    bool open = chunk.GetInteriorness(sx, sy, sz) < threshold;
                    chunk.SetEnvTag(sx, sy, sz, open ? (byte)0 : enclosed);
                }
            }
        }
    }
}
