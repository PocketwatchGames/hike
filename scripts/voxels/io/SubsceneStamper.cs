using System;
using Godot;

// Stamps a SubsceneState into a WorldState. Two-phase API:
//
//   StampVoxels(ws, sub, anchor)         — voxel channels + entities
//   StampEnvOverrides(ws, sub, anchor)   — optional Wind/EnvTag overrides
//
// Phasing matters during WorldGen: voxels must land BEFORE the sunlight /
// wind / envtag default bake (so the bake sees the final geometry), while
// env overrides must land AFTER the default bake (so authored values like
// "this dungeon is a Tunnel regardless of wind" win over the bake's
// inferred tag). At runtime — after worldgen has finished — call
// StampAll() to do both back-to-back.
//
// Sunlight, BlockLight, and FogDensity are NOT touched here — sunlight
// and wind/envtag default bakes run at the end of WorldGen and resample
// the post-stamp geometry; block light rebuilds itself when torch entities
// spawn and register with LightEngine.
//
// TerrainId is the one channel not copied verbatim — a scene's natural ground
// INHERITS the ground it lands on. See SampleGroundTerrain.
//
// Entity instances in `sub.Entities` are mutated (WorldPosition set in
// world-space) and added to the world directly. After StampVoxels, the
// supplied SubsceneState's entity list is empty — load a fresh state from
// disk if you need to stamp the same source again.
public static class SubsceneStamper
{
    public static void StampVoxels(WorldState ws, SubsceneState sub, Vector3 worldAnchor)
    {
        Vector3I worldOrigin = ComputeWorldOrigin(sub, worldAnchor);
        Vector3I size = sub.Size;
        // Sampled before the first write — the stamp overwrites the very
        // voxels it inherits from.
        int[,] groundTerrain = SampleGroundTerrain(ws, worldOrigin, size);
        int footprintGround = MajorityGround(groundTerrain, size);

        for (int lx = 0; lx < size.X; lx++)
        {
            for (int ly = 0; ly < size.Y; ly++)
            {
                for (int lz = 0; lz < size.Z; lz++)
                {
                    if (!sub.PresenceMask[lx, ly, lz])
                    {
                        continue;
                    }
                    int wx = worldOrigin.X + lx;
                    int wy = worldOrigin.Y + ly;
                    int wz = worldOrigin.Z + lz;
                    ws.SetBlockWorld(wx, wy, wz, sub.Voxels[lx, ly, lz], (SharpAxes)sub.Shape[lx, ly, lz]);
                    ws.SetTerrainIdWorld(wx, wy, wz, ResolveTerrainId(sub, groundTerrain, footprintGround, lx, ly, lz));
                    ws.SetOverlayIdWorld(wx, wy, wz, sub.OverlayId[lx, ly, lz]);
                    ws.SetDetailGroupWorld(wx, wy, wz, sub.DetailGroup[lx, ly, lz]);
                    ws.SetDetailStrengthWorld(wx, wy, wz, sub.DetailStrength[lx, ly, lz]);
                    // Written unconditionally so a stamp CLEARS any face mask
                    // already on the destination voxel, the way every other
                    // channel here overwrites rather than merges.
                    ws.SetOverlayFacesWorld(wx, wy, wz, sub.OverlayFaces == null ? 0 : sub.OverlayFaces[lx, ly, lz]);
                }
            }
        }

        Vector3 worldOffset = WorldOffset(sub, worldAnchor);
        if (sub.Entities != null)
        {
            foreach (EntitySimState e in sub.Entities)
            {
                e.WorldPosition += worldOffset;
                // MobSimState carries a SpawnPosition that needs the same
                // translation so spawn-anchored behaviors (return-to-spawn,
                // burrow-from-spawn) work in the destination world.
                if (e is MobSimState mob)
                {
                    mob.SpawnPosition += worldOffset;
                }
                ws.AddEntity(e);
            }
            sub.Entities.Clear();
        }
    }

    // A TerrainId byte is a slot in the kit palette of the world the scene was
    // authored in. Palettes are built per WorldGenData by walking its zones, so
    // the same kit sits at a different slot in every world and a kit no zone
    // references has no slot at all — an out-of-range slot renders as bare
    // stone, because its shader uniform was never written.
    //
    // The authored byte is therefore discarded: a scene has no biome of its own,
    // so its natural ground inherits the ground it is stamped onto. The same
    // town square reads as mud in a swamp and grass in a forest, with nothing to
    // author per scene. Deliberate materials go in channels that ARE world-
    // independent — an explicit int (Stone, Marsh) or an OverlayId
    // (cobblestone, dirt), both of which name a block directly.
    private const int NO_GROUND = -1;

    // How far below the bbox floor to look for the ground being stamped onto.
    // Scenes anchor ON the plateau's top voxel, so the first sample usually
    // hits; the rest covers columns whose ground sits a step or two lower.
    private const int GROUND_SEARCH_DEPTH = 8;

    private static int ResolveTerrainId(SubsceneState sub, int[,] groundTerrain, int footprintGround, int lx, int ly, int lz)
    {
        int inherited = groundTerrain[lx, lz];
        if (inherited == NO_GROUND)
        {
            inherited = footprintGround;
        }
        // Nothing under any of it — a stamp into open air, or the editor's blank
        // workspace where the scene IS the world and its own byte is the only
        // index there is.
        return inherited != NO_GROUND ? inherited : sub.TerrainId[lx, ly, lz];
    }

    // Per-column TerrainId of the destination ground under the stamp's footprint,
    // NO_GROUND for a column with nothing solid within GROUND_SEARCH_DEPTH.
    private static int[,] SampleGroundTerrain(WorldState ws, Vector3I worldOrigin, Vector3I size)
    {
        var ground = new int[size.X, size.Z];
        for (int lx = 0; lx < size.X; lx++)
        {
            for (int lz = 0; lz < size.Z; lz++)
            {
                int wx = worldOrigin.X + lx;
                int wz = worldOrigin.Z + lz;
                ground[lx, lz] = NO_GROUND;
                for (int depth = 0; depth < GROUND_SEARCH_DEPTH; depth++)
                {
                    int wy = worldOrigin.Y - depth;
                    if (!Blocks.IsSolid(ws.GetBlockWorld(wx, wy, wz)))
                    {
                        continue;
                    }
                    ground[lx, lz] = ws.GetTerrainIdWorld(wx, wy, wz);
                    break;
                }
            }
        }
        return ground;
    }

    // What the footprint as a whole is standing on, for the columns that found
    // nothing — a scene overhanging a ledge or a pond takes the kit the rest of
    // it is sitting on rather than a palette slot nobody chose. NO_GROUND when
    // no column found ground at all.
    private static int MajorityGround(int[,] ground, Vector3I size)
    {
        var counts = new int[byte.MaxValue + 1];
        int best = NO_GROUND;
        int bestCount = 0;
        for (int lx = 0; lx < size.X; lx++)
        {
            for (int lz = 0; lz < size.Z; lz++)
            {
                int id = ground[lx, lz];
                if (id == NO_GROUND)
                {
                    continue;
                }
                if (++counts[id] > bestCount)
                {
                    bestCount = counts[id];
                    best = id;
                }
            }
        }
        return best;
    }

    // Subscene-local → world translation for a stamp at `worldAnchor`. Exposed
    // so a caller that pulls entities OUT of the list before stamping (WorldGen
    // consumes markers rather than placing them) lands them where the stamp
    // would have.
    public static Vector3 WorldOffset(SubsceneState sub, Vector3 worldAnchor)
    {
        return new Vector3(
            worldAnchor.X - sub.Anchor.X,
            worldAnchor.Y - sub.Anchor.Y,
            worldAnchor.Z - sub.Anchor.Z);
    }

    public static void StampEnvOverrides(WorldState ws, SubsceneState sub, Vector3 worldAnchor)
    {
        if (sub.EnvTag == null)
        {
            return;
        }

        Vector3I worldOrigin = ComputeWorldOrigin(sub, worldAnchor);
        Vector3I size = sub.Size;
        Vector3I envSize = sub.EnvSize;

        const int S = ChunkState.ENV_VOXELS_PER_CELL;
        // World env-cell range overlapped by the bbox.
        int cellW0X = FloorDiv(worldOrigin.X, S);
        int cellW0Y = FloorDiv(worldOrigin.Y, S);
        int cellW0Z = FloorDiv(worldOrigin.Z, S);
        int cellW1X = FloorDiv(worldOrigin.X + size.X - 1, S);
        int cellW1Y = FloorDiv(worldOrigin.Y + size.Y - 1, S);
        int cellW1Z = FloorDiv(worldOrigin.Z + size.Z - 1, S);

        for (int cwx = cellW0X; cwx <= cellW1X; cwx++)
        {
            for (int cwy = cellW0Y; cwy <= cellW1Y; cwy++)
            {
                for (int cwz = cellW0Z; cwz <= cellW1Z; cwz++)
                {
                    // World voxel at this cell's center.
                    int vcx = cwx * S + S / 2;
                    int vcy = cwy * S + S / 2;
                    int vcz = cwz * S + S / 2;

                    // Subscene env-cell containing that center.
                    int lcx = FloorDiv(vcx - worldOrigin.X, S);
                    int lcy = FloorDiv(vcy - worldOrigin.Y, S);
                    int lcz = FloorDiv(vcz - worldOrigin.Z, S);
                    if (lcx < 0 || lcx >= envSize.X || lcy < 0 || lcy >= envSize.Y || lcz < 0 || lcz >= envSize.Z)
                    {
                        continue;
                    }

                    int chunkX = FloorDiv(cwx, ChunkState.ENV_SUBGRID_SIZE);
                    int chunkY = FloorDiv(cwy, ChunkState.ENV_SUBGRID_SIZE);
                    int chunkZ = FloorDiv(cwz, ChunkState.ENV_SUBGRID_SIZE);
                    ChunkState chunk = ws.GetChunk(new Vector3I(chunkX, chunkY, chunkZ));
                    if (chunk == null)
                    {
                        continue;
                    }
                    int dsx = Mod(cwx, ChunkState.ENV_SUBGRID_SIZE);
                    int dsy = Mod(cwy, ChunkState.ENV_SUBGRID_SIZE);
                    int dsz = Mod(cwz, ChunkState.ENV_SUBGRID_SIZE);

                    if (sub.EnvTag != null)
                    {
                        chunk.EnvTag[dsx, dsy, dsz] = sub.EnvTag[lcx, lcy, lcz];
                    }
                }
            }
        }
    }

    // Convenience for callers who don't need the two-phase ordering — does
    // voxels + entities, then env overrides immediately. Safe at runtime
    // because the default bakes have long since finished; not safe during
    // WorldGen.Generate (call StampVoxels then StampEnvOverrides at the
    // right phases instead).
    public static void StampAll(WorldState ws, SubsceneState sub, Vector3 worldAnchor)
    {
        StampVoxels(ws, sub, worldAnchor);
        StampEnvOverrides(ws, sub, worldAnchor);
    }

    public static void StampAll(WorldState ws, string path, Vector3 worldAnchor)
    {
        SubsceneState sub = SubsceneFile.Read(path);
        StampAll(ws, sub, worldAnchor);
    }

    // World voxel the subscene's local (0,0,0) lands on for a stamp at
    // `worldAnchor` — i.e. the bbox min corner. Public because callers that
    // measure the stamped volume before the stamp (undo capture, entity
    // eviction) need the same answer the writes will use.
    public static Vector3I ComputeWorldOrigin(SubsceneState sub, Vector3 worldAnchor)
    {
        // Floor against the anchor so an integer-aligned anchor lands on
        // integer voxel boundaries even when the anchor is a midpoint
        // (e.g. 7.5 for a doorway centered between two voxels).
        return new Vector3I(
            Mathf.FloorToInt(worldAnchor.X - sub.Anchor.X),
            Mathf.FloorToInt(worldAnchor.Y - sub.Anchor.Y),
            Mathf.FloorToInt(worldAnchor.Z - sub.Anchor.Z));
    }

    private static int FloorDiv(int a, int b)
    {
        return (int)Math.Floor((double)a / b);
    }

    private static int Mod(int a, int m)
    {
        return ((a % m) + m) % m;
    }
}
