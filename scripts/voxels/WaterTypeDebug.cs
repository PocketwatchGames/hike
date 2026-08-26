using System;
using System.Collections.Generic;
using Godot;

// Diagnostic: what water the LOADED world is actually made of.
// Console: `water_type_probe`.
//
// Exists because "I see no scum" has three unrelated causes and the rendered
// surface cannot tell them apart: nothing was painted here, the water is real
// but hundreds of metres away, or the data is right and the shader is at fault.
// This reports the stamped blocks straight out of WorldState AND reads CUSTOM0
// back out of the real mesher, so a disagreement localizes the fault.
//
// Reads the chunk dictionary rather than walking the world box: the whole world
// is resident, and this is thousands of chunks rather than millions of columns.
public static class WaterTypeDebug
{
    public static void Dump()
    {
        WorldState ws = Sim.Current?.WorldState;
        if (ws == null)
        {
            GD.Print("[water_type] no world loaded");
            return;
        }

        long freeSurface = 0;
        var counts = new Dictionary<int, long>();
        var minY = new Dictionary<int, int>();
        var maxY = new Dictionary<int, int>();

        Player player = Sim.Current?.player;
        Vector3 p = player != null ? player.GlobalPosition : Vector3.Zero;
        float nearestSq = float.MaxValue;
        Vector3I nearest = Vector3I.Zero;
        int nearestBlock = 0;

        foreach (ChunkState chunk in ws._chunks.Values)
        {
            int baseX = chunk.ChunkCoord.X * ChunkState.SIZE;
            int baseY = chunk.ChunkCoord.Y * ChunkState.SIZE;
            int baseZ = chunk.ChunkCoord.Z * ChunkState.SIZE;
            for (int x = 0; x < ChunkState.SIZE; x++)
            {
                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    for (int z = 0; z < ChunkState.SIZE; z++)
                    {
                        int id = chunk.Voxels[x, y, z];
                        if (!Blocks.IsWater(id))
                        {
                            continue;
                        }
                        int wx = baseX + x, wy = baseY + y, wz = baseZ + z;
                        int above = ws.GetBlockWorld(wx, wy + 1, wz);
                        if (Blocks.IsWater(above) || Blocks.IsSolid(above))
                        {
                            continue;
                        }
                        freeSurface++;
                        counts.TryGetValue(id, out long c);
                        counts[id] = c + 1;
                        minY[id] = minY.TryGetValue(id, out int lo) ? Math.Min(lo, wy) : wy;
                        maxY[id] = maxY.TryGetValue(id, out int hi) ? Math.Max(hi, wy) : wy;

                        if (id == Blocks.DefaultWaterId)
                        {
                            continue;
                        }
                        float dx = wx + 0.5f - p.X, dz = wz + 0.5f - p.Z;
                        float dSq = dx * dx + dz * dz;
                        if (dSq < nearestSq)
                        {
                            nearestSq = dSq;
                            nearest = new Vector3I(wx, wy, wz);
                            nearestBlock = id;
                        }
                    }
                }
            }
        }

        GD.Print($"[water_type] {ws._chunks.Count} chunks: {freeSurface} free-surface water voxels (sea level {TerrainMath.SEA_LEVEL})");
        foreach (KeyValuePair<int, long> kv in counts)
        {
            BlockData b = BlockCatalog.Active.GetById(kv.Key);
            string film = b?.waterFilm?.filmName?.ToString() ?? "bare";
            GD.Print($"[water_type]   {kv.Key} {b?.blockName} (film {film}, turbidity {Blocks.WaterTurbidity(kv.Key):+0.00;-0.00;0}): {kv.Value}, y {minY[kv.Key]}..{maxY[kv.Key]}");
        }
        if (nearestSq == float.MaxValue)
        {
            GD.Print("[water_type]   every body is standard water. Water type is PAINTED — "
                + "use the world-map painter's Water tool, then re-bake; nothing derives it.");
        }
        else if (player != null)
        {
            GD.Print($"[water_type] nearest non-standard water: {nearest} block {nearestBlock}, {Mathf.Sqrt(nearestSq):F1}m away");
        }

        // Read CUSTOM0 back out of the REAL mesher. The channel is packed by hand
        // and nothing else checks it: a wrong lane costs the film silently, which
        // on screen is indistinguishable from "there is no film here".
        if (nearestSq < float.MaxValue)
        {
            var cc = new Vector3I(
                (int)Mathf.Floor(nearest.X / (float)ChunkState.SIZE),
                (int)Mathf.Floor(nearest.Y / (float)ChunkState.SIZE),
                (int)Mathf.Floor(nearest.Z / (float)ChunkState.SIZE));
            ChunkState chunk = ws.GetChunk(cc);
            if (chunk != null)
            {
                var buf = new MeshBuffer(1);
                WaterMesher.Build(chunk, ws.GetBlockWorld, buf,
                    cc.X * ChunkState.SIZE, cc.Y * ChunkState.SIZE, cc.Z * ChunkState.SIZE, out bool any);
                ArrayMesh mesh = any ? buf.ToArrayMesh(null) : null;
                if (mesh == null)
                {
                    GD.Print($"[water_type] chunk {cc} meshed no water — cannot check CUSTOM0");
                }
                else
                {
                    float[] c0 = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
                    var seen = new Dictionary<int, int>();
                    for (int i = 0; i + 3 < c0.Length; i += 4)
                    {
                        int id = Mathf.RoundToInt(c0[i]);
                        seen.TryGetValue(id, out int c);
                        seen[id] = c + 1;
                    }
                    GD.Print($"[water_type] CUSTOM0.x in chunk {cc}, {c0.Length / 4} verts:");
                    foreach (KeyValuePair<int, int> kv in seen)
                    {
                        BlockData b = BlockCatalog.Active.GetById(kv.Key);
                        GD.Print($"[water_type]   block {kv.Key} {b?.blockName}: {kv.Value} verts");
                    }
                }
            }
        }

    }
}
