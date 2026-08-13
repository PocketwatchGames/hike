using System.Collections.Generic;
using Godot;

// Bakes ChunkState.Interiorness: how hard it is for the OUTDOORS to reach a
// cell. An aperture-weighted flood inward from open sky — cost accumulates per
// voxel travelled, and travelling through a narrow gap costs far more than
// crossing open space.
//
// This exists because every cheaper proxy failed on a case the game actually
// has. Sunlight can't work: it is a max-fill flood that ignores aperture SIZE,
// so a one-voxel window admits exactly as much as a missing wall, and a hole in
// a roof reads as open sky. Vertical cover alone can't work either: it cannot
// tell the strip under an eave from the middle of the room. What separates them
// is how WIDE the opening is and how far you must travel from it, which is
// precisely what this measures.
//
//   * under an eave      — one wide step from open air        → ~0
//   * doorway            — narrow aperture, cost jumps        → high beyond it
//   * one-voxel window   — very narrow, barely admits         → stays high
//   * broken roof        — holes are narrow apertures, so the room stays a
//                          room and softens roughly in proportion to how
//                          holed it is, with no special case for roofs
//   * deep cave          — saturates
//
// Run at worldgen (and captured into a .hikescene on save, so an authored
// interior survives stamping). Never re-derived at load: the bytes are
// serialized, and a painted value must round-trip.
public static class InteriornessGen
{
    // Neighbour offsets for the flood and the narrowness count.
    private static readonly Vector3I[] Neighbors =
    {
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, 1), new(0, 0, -1),
    };

    public static void Compute(WorldState ws)
    {
        var all = new HashSet<Vector3I>(ws._chunks.Keys);
        ComputeChunks(ws, all, all);
    }

    // Regional recompute, for an edit that changes cover over a known footprint
    // (a roof placed, deleted, moved or re-styled).
    //
    // Exact, not an approximation, and that rests on one property: cost
    // saturates at interiornessSaturationCost and every step costs at least 1,
    // so changing what a cell can reach only moves values within that many
    // voxels of travel — past it every path was already saturated. So the region
    // padded by the saturation cost is what must be REWRITTEN (the room under a
    // roof, not just its sheet of eave columns), and the flood needs that much
    // run-up again outside the write set to arrive there at the right cost, plus
    // a chunk because the write set is whole chunks.
    //
    // Everything past that is left alone, including any value a subscene stamp
    // or a paint brush authored — which a whole-world re-derive would silently
    // replace with the two-way worldgen default.
    //
    // Returns the chunks it rewrote, so the classification that reads this field
    // can be scoped to exactly the same set.
    public static HashSet<Vector3I> ComputeRegion(WorldState ws, VoxelBox region)
    {
        if (region.IsEmpty)
        {
            return new HashSet<Vector3I>();
        }
        int saturation = Mathf.Max(1, ws.SimData?.interiornessSaturationCost ?? 24);
        VoxelBox write = region.Expand(saturation);
        HashSet<Vector3I> writeChunks = ChunksIn(ws, write);
        ComputeChunks(ws, ChunksIn(ws, write.Expand(saturation + ChunkState.SIZE)), writeChunks);
        return writeChunks;
    }

    // `flood` bounds where cost can travel (SetCost fails outside it, so the
    // flood stops there); `write` is the subset whose Interiorness is rewritten.
    private static void ComputeChunks(WorldState ws, HashSet<Vector3I> flood, HashSet<Vector3I> write)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SimData simData = ws.SimData;
        int saturation = Mathf.Max(1, simData?.interiornessSaturationCost ?? 24);
        int narrowPenalty = Mathf.Max(0, simData?.interiornessNarrowPenalty ?? 3);
        float seedFraction = Mathf.Clamp(simData?.interiornessSeedSkyFraction ?? 0.9f, 0f, 1f);
        int seedSkyLevel = Mathf.RoundToInt(seedFraction * LightEngine.MAX_LIGHT);

        // Per-voxel travel cost, saturating. Kept in a side table rather than on
        // ChunkState: it is scratch for this pass and would otherwise be a
        // per-voxel channel nothing else reads.
        var cost = new Dictionary<Vector3I, byte[,,]>();
        foreach (Vector3I coord in flood)
        {
            ChunkState chunk = ws.GetChunk(coord);
            if (chunk == null)
            {
                continue;
            }
            var arr = new byte[ChunkState.SIZE, ChunkState.SIZE, ChunkState.SIZE];
            for (int x = 0; x < ChunkState.SIZE; x++)
            {
                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    for (int z = 0; z < ChunkState.SIZE; z++)
                    {
                        arr[x, y, z] = (byte)saturation;
                    }
                }
            }
            cost[chunk.ChunkCoord] = arr;
        }

        // Bucket queue — step costs are small integers, so this is O(n) where a
        // priority queue would be O(n log n) for no benefit.
        var buckets = new List<Vector3I>[saturation + 1];
        for (int i = 0; i <= saturation; i++)
        {
            buckets[i] = new List<Vector3I>();
        }

        // Seed: air that is essentially open to the sky IS the outdoors. A
        // voxel sitting in a roof hole seeds too, correctly — it genuinely is
        // outside. What stops the room below inheriting that is the narrowness
        // of the gap it has to come through, not any roof-specific rule.
        int seeded = 0;
        foreach (KeyValuePair<Vector3I, byte[,,]> kvp in cost)
        {
            Vector3I cc = kvp.Key;
            byte[,,] arr = kvp.Value;
            ChunkState chunk = ws.GetChunk(cc);
            for (int x = 0; x < ChunkState.SIZE; x++)
            {
                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    for (int z = 0; z < ChunkState.SIZE; z++)
                    {
                        if (!Blocks.IsEmpty(chunk.Voxels[x, y, z]))
                        {
                            continue;
                        }
                        if (chunk.SkyExposure[x, y, z] < seedSkyLevel)
                        {
                            continue;
                        }
                        arr[x, y, z] = 0;
                        buckets[0].Add(new Vector3I(
                            cc.X * ChunkState.SIZE + x,
                            cc.Y * ChunkState.SIZE + y,
                            cc.Z * ChunkState.SIZE + z));
                        seeded++;
                    }
                }
            }
        }

        for (int c = 0; c <= saturation; c++)
        {
            List<Vector3I> bucket = buckets[c];
            // Grows while being walked when a neighbour lands in this same
            // bucket (step cost can be 0 only if narrowPenalty is 0, but index
            // by count anyway so re-entry is safe).
            for (int i = 0; i < bucket.Count; i++)
            {
                Vector3I p = bucket[i];
                if (GetCost(cost, p) != c)
                {
                    continue; // superseded by a cheaper path already processed
                }
                for (int n = 0; n < Neighbors.Length; n++)
                {
                    Vector3I q = p + Neighbors[n];
                    // Reject on the CHEAP test first. Any step costs at least
                    // 1, so a neighbour already at or below this cost can never
                    // improve — and since nearly every voxel in an open world
                    // is a cost-0 seed whose neighbours are also cost-0 seeds,
                    // this is the difference between one dictionary lookup and
                    // seven for the overwhelming majority of edges. Computing
                    // narrowness before this test made the outdoors, which has
                    // no work to do at all, dominate the pass.
                    int existing = GetCost(cost, q);
                    if (existing <= c)
                    {
                        continue;
                    }
                    if (!IsOpen(ws, q))
                    {
                        continue;
                    }
                    int step = 1 + narrowPenalty * (Neighbors.Length - CountAirNeighbors(ws, q));
                    int next = c + step;
                    if (next >= saturation)
                    {
                        next = saturation;
                    }
                    if (next >= existing)
                    {
                        continue;
                    }
                    if (!SetCost(cost, q, (byte)next))
                    {
                        continue; // outside a resident chunk
                    }
                    buckets[next].Add(q);
                }
            }
            bucket.Clear();
        }

        // Aggregate to env cells over AIR ONLY. Solid voxels sit at max cost
        // because the flood never travels through them, so averaging them in
        // makes a cell read interior in proportion to how much WALL it holds —
        // which inverts the result exactly where it matters: the cell straddling
        // a doorframe is mostly wall and would read more enclosed than the
        // middle of the room it opens into. A cell with no air at all keeps the
        // saturated value; nothing stands in it, and a neighbouring room cell
        // blending against it should be pulled toward interior, not away.
        const int CELL = ChunkState.ENV_VOXELS_PER_CELL;
        foreach (Vector3I coord in write)
        {
            ChunkState chunk = ws.GetChunk(coord);
            if (chunk == null || !cost.TryGetValue(coord, out byte[,,] arr))
            {
                continue;
            }
            for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
            {
                for (int sy = 0; sy < ChunkState.ENV_SUBGRID_SIZE; sy++)
                {
                    for (int sz = 0; sz < ChunkState.ENV_SUBGRID_SIZE; sz++)
                    {
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
                                    if (!Blocks.IsEmpty(chunk.Voxels[lx, ly, lz]))
                                    {
                                        continue;
                                    }
                                    sum += arr[lx, ly, lz];
                                    airCount++;
                                }
                            }
                        }
                        int mean = airCount > 0 ? sum / airCount : saturation;
                        chunk.SetInteriorness(sx, sy, sz, mean * 255 / saturation);
                    }
                }
            }
        }
        // Regional passes run per editor edit; only the world-wide bake is worth
        // a line.
        if (write.Count == ws._chunks.Count)
        {
            GD.Print($"[InteriornessGen] seeds={seeded} saturation={saturation} narrowPenalty={narrowPenalty} {sw.ElapsedMilliseconds}ms");
        }
    }

    // Resident chunks the box overlaps.
    private static HashSet<Vector3I> ChunksIn(WorldState ws, VoxelBox box)
    {
        var chunks = new HashSet<Vector3I>();
        Vector3I min = VoxelBox.ChunkOf(box.Min);
        Vector3I max = VoxelBox.ChunkOf(box.Max);
        for (int cx = min.X; cx <= max.X; cx++)
        {
            for (int cy = min.Y; cy <= max.Y; cy++)
            {
                for (int cz = min.Z; cz <= max.Z; cz++)
                {
                    var coord = new Vector3I(cx, cy, cz);
                    if (ws.GetChunk(coord) != null)
                    {
                        chunks.Add(coord);
                    }
                }
            }
        }
        return chunks;
    }

    // Passable for the flood: air, and not covered by a non-voxel occluder.
    //
    // The SunOpaque half is essential and easy to miss — a roof is an ENTITY,
    // not a voxel, so a purely voxel-based test lets the outdoors pour straight
    // down through a cottage roof and the room reads as open field. Roofs mark
    // their cover into SunOpaque (one sheet at eave level), which is exactly the
    // barrier this pass needs, and it is punched through wherever the roof is
    // holed — so a broken roof admits the outdoors through its holes, at a cost
    // set by how wide they are, with no roof-specific code here.
    private static bool IsOpen(WorldState ws, Vector3I p)
    {
        return Blocks.IsEmpty(ws.GetBlockWorld(p.X, p.Y, p.Z))
            && !ws.GetSunOpaqueWorld(p.X, p.Y, p.Z);
    }

    // Open neighbours out of 6. Low counts mean a tight passage — a doorway, a
    // roof hole, a crack — and are what makes squeezing through expensive.
    private static int CountAirNeighbors(WorldState ws, Vector3I p)
    {
        int count = 0;
        for (int n = 0; n < Neighbors.Length; n++)
        {
            if (IsOpen(ws, p + Neighbors[n]))
            {
                count++;
            }
        }
        return count;
    }

    private static int GetCost(Dictionary<Vector3I, byte[,,]> cost, Vector3I p)
    {
        Vector3I cc = WorldToChunk(p);
        if (!cost.TryGetValue(cc, out byte[,,] arr))
        {
            return int.MaxValue;
        }
        return arr[Mod(p.X), Mod(p.Y), Mod(p.Z)];
    }

    private static bool SetCost(Dictionary<Vector3I, byte[,,]> cost, Vector3I p, byte value)
    {
        Vector3I cc = WorldToChunk(p);
        if (!cost.TryGetValue(cc, out byte[,,] arr))
        {
            return false;
        }
        arr[Mod(p.X), Mod(p.Y), Mod(p.Z)] = value;
        return true;
    }

    private static Vector3I WorldToChunk(Vector3I p)
    {
        return new Vector3I(
            (int)System.Math.Floor((double)p.X / ChunkState.SIZE),
            (int)System.Math.Floor((double)p.Y / ChunkState.SIZE),
            (int)System.Math.Floor((double)p.Z / ChunkState.SIZE));
    }

    private static int Mod(int v)
    {
        int m = v % ChunkState.SIZE;
        return m < 0 ? m + ChunkState.SIZE : m;
    }
}
