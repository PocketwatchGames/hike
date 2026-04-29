using System;
using Godot;

// Samples a blended RegionData + WeatherData at a world XZ position.
// The world's chunks each carry a RegionIndex into WorldState.Regions[];
// Sample() looks at the chunks within a few-chunk radius around the
// player and weights each by a smoothstep falloff on its distance to
// the player. Region contributions are accumulated by index, then
// blended into the output.
//
// The blend kernel reaches BlendRadiusChunks chunks out, so the cross-
// blend BAND between two adjacent regions is wider than a single chunk
// — the goal is a soft, several-chunk-wide transition rather than a
// hard line at chunk boundaries.
//
// Sample() stays as the only seam between region authoring and
// downstream consumers. Once the editor lets you paint arbitrary region
// shapes, only the chunk → region map changes; nothing here moves.
public static class RegionBlend
{
    // Half-width of the smoothstep kernel, in chunks. The kernel reaches
    // out this far in chunk-distance from the player; beyond this, a
    // chunk's region contributes 0. Larger = softer / wider transition.
    public const float BlendRadiusChunks = 2.0f;

    // Sentinel "no region picked" weight — anything below this aborts
    // the blend and the caller's current outputs are left untouched.
    private const float MinTotalWeight = 1e-6f;

    // Blend the regions present in `ws.Regions` according to which
    // chunks around the player belong to which region. Writes into the
    // caller-owned outRegion + outWeather (working copies held by
    // SkyController so we never allocate Resources on the hot path) and
    // returns the blended runtime fields via out parameters. If the
    // world has no regions or no chunks in the kernel, outputs are left
    // at whatever the caller had.
    public static void Sample(
        Vector3 playerWorldPos, WorldState ws,
        RegionData outRegion, WeatherData outWeather,
        out Vector3 outWindDirection, out float outElevation)
    {
        outWindDirection = new Vector3(1f, 0f, 0f);
        outElevation = 0f;
        if (ws == null || ws.Regions == null || ws.Regions.Length == 0) { return; }
        if (outRegion == null || outWeather == null) { return; }

        int regionCount = ws.Regions.Length;
        Span<float> weights = regionCount <= 32 ? stackalloc float[regionCount] : new float[regionCount];
        if (!ComputeWeights(playerWorldPos, ws, weights)) { return; }

        // --- Region theme + scalars (RegionData) ---
        float sunR = 0, sunG = 0, sunB = 0, sunA = 0;
        float moonR = 0, moonG = 0, moonB = 0, moonA = 0;
        float skyR = 0, skyG = 0, skyB = 0, skyA = 0;
        float dustR = 0, dustG = 0, dustB = 0, dustA = 0;
        float waterR = 0, waterG = 0, waterB = 0, waterA = 0;
        float dustAmount = 0, waterOpacity = 0;
        float themeWeightSum = 0;

        for (int i = 0; i < regionCount; i++)
        {
            float w = weights[i];
            if (w <= 0f) { continue; }
            RegionData rd = ws.Regions[i].Data;
            if (rd == null) { continue; }
            themeWeightSum += w;
            AccumulateColor(rd.SunColor, w, ref sunR, ref sunG, ref sunB, ref sunA);
            AccumulateColor(rd.MoonColor, w, ref moonR, ref moonG, ref moonB, ref moonA);
            AccumulateColor(rd.SkyColor, w, ref skyR, ref skyG, ref skyB, ref skyA);
            AccumulateColor(rd.DustColor, w, ref dustR, ref dustG, ref dustB, ref dustA);
            AccumulateColor(rd.WaterColor, w, ref waterR, ref waterG, ref waterB, ref waterA);
            dustAmount += rd.DustAmount * w;
            waterOpacity += rd.WaterOpacity * w;
        }

        if (themeWeightSum >= MinTotalWeight)
        {
            float themeInv = 1f / themeWeightSum;
            outRegion.SunColor = new Color(sunR * themeInv, sunG * themeInv, sunB * themeInv, sunA * themeInv);
            outRegion.MoonColor = new Color(moonR * themeInv, moonG * themeInv, moonB * themeInv, moonA * themeInv);
            outRegion.SkyColor = new Color(skyR * themeInv, skyG * themeInv, skyB * themeInv, skyA * themeInv);
            outRegion.DustColor = new Color(dustR * themeInv, dustG * themeInv, dustB * themeInv, dustA * themeInv);
            outRegion.WaterColor = new Color(waterR * themeInv, waterG * themeInv, waterB * themeInv, waterA * themeInv);
            outRegion.DustAmount = dustAmount * themeInv;
            outRegion.WaterOpacity = waterOpacity * themeInv;
        }

        // --- Runtime fields (RegionState) ---
        // windDirection blends via vector sum of the regions' XZ unit
        // vectors and is re-normalized at the end (shortest-arc blend).
        Vector2 windDir2D = Vector2.Zero;
        float elevation = 0f;
        for (int i = 0; i < regionCount; i++)
        {
            float w = weights[i];
            if (w <= 0f) { continue; }
            RegionState rs = ws.Regions[i];
            windDir2D += SafeNormalizeXZ(rs.WindDirection) * w;
            elevation += rs.Elevation * w;
        }
        if (windDir2D.LengthSquared() > MinTotalWeight)
        {
            windDir2D = windDir2D.Normalized();
            outWindDirection = new Vector3(windDir2D.X, 0f, windDir2D.Y);
        }
        outElevation = Mathf.Clamp(elevation, 0f, 1f);

        // --- Weather (WeatherData) ---
        BlendWeather(ws, weights, outWeather);
    }

    // Per-region normalized weights at `playerWorldPos`. Same kernel as
    // Sample() but without the palette/weather blend — for callers that
    // need to drive parallel per-region pipelines (audio: one bus of
    // layer players per region, mixed by these weights). `outWeights`
    // length must equal ws.Regions.Length. Returns false if the world
    // has no regions or no chunks contributed (caller should leave its
    // outputs untouched).
    public static bool SampleWeights(Vector3 playerWorldPos, WorldState ws, Span<float> outWeights)
    {
        if (ws == null || ws.Regions == null || ws.Regions.Length == 0) { return false; }
        if (outWeights.Length != ws.Regions.Length) { return false; }
        return ComputeWeights(playerWorldPos, ws, outWeights);
    }

    // Shared kernel: smoothstep-distance-weighted region accumulation
    // around `playerWorldPos`, normalized to sum to 1. Returns false if
    // the kernel found no loaded chunks belonging to a known region —
    // caller treats that as "no data, leave previous outputs alone".
    private static bool ComputeWeights(Vector3 playerWorldPos, WorldState ws, Span<float> weights)
    {
        int regionCount = weights.Length;
        for (int i = 0; i < regionCount; i++) { weights[i] = 0f; }

        // The chunk-grid window around the player. Span = ceil(radius) on
        // each side so any chunk whose center could be within the kernel
        // is considered.
        float radiusChunks = BlendRadiusChunks;
        int kernelHalfChunks = Mathf.CeilToInt(radiusChunks);
        int playerChunkX = Mathf.FloorToInt(playerWorldPos.X / ChunkState.SIZE);
        int playerChunkZ = Mathf.FloorToInt(playerWorldPos.Z / ChunkState.SIZE);
        int playerChunkY = Mathf.FloorToInt(playerWorldPos.Y / ChunkState.SIZE);

        for (int dx = -kernelHalfChunks; dx <= kernelHalfChunks; dx++)
        {
            for (int dz = -kernelHalfChunks; dz <= kernelHalfChunks; dz++)
            {
                int cx = playerChunkX + dx;
                int cz = playerChunkZ + dz;
                int regionIdx = ResolveColumnRegion(ws, cx, playerChunkY, cz);
                if (regionIdx < 0 || regionIdx >= regionCount) { continue; }

                // Distance in CHUNK units from player → chunk center.
                // Working in chunk units keeps the radius parameter
                // intuitive ("blend over 2 chunks worth") and independent
                // of ChunkState.SIZE.
                float chunkCenterX = (cx + 0.5f) * ChunkState.SIZE;
                float chunkCenterZ = (cz + 0.5f) * ChunkState.SIZE;
                float distChunks = Mathf.Sqrt(
                    ((playerWorldPos.X - chunkCenterX) * (playerWorldPos.X - chunkCenterX)
                   + (playerWorldPos.Z - chunkCenterZ) * (playerWorldPos.Z - chunkCenterZ))
                ) / ChunkState.SIZE;

                // smoothstep(R, 0, d) = 1 at d=0, 0 at d=R, smooth in between.
                float w = Mathf.SmoothStep(radiusChunks, 0f, distChunks);
                if (w > 0f) { weights[regionIdx] += w; }
            }
        }

        float totalWeight = 0f;
        for (int i = 0; i < regionCount; i++) { totalWeight += weights[i]; }
        if (totalWeight < MinTotalWeight) { return false; }
        float inv = 1f / totalWeight;
        for (int i = 0; i < regionCount; i++) { weights[i] *= inv; }
        return true;
    }

    // Pick the region this column (cx, cz) belongs to. Tries the chunk
    // at the player's Y first; if that's unloaded, scans the column for
    // any loaded chunk so a chunk above/below the player still
    // contributes its region. Returns -1 if no chunk in this column is
    // loaded — caller skips the contribution (correct streaming default).
    private static int ResolveColumnRegion(WorldState ws, int cx, int preferredY, int cz)
    {
        ChunkState chunk = ws.GetChunk(new Vector3I(cx, preferredY, cz));
        if (chunk != null) { return chunk.RegionIndex; }
        // Fall back to a scan over the world's Y range. Only runs when
        // the player straddles a column with no chunk at their Y level
        // (e.g. flying above the world bounds), so the cost is fine.
        for (int cy = ws.Min.Y; cy <= ws.Max.Y; cy++)
        {
            chunk = ws.GetChunk(new Vector3I(cx, cy, cz));
            if (chunk != null) { return chunk.RegionIndex; }
        }
        return -1;
    }

    private static void AccumulateColor(Color c, float w,
        ref float r, ref float g, ref float b, ref float a)
    {
        r += c.R * w;
        g += c.G * w;
        b += c.B * w;
        a += c.A * w;
    }

    // N-way weighted blend into the provided WeatherData. Regions with
    // a null weather sub-resource drop out; weights re-normalize across
    // the regions that DO have weather so worlds with partial authoring
    // still produce a sensible result.
    private static void BlendWeather(WorldState ws, Span<float> weights, WeatherData dst)
    {
        float sum = 0f;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] <= 0f) { continue; }
            if (ws.Regions[i].Data?.weather == null) { continue; }
            sum += weights[i];
        }
        if (sum < MinTotalWeight) { return; }
        float inv = 1f / sum;

        float cloudCover = 0, windSpeed = 0, temperature = 0, humidity = 0, rainAmount = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            float w = weights[i];
            if (w <= 0f) { continue; }
            WeatherData wd = ws.Regions[i].Data?.weather;
            if (wd == null) { continue; }
            float nw = w * inv;
            cloudCover += wd.cloudCover * nw;
            windSpeed += wd.windSpeed * nw;
            temperature += wd.temperature * nw;
            humidity += wd.humidity * nw;
            rainAmount += wd.rainAmount * nw;
        }

        dst.cloudCover = cloudCover;
        dst.windSpeed = windSpeed;
        dst.temperature = temperature;
        dst.humidity = humidity;
        dst.rainAmount = rainAmount;
    }

    private static Vector2 SafeNormalizeXZ(Vector3 v)
    {
        Vector2 xz = new Vector2(v.X, v.Z);
        float l2 = xz.LengthSquared();
        if (l2 < 1e-6f) { return Vector2.Zero; }
        return xz / Mathf.Sqrt(l2);
    }
}
