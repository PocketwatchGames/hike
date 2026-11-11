using System;
using System.Collections.Generic;
using Godot;

// Builds a SubsceneState's voxel body out of the scene editor's world.
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
                        if (chunk.Voxels[x, y, z] == Blocks.AirId)
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

    // Every voxel inside the bbox is marked present; the stamper still skips
    // plain air, so a scene never clears the space it lands in.
    //
    // The anchor's Y is the AUTHORING WORLD'S y=0 plane, not the bbox floor:
    // whatever you built at y=0 lands on the destination ground, and anything
    // below it stamps below — so a dungeon authored under y=0 embeds instead of
    // being lifted until its deepest voxel rests on the surface. X/Z stay at the
    // bbox min corner, which is what footprint placement measures from.
    //
    // Voxels and env only. The entities are the scene editor's placements,
    // which it adds itself — a world's states are never read back into a scene.
    //
    // includeEnv bakes Wind/EnvTag from the source chunks' subgrids — for
    // castles/dungeons that need to override the destination's ambience.
    // interiorClassOverride >= 0 rewrites every enclosed cell of the captured
    // EnvTag to that palette index — the scene-level "what kind of interior is
    // this" knob. Negative leaves the captured bytes alone.
    public static SubsceneState Build(WorldState ws, Vector3I min, Vector3I max, bool includeEnv, int interiorClassOverride = -1)
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
                    sub.Voxels[dx, dy, dz] = (byte)ws.GetBlockWorld(wx, wy, wz);
                    sub.Shape[dx, dy, dz] = (byte)ws.GetShapeWorld(wx, wy, wz);
                    sub.TerrainId[dx, dy, dz] = (byte)ws.GetTerrainIdWorld(wx, wy, wz);
                    sub.OverlayId[dx, dy, dz] = (byte)ws.GetOverlayIdWorld(wx, wy, wz);
                    sub.DetailGroup[dx, dy, dz] = (byte)ws.GetDetailGroupWorld(wx, wy, wz);
                    sub.DetailStrength[dx, dy, dz] = (byte)ws.GetDetailStrengthWorld(wx, wy, wz);
                    sub.PresenceMask[dx, dy, dz] = true;

                    int overlayFaces = ws.GetOverlayFacesWorld(wx, wy, wz);
                    if (overlayFaces != 0)
                    {
                        sub.EnsureOverlayFaces();
                        sub.OverlayFaces[dx, dy, dz] = (byte)overlayFaces;
                    }
                }
            }
        }

        if (includeEnv)
        {
            sub.EnsureEnvTag();
            BakeEnvFromWorld(ws, sub, min, interiorClassOverride);
        }

        sub.Anchor = new Vector3(0f, -min.Y, 0f);
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

    private static Vector3I ComponentMin(Vector3I a, Vector3I b)
    {
        return new Vector3I(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    }

    private static Vector3I ComponentMax(Vector3I a, Vector3I b)
    {
        return new Vector3I(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    }
}
