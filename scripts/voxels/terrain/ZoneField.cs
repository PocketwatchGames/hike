using System;
using Godot;

// Blended per-column scalars sampled from the per-zone ZoneGenData at a column.
// Tunnel/cave thresholds are XZ-only so a single struct serves callers that
// walk the column at any Y.
public struct BlendedZoneGen
{
    // Per-zone authored center elevation, kernel-blended at sample time.
    public float Elevation;
    public float ElevationRange;
    public float GrassThreshold;

    // Per-zone monster and forge difficulty bands, kernel-blended so they
    // crossfade across zone borders. The level samplers lerp between each pair
    // by their own (independent) level-noise field.
    public float MobLevelMin;
    public float MobLevelMax;
    // Same, for spawns with a ceiling overhead (caves, tunnels).
    public float UndergroundMobLevelMin;
    public float UndergroundMobLevelMax;
    public float ForgeLevelMin;
    public float ForgeLevelMax;

    // Flatten override (flattenSurface zones). FlattenWeight is the summed
    // weight of flattening zones at this column (0..1); FlattenLevel is the
    // weight-scaled sum of their targets. An approach pulls its height toward
    // FlattenLevel by FlattenWeight, so a village core sits dead flat while its
    // edge blends back into the surrounding terrain.
    public float FlattenWeight;
    public float FlattenLevel;
}

// WHERE THIS WORLD'S ZONES ARE, and how they blend — one object per generate,
// built from the authored WorldGenData and handed to everything that asks.
//
// This was six mutable statics on WorldGen (`_activeGenData`, `_zoneBoundsContext`
// and the blend radii read off them). That made the answer process-global: it
// outlived the run that set it, it was never cleared, terrain plugins and spawn
// entries read it without holding it, and the world-map painter's BACKGROUND
// bake could read whichever world a previous Generate happened to leave behind.
// A run is an object now, so none of that is expressible.
//
// The kernel mirrors ZoneBlend.Sample: reach `blendRadius` chunks out from a
// column and weight each chunk's zone by its smoothstep falloff. ChunkZoneIndex
// still returns one zone per chunk for ChunkState.ZoneIndex (gameplay needs a
// single value), but the worldgen scalars blend smoothly across chunk borders so
// a desert→forest transition is not a hard line.
public sealed class ZoneField
{
    // Zone PLACEMENT (bounds + priority) and zone TUNING are parallel arrays
    // indexed alike — `zones[i]` says where zone i is, `Gens[i]` says what it
    // generates.
    private readonly PlacedZone[] _placed;

    // The world extent / spawn chunk / edge noise each PlacedZone's bounds are
    // evaluated against. Public because the fixture and POI passes ask a
    // specific zone's bounds directly rather than through the kernel.
    public readonly ZoneBoundsContext Bounds;

    // Soft scalar fades (elevation, density) use this reach; kit-identity
    // stamps use the tighter one so out-of-biome kit bleed stays near the seam.
    // KitBlendRadius must stay >= 1.0 or corner voxels get zero weight and
    // PickKitZone falls back to a chunk-aligned hard seam, the exact thing the
    // kernel exists to avoid.
    private readonly float _blendRadius;
    private readonly float _kitBlendRadius;

    public readonly ZoneGenData[] Gens;

    public int Count => Gens != null ? Gens.Length : 0;

    // Per-voxel salt for the kit-border hash. Distinct from any other hash salt
    // so kit borders don't correlate with future per-voxel decisions.
    private const int KIT_HASH_SALT = 0x4B495454; // "KITT"

    public ZoneField(WorldGenData genData, ZoneBoundsContext bounds)
    {
        Gens = genData?.ZoneGens ?? Array.Empty<ZoneGenData>();
        _placed = genData?.zones;
        Bounds = bounds;
        _blendRadius = genData?.finish?.zoneGenBlendRadius ?? 2.0f;
        _kitBlendRadius = genData?.kitBlendRadius ?? 2.0f;
    }

    // The single zone a CHUNK belongs to — highest-priority authored bounds
    // containing it. This is what ChunkState.ZoneIndex stores.
    public byte ChunkZoneIndex(Vector3I chunkCoord, int zoneCount)
    {
        if (zoneCount <= 0 || _placed == null) { return 0; }

        int best = -1;
        int bestPriority = int.MinValue;
        int n = Math.Min(zoneCount, _placed.Length);
        for (int i = 0; i < n; i++)
        {
            ZoneBounds bounds = _placed[i]?.bounds;
            if (bounds == null) { continue; }
            if (bounds.priority <= bestPriority) { continue; }
            if (bounds.Contains(chunkCoord.X, chunkCoord.Z, Bounds))
            {
                best = i;
                bestPriority = bounds.priority;
            }
        }
        return best >= 0 ? (byte)best : (byte)0;
    }

    // `weights` Span must be sized to zoneCount. Output sums to 1 (or all zeros
    // if no neighbour has a valid zone — caller's choice what to do about that).
    public void Weights(int wx, int wz, int zoneCount, Span<float> weights)
    {
        Weights(wx, wz, zoneCount, weights, _blendRadius);
    }

    public void Weights(int wx, int wz, int zoneCount, Span<float> weights, float blendRadius)
    {
        for (int i = 0; i < zoneCount; i++) { weights[i] = 0f; }
        if (zoneCount <= 0) { return; }

        int chunkX = (int)Math.Floor((double)wx / ChunkState.SIZE);
        int chunkZ = (int)Math.Floor((double)wz / ChunkState.SIZE);
        int half = Mathf.CeilToInt(blendRadius);

        for (int dx = -half; dx <= half; dx++)
        {
            for (int dz = -half; dz <= half; dz++)
            {
                int cx = chunkX + dx;
                int cz = chunkZ + dz;
                int zoneIdx = ChunkZoneIndex(new Vector3I(cx, 0, cz), zoneCount);
                float chunkCenterX = (cx + 0.5f) * ChunkState.SIZE;
                float chunkCenterZ = (cz + 0.5f) * ChunkState.SIZE;
                float dxw = wx - chunkCenterX;
                float dzw = wz - chunkCenterZ;
                float distChunks = Mathf.Sqrt(dxw * dxw + dzw * dzw) / ChunkState.SIZE;
                float w = Mathf.SmoothStep(blendRadius, 0f, distChunks);
                if (w > 0f) { weights[zoneIdx] += w; }
            }
        }

        float total = 0f;
        for (int i = 0; i < zoneCount; i++) { total += weights[i]; }
        if (total > 1e-6f)
        {
            float inv = 1f / total;
            for (int i = 0; i < zoneCount; i++) { weights[i] *= inv; }
        }
    }

    public BlendedZoneGen SampleBlended(int wx, int wz)
    {
        return SampleBlended(wx, wz, Span<float>.Empty);
    }

    // As above, and ALSO writes the per-zone kernel weights into weightsOut so
    // the caller can blend fields this struct knows nothing about. That is how a
    // terrain approach folds its own per-zone knobs without this struct growing
    // a field per approach — and without paying for the weight solve twice,
    // which is the whole reason it is an out-parameter rather than a second
    // public call.
    public BlendedZoneGen SampleBlended(int wx, int wz, Span<float> weightsOut)
    {
        var result = new BlendedZoneGen();
        int n = Count;
        if (n == 0) { return result; }

        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        Weights(wx, wz, n, weights);

        // Per-zone terrain blend reach, keyed off the DOMINANT zone: a zone that
        // authors a tighter TerrainBlendChunks holds its own terrain across its
        // whole footprint instead of letting a neighbour bleed ~blendRadius
        // chunks in. Asymmetric on purpose — the village stays a flat, dry beach
        // up to its edge while the swamp around it keeps blending softly with
        // the mud/highlands. Only recompute when the dominant zone overrides the
        // global radius, so ordinary columns pay nothing.
        if (_placed != null)
        {
            int dom = -1;
            float bestW = 0f;
            for (int i = 0; i < n; i++)
            {
                if (weights[i] > bestW) { bestW = weights[i]; dom = i; }
            }
            float reach = dom >= 0 && dom < _placed.Length
                ? (_placed[dom]?.bounds?.terrainBlendChunks ?? 0f)
                : 0f;
            if (reach > 0f)
            {
                Weights(wx, wz, n, weights, reach);
            }
        }

        if (!weightsOut.IsEmpty && weightsOut.Length >= n)
        {
            weights.Slice(0, n).CopyTo(weightsOut);
        }

        for (int i = 0; i < n; i++)
        {
            float w = weights[i];
            if (w <= 0f) { continue; }
            ZoneGenData rg = Gens[i];
            if (rg == null) { continue; }
            // Shared terrain scalars come off the zone's terrain sub-resource; a
            // zone that has not been given one blends the base defaults rather
            // than dropping out of the sum and skewing its neighbours' weight.
            ZoneTerrainData zt = rg.terrain;
            result.Elevation += (zt?.elevation ?? 0f) * w;
            result.ElevationRange += (zt?.elevationRange ?? 2f) * w;
            result.GrassThreshold += rg.grassThreshold * w;
            result.MobLevelMin += rg.mobLevelMin * w;
            result.MobLevelMax += rg.mobLevelMax * w;
            result.UndergroundMobLevelMin += rg.undergroundMobLevelMin * w;
            result.UndergroundMobLevelMax += rg.undergroundMobLevelMax * w;
            result.ForgeLevelMin += rg.forgeLevelMin * w;
            result.ForgeLevelMax += rg.forgeLevelMax * w;
            if (zt != null && zt.flattenSurface)
            {
                result.FlattenWeight += w;
                result.FlattenLevel += zt.flattenLevel * w;
            }
        }
        return result;
    }

    // Sample a zone index at a column weighted by the same kernel that drives
    // scalar blending. Use this for prop / mob scene picks: in the overlap band
    // between two zones each prop independently rolls which palette to draw
    // from, so a forest→desert seam reads as a few desert trees among forest
    // pines rather than a hard line at the chunk boundary. Returns -1 if no zone
    // has positive weight (caller skips the spawn).
    public int PickWeighted(int wx, int wz, Random rng)
    {
        return PickWeighted(wx, wz, rng, _blendRadius);
    }

    // As above but with an explicit kernel reach — the spawn pass passes the
    // dominant zone's SpawnBlendReachChunks so each zone controls how far its
    // content blends across its own border (a wider reach = a wider, softer
    // mixing band; the caller uses the crisp dominant zone when reach is 0).
    public int PickWeighted(int wx, int wz, Random rng, float blendRadius)
    {
        int n = Count;
        if (n == 0) { return -1; }

        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        Weights(wx, wz, n, weights, blendRadius);

        float total = 0f;
        for (int i = 0; i < n; i++) { total += weights[i]; }
        if (total <= 1e-6f) { return -1; }

        float r = (float)rng.NextDouble() * total;
        float acc = 0f;
        for (int i = 0; i < n; i++)
        {
            acc += weights[i];
            if (r <= acc) { return i; }
        }
        return n - 1;
    }

    // Convenience: kernel-weighted zone pick that returns the data resource (or
    // null). Caller reads chance/scene/data fields off it for the spawn roll.
    public ZoneGenData PickWeightedData(int wx, int wz, Random rng)
    {
        int idx = PickWeighted(wx, wz, rng);
        return idx < 0 ? null : Gens[idx];
    }

    // Index of the single highest-weight zone at a column — the biome it
    // actually sits in, not a weighted roll. The default for content placement
    // (so nothing bleeds across a border) and the base the per-zone
    // SpawnBlendReachChunks softens. Returns -1 if no zone has positive weight.
    public int DominantIndex(int wx, int wz)
    {
        int n = Count;
        if (n == 0) { return -1; }
        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        Weights(wx, wz, n, weights);
        int best = -1;
        float bestW = 0f;
        for (int i = 0; i < n; i++)
        {
            if (weights[i] > bestW)
            {
                bestW = weights[i];
                best = i;
            }
        }
        return best;
    }

    // Same weighted pick as PickWeighted but driven by a precomputed [0, 1)
    // sample. For deterministic per-voxel kit assignment we want jagged zone
    // borders that follow the kernel weights — a hash of the voxel's column
    // gives a stable noisy boundary instead of the chunk-aligned orthogonal seam
    // `chunk.ZoneIndex` would give.
    public int PickWeightedFromHash(int wx, int wz, float r01, float blendRadius)
    {
        int n = Count;
        if (n == 0) { return -1; }

        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        Weights(wx, wz, n, weights, blendRadius);

        float total = 0f;
        for (int i = 0; i < n; i++) { total += weights[i]; }
        if (total <= 1e-6f) { return -1; }

        float r = r01 * total;
        float acc = 0f;
        for (int i = 0; i < n; i++)
        {
            acc += weights[i];
            if (r <= acc) { return i; }
        }
        return n - 1;
    }

    // Pick a zone for a kit stamp at a column. Falls back to the chunk's
    // ZoneIndex when the kernel produces no positive weight (off-world, edge
    // cases) so we always end up with a stamped kit.
    public int PickKitZone(int wx, int wz, int fallbackZoneIndex)
    {
        int idx = PickWeightedFromHash(wx, wz,
            TerrainMath.HashFloat01(wx, wz, KIT_HASH_SALT), _kitBlendRadius);
        return idx >= 0 ? idx : fallbackZoneIndex;
    }

    // The surface kit of the highest-weight zone at a column, or null. Uses the
    // tight kit reach, since it answers a kit-identity question.
    public TerrainKitData DominantSurfaceKit(int wx, int wz)
    {
        int n = Count;
        if (n == 0) { return null; }
        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        Weights(wx, wz, n, weights, _kitBlendRadius);
        int best = -1;
        float bestW = 0f;
        for (int i = 0; i < n; i++)
        {
            if (weights[i] > bestW)
            {
                bestW = weights[i];
                best = i;
            }
        }
        return best >= 0 ? Gens[best]?.surfaceKit : null;
    }
}
