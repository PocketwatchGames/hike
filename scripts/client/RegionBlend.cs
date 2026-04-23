using Godot;

// Samples a blended RegionData + WeatherData at a world XZ position.
// Four SimData region slots sit at (±x, ±z) corners around the world
// origin; a 32m cross-blend band centered on X=0 and Z=0 mixes them
// bilinearly. Null slots contribute zero weight and their neighbors
// scale up to cover.
//
// This is transitional scaffolding. The long-term design is arbitrary
// region placement; Sample() stays as the single seam, only the
// weight-computation changes.
public static class RegionBlend
{
    // Half-width of the axis-aligned transition band in meters. Within
    // ±this of each axis the region mixture cross-blends bilinearly.
    public const float TransitionHalfWidthMeters = 16f;

    // Blend the four SimData region slots at the given world position
    // and write the result into the caller-owned outRegion + outWeather.
    // These are mutable working copies held by SkyController so we
    // never allocate Resources on the hot path. If all four region
    // slots are null, the outputs are left at whatever the caller had
    // (SkyController seeds them with sensible defaults).
    public static void Sample(Vector3 playerWorldPos, SimData sim, RegionData outRegion, WeatherData outWeather)
    {
        if (sim == null || outRegion == null || outWeather == null) { return; }

        RegionData sw = sim.regionSW;
        RegionData se = sim.regionSE;
        RegionData nw = sim.regionNW;
        RegionData ne = sim.regionNE;

        // tx: 0 on west side, 1 on east side; blends within ±half-width
        // of X=0. tz: same, with +Z = north.
        float tx = Mathf.SmoothStep(-TransitionHalfWidthMeters, TransitionHalfWidthMeters, playerWorldPos.X);
        float tz = Mathf.SmoothStep(-TransitionHalfWidthMeters, TransitionHalfWidthMeters, playerWorldPos.Z);

        float wSW = (1f - tx) * (1f - tz);
        float wSE = tx * (1f - tz);
        float wNW = (1f - tx) * tz;
        float wNE = tx * tz;

        // Zero-out weights for null slots so the remaining corners
        // cover continuously. A world with only NE+SW authored still
        // blends correctly along the NE↔SW diagonal.
        if (sw == null) { wSW = 0f; }
        if (se == null) { wSE = 0f; }
        if (nw == null) { wNW = 0f; }
        if (ne == null) { wNE = 0f; }
        float sum = wSW + wSE + wNW + wNE;
        if (sum < 1e-6f) { return; }
        float inv = 1f / sum;
        wSW *= inv; wSE *= inv; wNW *= inv; wNE *= inv;

        outRegion.SunColor = WeightedColor(
            sw?.SunColor, wSW, se?.SunColor, wSE, nw?.SunColor, wNW, ne?.SunColor, wNE);
        outRegion.MoonColor = WeightedColor(
            sw?.MoonColor, wSW, se?.MoonColor, wSE, nw?.MoonColor, wNW, ne?.MoonColor, wNE);
        outRegion.SkyColor = WeightedColor(
            sw?.SkyColor, wSW, se?.SkyColor, wSE, nw?.SkyColor, wNW, ne?.SkyColor, wNE);
        outRegion.DustColor = WeightedColor(
            sw?.DustColor, wSW, se?.DustColor, wSE, nw?.DustColor, wNW, ne?.DustColor, wNE);

        BlendWeather(outWeather,
            sw?.weather, wSW, se?.weather, wSE, nw?.weather, wNW, ne?.weather, wNE);
    }

    // 4-way weighted color blend. Null contributions are skipped; if
    // all four are null the output stays at its previous value.
    private static Color WeightedColor(Color? a, float wa, Color? b, float wb, Color? c, float wc, Color? d, float wd)
    {
        float r = 0, g = 0, bc = 0, al = 0, ws = 0;
        if (a.HasValue) { var v = a.Value; r += v.R * wa; g += v.G * wa; bc += v.B * wa; al += v.A * wa; ws += wa; }
        if (b.HasValue) { var v = b.Value; r += v.R * wb; g += v.G * wb; bc += v.B * wb; al += v.A * wb; ws += wb; }
        if (c.HasValue) { var v = c.Value; r += v.R * wc; g += v.G * wc; bc += v.B * wc; al += v.A * wc; ws += wc; }
        if (d.HasValue) { var v = d.Value; r += v.R * wd; g += v.G * wd; bc += v.B * wd; al += v.A * wd; ws += wd; }
        if (ws < 1e-6f) { return new Color(1f, 1f, 1f, 1f); }
        return new Color(r, g, bc, al);
    }

    // 4-way weighted blend into the provided WeatherData. Null entries
    // drop out; weights are re-normalized across present entries so
    // authoring three regions with one null weather slot still works.
    // Wind direction blends via vector sum (the shortest-arc
    // generalization for N inputs) and is re-normalized at the end.
    private static void BlendWeather(WeatherData dst,
        WeatherData a, float wa, WeatherData b, float wb,
        WeatherData c, float wc, WeatherData d, float wd)
    {
        float sum = 0f;
        if (a != null) { sum += wa; }
        if (b != null) { sum += wb; }
        if (c != null) { sum += wc; }
        if (d != null) { sum += wd; }
        if (sum < 1e-6f) { return; }
        float inv = 1f / sum;
        if (a != null) { wa *= inv; } else { wa = 0f; }
        if (b != null) { wb *= inv; } else { wb = 0f; }
        if (c != null) { wc *= inv; } else { wc = 0f; }
        if (d != null) { wd *= inv; } else { wd = 0f; }

        float cloudCover = 0, windSpeed = 0, temperature = 0, humidity = 0, fog = 0, rainAmount = 0, dustAmount = 0;
        Vector2 windDir2D = Vector2.Zero;

        if (a != null)
        {
            cloudCover += a.cloudCover * wa;
            windSpeed += a.windSpeed * wa;
            temperature += a.temperature * wa;
            humidity += a.humidity * wa;
            fog += a.fog * wa;
            rainAmount += a.rainAmount * wa;
            dustAmount += a.dustAmount * wa;
            windDir2D += SafeNormalizeXZ(a.windDirection) * wa;
        }
        if (b != null)
        {
            cloudCover += b.cloudCover * wb;
            windSpeed += b.windSpeed * wb;
            temperature += b.temperature * wb;
            humidity += b.humidity * wb;
            fog += b.fog * wb;
            rainAmount += b.rainAmount * wb;
            dustAmount += b.dustAmount * wb;
            windDir2D += SafeNormalizeXZ(b.windDirection) * wb;
        }
        if (c != null)
        {
            cloudCover += c.cloudCover * wc;
            windSpeed += c.windSpeed * wc;
            temperature += c.temperature * wc;
            humidity += c.humidity * wc;
            fog += c.fog * wc;
            rainAmount += c.rainAmount * wc;
            dustAmount += c.dustAmount * wc;
            windDir2D += SafeNormalizeXZ(c.windDirection) * wc;
        }
        if (d != null)
        {
            cloudCover += d.cloudCover * wd;
            windSpeed += d.windSpeed * wd;
            temperature += d.temperature * wd;
            humidity += d.humidity * wd;
            fog += d.fog * wd;
            rainAmount += d.rainAmount * wd;
            dustAmount += d.dustAmount * wd;
            windDir2D += SafeNormalizeXZ(d.windDirection) * wd;
        }

        dst.cloudCover = cloudCover;
        dst.windSpeed = windSpeed;
        dst.temperature = temperature;
        dst.humidity = humidity;
        dst.fog = fog;
        dst.rainAmount = rainAmount;
        dst.dustAmount = dustAmount;

        if (windDir2D.LengthSquared() > 1e-6f)
        {
            windDir2D = windDir2D.Normalized();
            dst.windDirection = new Vector3(windDir2D.X, 0f, windDir2D.Y);
        }
        else
        {
            dst.windDirection = new Vector3(1f, 0f, 0f);
        }
    }

    private static Vector2 SafeNormalizeXZ(Vector3 v)
    {
        Vector2 xz = new Vector2(v.X, v.Z);
        float l2 = xz.LengthSquared();
        if (l2 < 1e-6f) { return Vector2.Zero; }
        return xz / Mathf.Sqrt(l2);
    }
}
