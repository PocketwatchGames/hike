using System;
using System.Collections.Generic;
using Godot;

// Builds a SubsceneState out of a live WorldState. Shared by the editor's
// save path and the headless world→subscene conversion, so both produce
// identical files from the same world.
public static class SubsceneBuilder
{
    // Bbox of every non-air voxel in the world (water counts — it's painted
    // like anything else). False when the world is empty. Walks every resident
    // chunk, so authoring-only — never call it on a streamed world.
    public static bool TryGetContentBounds(WorldState ws, out Vector3I min, out Vector3I max)
    {
        min = default;
        max = default;
        bool any = false;
        foreach (KeyValuePair<Vector3I, ChunkState> kvp in ws._chunks)
        {
            Vector3I origin = kvp.Key * ChunkState.SIZE;
            ChunkState chunk = kvp.Value;
            for (int x = 0; x < ChunkState.SIZE; x++)
            {
                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    for (int z = 0; z < ChunkState.SIZE; z++)
                    {
                        if (chunk.Voxels[x, y, z] == VoxelType.Air)
                        {
                            continue;
                        }
                        var voxel = new Vector3I(origin.X + x, origin.Y + y, origin.Z + z);
                        min = any ? ComponentMin(min, voxel) : voxel;
                        max = any ? ComponentMax(max, voxel) : voxel;
                        any = true;
                    }
                }
            }
        }
        return any;
    }

    // Every voxel inside the bbox is marked present (= it overwrites the
    // destination on stamp), so enclosed air overwrites too. Anchor is left at
    // (0,0,0) — the bbox min corner.
    //
    // filterEntitiesToBox keeps only the entities standing inside the bbox,
    // for carving one piece out of a larger world. Otherwise every entity in
    // the world comes along: subscene entities are a flat list translated by
    // the stamp anchor, not grid cells, so one outside the bbox stamps fine.
    //
    // includeEnv bakes Wind/EnvTag from the source chunks' subgrids — for
    // castles/dungeons that need to override the destination's ambience.
    // interiorClassOverride >= 0 rewrites every enclosed cell of the captured
    // EnvTag to that palette index — the scene-level "what kind of interior is
    // this" knob. Negative leaves the captured bytes alone.
    public static SubsceneState Build(WorldState ws, Vector3I min, Vector3I max, bool includeEnv, bool filterEntitiesToBox, int interiorClassOverride = -1)
    {
        Vector3I size = max - min + new Vector3I(1, 1, 1);
        var sub = new SubsceneState(size);
        for (int dx = 0; dx < size.X; dx++)
        {
            for (int dy = 0; dy < size.Y; dy++)
            {
                for (int dz = 0; dz < size.Z; dz++)
                {
                    int wx = min.X + dx;
                    int wy = min.Y + dy;
                    int wz = min.Z + dz;
                    sub.Voxels[dx, dy, dz] = ws.GetVoxelWorld(wx, wy, wz);
                    sub.Shape[dx, dy, dz] = (byte)ws.GetShapeWorld(wx, wy, wz);
                    sub.TerrainId[dx, dy, dz] = (byte)ws.GetTerrainIdWorld(wx, wy, wz);
                    sub.OverlayId[dx, dy, dz] = (byte)ws.GetOverlayIdWorld(wx, wy, wz);
                    sub.DetailGroup[dx, dy, dz] = (byte)ws.GetDetailGroupWorld(wx, wy, wz);
                    sub.DetailStrength[dx, dy, dz] = (byte)ws.GetDetailStrengthWorld(wx, wy, wz);
                    sub.PresenceMask[dx, dy, dz] = true;
                }
            }
        }

        if (includeEnv)
        {
            sub.EnsureEnvTag();
            BakeEnvFromWorld(ws, sub, min, interiorClassOverride);
        }

        sub.Entities = filterEntitiesToBox
            ? CollectEntitiesInBox(ws, min, max, size)
            : CollectAllEntities(ws, min);
        sub.Anchor = Vector3.Zero;
        return sub;
    }

    private static void BakeEnvFromWorld(WorldState ws, SubsceneState sub, Vector3I worldOrigin, int interiorClassOverride)
    {
        const int S = ChunkState.ENV_VOXELS_PER_CELL;
        Vector3I envSize = sub.EnvSize;
        for (int lcx = 0; lcx < envSize.X; lcx++)
        {
            for (int lcy = 0; lcy < envSize.Y; lcy++)
            {
                for (int lcz = 0; lcz < envSize.Z; lcz++)
                {
                    // Subscene env-cell center → world voxel center → world env-cell.
                    int vcx = worldOrigin.X + lcx * S + S / 2;
                    int vcy = worldOrigin.Y + lcy * S + S / 2;
                    int vcz = worldOrigin.Z + lcz * S + S / 2;
                    int cwx = (int)Math.Floor((double)vcx / S);
                    int cwy = (int)Math.Floor((double)vcy / S);
                    int cwz = (int)Math.Floor((double)vcz / S);
                    int chunkX = (int)Math.Floor((double)cwx / ChunkState.ENV_SUBGRID_SIZE);
                    int chunkY = (int)Math.Floor((double)cwy / ChunkState.ENV_SUBGRID_SIZE);
                    int chunkZ = (int)Math.Floor((double)cwz / ChunkState.ENV_SUBGRID_SIZE);
                    ChunkState chunk = ws.GetChunk(new Vector3I(chunkX, chunkY, chunkZ));
                    if (chunk == null)
                    {
                        continue;
                    }
                    int sx = ((cwx % ChunkState.ENV_SUBGRID_SIZE) + ChunkState.ENV_SUBGRID_SIZE) % ChunkState.ENV_SUBGRID_SIZE;
                    int sy = ((cwy % ChunkState.ENV_SUBGRID_SIZE) + ChunkState.ENV_SUBGRID_SIZE) % ChunkState.ENV_SUBGRID_SIZE;
                    int sz = ((cwz % ChunkState.ENV_SUBGRID_SIZE) + ChunkState.ENV_SUBGRID_SIZE) % ChunkState.ENV_SUBGRID_SIZE;
                    byte envTag = chunk.EnvTag[sx, sy, sz];
                    // Index 0 is outdoor and is left alone — the scene's
                    // open-air margin should keep taking the destination's
                    // ambience rather than being declared an interior.
                    if (interiorClassOverride >= 0 && envTag != 0)
                    {
                        envTag = (byte)interiorClassOverride;
                    }
                    sub.EnvTag[lcx, lcy, lcz] = envTag;
                }
            }
        }
    }

    private static List<EntitySimState> CollectAllEntities(WorldState ws, Vector3I min)
    {
        var all = new List<EntitySimState>();
        foreach (EntitySimState e in ws.AllChunkEntities())
        {
            all.Add(e);
        }
        return CloneToLocal(all, min);
    }

    // Walk every chunk overlapping the bbox and collect the EntitySimStates
    // inside it.
    private static List<EntitySimState> CollectEntitiesInBox(WorldState ws, Vector3I min, Vector3I max, Vector3I size)
    {
        var inside = new List<EntitySimState>();
        Vector3I cMin = new Vector3I(
            (int)Math.Floor((double)min.X / ChunkState.SIZE),
            (int)Math.Floor((double)min.Y / ChunkState.SIZE),
            (int)Math.Floor((double)min.Z / ChunkState.SIZE));
        Vector3I cMax = new Vector3I(
            (int)Math.Floor((double)max.X / ChunkState.SIZE),
            (int)Math.Floor((double)max.Y / ChunkState.SIZE),
            (int)Math.Floor((double)max.Z / ChunkState.SIZE));
        for (int cx = cMin.X; cx <= cMax.X; cx++)
        {
            for (int cy = cMin.Y; cy <= cMax.Y; cy++)
            {
                for (int cz = cMin.Z; cz <= cMax.Z; cz++)
                {
                    List<EntitySimState> chunkEntities = ws.GetEntities(new Vector3I(cx, cy, cz));
                    if (chunkEntities == null)
                    {
                        continue;
                    }
                    foreach (EntitySimState e in chunkEntities)
                    {
                        Vector3 p = e.WorldPosition;
                        if (p.X >= min.X && p.X < min.X + size.X
                            && p.Y >= min.Y && p.Y < min.Y + size.Y
                            && p.Z >= min.Z && p.Z < min.Z + size.Z)
                        {
                            inside.Add(e);
                        }
                    }
                }
            }
        }

        return CloneToLocal(inside, min);
    }

    // Deep-clone, then translate into subscene-local space. Cloning avoids
    // mutating the source world's entities.
    private static List<EntitySimState> CloneToLocal(List<EntitySimState> source, Vector3I min)
    {
        List<EntitySimState> clones = EntitySerializer.CloneList(source);
        Vector3 offset = new Vector3(-min.X, -min.Y, -min.Z);
        foreach (EntitySimState clone in clones)
        {
            clone.WorldPosition += offset;
            if (clone is MobSimState mob)
            {
                mob.SpawnPosition += offset;
            }
        }
        return clones;
    }

    private static Vector3I ComponentMin(Vector3I a, Vector3I b)
    {
        return new Vector3I(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    }

    private static Vector3I ComponentMax(Vector3I a, Vector3I b)
    {
        return new Vector3I(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    }
}
