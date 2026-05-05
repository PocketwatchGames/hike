using System;
using Godot;

// Diurnal + per-12h-variance perturbation of the blended WeatherData.
// Called between ZoneBlend.Sample (which writes the per-frame max-
// envelope WeatherData from the four zone presets) and
// WeatherDerivation.Derive (which turns the working WeatherData into
// the visible palette). Authored ZoneData.weather values are treated
// as MAX values for each channel; this pass maps them down to the
// values currently in effect.
//
// Two stages:
//   UpdateVariance — advances the WorldState's prev / next / current
//                    variance scalars. Re-rolls `next` every
//                    SimData.VarianceHours and smooth-lerps current from
//                    prev → next across the next sunrise / sunset.
//   Apply          — rewrites the working WeatherData fields in place
//                    using (diurnal curve, variance, zone max,
//                    elevation) per the user-spec couplings. Pure.
//
// Pure-data design: no node references, no allocations on the hot path.
public static class WeatherSimulation
{
    // Smoothed sinusoid in [0, 1] reaching 1 at sim.DiurnalPeak01 and
    // 0 at sim.DiurnalTrough01, with continuous derivative everywhere
    // (slope vanishes at both extrema, so wind / temp / cloud / dust
    // ride a smooth wave through noon and midnight rather than popping
    // at the peak / trough boundary). Mapping:
    //   t_rel  = (timeOfDay01 - trough) mod 1   ∈ [0, 1)
    //   warmingHalf = (peak - trough) mod 1     ∈ (0, 1)
    //   u: piecewise-linear in t_rel, 0 at trough → 1 at peak → 0 at next trough.
    //   value = 0.5 - 0.5·cos(π·u)
    public static float DiurnalCurve(float timeOfDay01, SimData sim)
    {
        float peak = sim?.DiurnalPeak01 ?? 0.6f;
        float trough = sim?.DiurnalTrough01 ?? 0.275f;
        float tRel = Mathf.PosMod(timeOfDay01 - trough, 1f);
        float warmingHalf = Mathf.PosMod(peak - trough, 1f);
        if (warmingHalf < 1e-4f) { warmingHalf = 0.5f; }
        float u = tRel < warmingHalf
            ? tRel / warmingHalf                                // 0 → 1 across warming half
            : 1f - (tRel - warmingHalf) / (1f - warmingHalf);   // 1 → 0 across cooling half
        return 0.5f - 0.5f * Mathf.Cos(u * Mathf.Pi);
    }

    // Signed time-derivative of DiurnalCurve at the current time, in
    // per-day-fraction units. Positive on the trough→peak (warming)
    // half, negative on the peak→trough (cooling) half, and exactly
    // zero at both extrema (so cooling-rate / warming-rate signals
    // taper smoothly into and out of the inflection points).
    public static float DiurnalCurveSlope(float timeOfDay01, SimData sim)
    {
        float peak = sim?.DiurnalPeak01 ?? 0.6f;
        float trough = sim?.DiurnalTrough01 ?? 0.275f;
        float tRel = Mathf.PosMod(timeOfDay01 - trough, 1f);
        float warmingHalf = Mathf.PosMod(peak - trough, 1f);
        if (warmingHalf < 1e-4f) { warmingHalf = 0.5f; }
        bool warming = tRel < warmingHalf;
        float u = warming
            ? tRel / warmingHalf
            : 1f - (tRel - warmingHalf) / (1f - warmingHalf);
        float dudt = warming
            ? 1f / warmingHalf
            : -1f / (1f - warmingHalf);
        return 0.5f * Mathf.Pi * Mathf.Sin(u * Mathf.Pi) * dudt;
    }

    // Total swing magnitude of the curve over a day — used as the
    // baseline-wind "convection" forcing. Constant 1.0 by construction
    // (the curve spans 0..1) but kept named for readability.
    private const float DiurnalSwingMagnitude = 1.0f;

    // Number of handover events per game-day. With VarianceHours=12,
    // returns 2 (sunrise + sunset). Other cadences distribute evenly:
    // VarianceHours=24 → 1 (sunrise only); VarianceHours=6 → 4
    // (sunrise / noon / sunset / midnight). Clamped to >= 1.
    private static int HandoversPerDay(SimData sim)
    {
        return Mathf.Max(1, Mathf.RoundToInt(24f / Mathf.Max(sim.VarianceHours, 1f)));
    }

    // Advance one variance channel using a HANDOVER-PHASE index:
    // - Phase N is the index of the most recently STARTED crossfade
    //   window. Window N opens at gameDay = N/hpd + 0.25 - halfWidth
    //   (i.e. just before sunrise / sunset), so phase increments AT
    //   the start of each window.
    // - When phase increments, prev := next and a fresh next is rolled.
    //   At that moment the displayed value equals both the old next
    //   (just before promotion) and the new prev (just after), so the
    //   transition is continuous — no pop.
    // - Inside the window, the displayed value smooth-steps prev → next.
    // - Outside the window (between the end of one and the start of
    //   the next), the value sits at next.
    //
    // Returns (value, slopePerDayFraction). The slope is 0 outside the
    // window, peaks at the window center, and is the analytical
    // derivative of the SmoothStep blend — used by Apply for wind /
    // temperature transients without frame-rate-dependent diffs.
    private static void AdvanceChannel(
        double gameDay, int hpd, float halfWidth,
        ref float prev, ref float next, ref long phaseField,
        RandomNumberGenerator rng,
        out float value, out float slopePerDayFraction)
    {
        // Phase whose crossfade window has STARTED at the current
        // gameDay. Window N starts at N/hpd + 0.25 - halfWidth, so
        //   phase = floor((gameDay - 0.25 + halfWidth) * hpd).
        long currentPhase = (long)Math.Floor((gameDay - 0.25 + halfWidth) * hpd);

        if (phaseField == long.MinValue)
        {
            // First call after WorldState construction — the seeded
            // prev/next pair is what's "in flight" now; just snap to
            // the current phase without rolling.
            phaseField = currentPhase;
        }
        while (phaseField < currentPhase)
        {
            prev = next;
            next = rng.Randf();
            phaseField++;
        }

        // Day-fraction position of the handover at the center of the
        // current phase's window. distInWindow = 0 at window start,
        // 2*halfWidth at window end, and grows past 2*halfWidth in the
        // hold period before the next window opens (clamped by
        // SmoothStep, so the displayed value sits at next).
        double handoverGameDay = (double)currentPhase / hpd + 0.25;
        double windowStart = handoverGameDay - halfWidth;
        double distInWindow = gameDay - windowStart;
        float windowSpan = 2f * halfWidth;
        float crossfade = Mathf.SmoothStep(0f, windowSpan, (float)distInWindow);
        value = Mathf.Lerp(prev, next, crossfade);

        // Analytical slope of SmoothStep across [0, windowSpan]:
        //   d/dx SmoothStep(0, w, x) = 6x(w-x) / w³, for x in [0, w], else 0.
        // Combined with d(value)/d(crossfade) = (next - prev), we get
        // d(value)/d(gameDay). Normalized to a peak magnitude of 1 by
        // dividing by 1.5 / windowSpan (the SmoothStep slope max), so
        // the returned slope is in the same range as (next - prev).
        if (distInWindow > 0 && distInWindow < windowSpan)
        {
            float x = (float)distInWindow;
            // 6x(w-x)/w^3 has max 1.5/w at x=w/2. Dividing by that max
            // gives 4x(w-x)/w^2 — clean shape function in [0, 1].
            float shape = 4f * x * (windowSpan - x) / (windowSpan * windowSpan);
            slopePerDayFraction = (next - prev) * shape;
        }
        else
        {
            slopePerDayFraction = 0f;
        }
    }

    // Advance the WorldState's variance state. Three independent
    // channels — temperature/wind (`WeatherVariance`), humidity, and
    // cloud cover — each with their own prev/next/phase index. Rolls
    // are tied to sunrise/sunset crossings, NOT absolute game-hours,
    // so a roll never pops the displayed value. Channels are
    // decoupled so a humid front doesn't have to coincide with a
    // temperature swing. Humidity / cloud channels' effect is gated
    // by simulated wind speed in Apply (advection model).
    public static void UpdateVariance(WorldState ws, SimData sim)
    {
        if (ws == null || sim == null) { return; }

        // Use TimeOfDayAbsolute (advances on the time_scale clock, in
        // lockstep with TimeOfDay01) — NOT GameTimeMs. The variance
        // handover boundaries must align with the actual sunrise /
        // sunset times that the lighting cycle uses; deriving them
        // from real time would let them drift whenever time_scale != 1
        // or InitialTimeOfDay != 0, and a variance handover landing on
        // top of the day/night phase blend produces a visible
        // lighting pop.
        double gameDay = ws.TimeOfDayAbsolute;
        int hpd = HandoversPerDay(sim);
        float halfWidth = Mathf.Max(sim.VarianceCrossfadeHalfWidth01, 1e-4f);

        AdvanceChannel(gameDay, hpd, halfWidth,
            ref ws.WeatherVariancePrev, ref ws.WeatherVarianceNext, ref ws.WeatherVariancePhase,
            ws.WeatherRng,
            out ws.WeatherVariance, out ws.WeatherVarianceSlope);
        AdvanceChannel(gameDay, hpd, halfWidth,
            ref ws.HumidityVariancePrev, ref ws.HumidityVarianceNext, ref ws.HumidityVariancePhase,
            ws.WeatherRng,
            out ws.HumidityVariance, out ws.HumidityVarianceSlope);
        AdvanceChannel(gameDay, hpd, halfWidth,
            ref ws.CloudVariancePrev, ref ws.CloudVarianceNext, ref ws.CloudVariancePhase,
            ws.WeatherRng,
            out ws.CloudVariance, out ws.CloudVarianceSlope);
    }

    // Rewrite weather fields in place using (zone, zone max,
    // diurnal curve, variance). Reads `weather` as the BLENDED MAX
    // values for weather-state channels (cloud, wind, temp, humidity,
    // fog, rain — the per-zone authored ceilings, blended by
    // ZoneBlend). Reads `zone` for the zone-intrinsic palette
    // (DustAmount as max, etc.) and `elevation` (the blended runtime
    // ZoneState.Elevation, since it now lives off the authored
    // ZoneData) as static forcing. Writes the simulated current
    // value for every weather channel.
    public static void Apply(WeatherData weather, ZoneData zone, float elevation, WorldState ws, SimData sim)
    {
        if (weather == null || sim == null) { return; }

        float timeOfDay01 = (float)(ws?.TimeOfDay01 ?? 0.5);
        float diurnal = DiurnalCurve(timeOfDay01, sim);   // 0=trough, 1=peak
        float diurnalSlope = DiurnalCurveSlope(timeOfDay01, sim);
        float coolDiurnal = 1f - diurnal;                  // 1 at trough, 0 at peak
        float coolingRate = Mathf.Max(0f, -diurnalSlope);  // >0 only when cooling

        // Authored max envelope. Fog has no authored max — WeatherDerivation
        // computes it directly from simulated humidity + cool-diurnal.
        float humidityMax = Mathf.Clamp(weather.humidity, 0f, 1f);
        float tempMax = weather.temperature;
        float windMax = Mathf.Max(weather.windSpeed, 0f);
        float cloudMax = Mathf.Clamp(weather.cloudCover, 0f, 1f);
        float rainMax = Mathf.Clamp(weather.rainAmount, 0f, 1f);
        float dustMax = Mathf.Clamp(zone?.DustAmount ?? 0f, 0f, 1f);
        elevation = Mathf.Clamp(elevation, 0f, 1f);

        // --- Baselines (diurnal-modulated maxes) -------------------------
        // Baseline humidity. Real humidity is HIGHEST at the cool trough
        // (cold air is closer to saturation) and LOWEST at the warm peak,
        // so we use coolDiurnal as the curve. Hot zones and high-
        // elevation zones damp the ceiling.
        float humidityFromTempScale = Mathf.Clamp(tempMax / 40f, 0f, 1f); // ~0..1 over 0..40C
        float baseHumidityCeiling = humidityMax
            * (1f - elevation * sim.HumidityFromElevation)
            * (1f - humidityFromTempScale * sim.HumidityFromMaxTemp);
        float baselineHumidity = Mathf.Clamp(
            baseHumidityCeiling * Mathf.Lerp(1f, coolDiurnal, sim.HumidityDiurnalDepth),
            0f, 1f);

        // Baseline temperature. Diurnal swing dampened by humidity (humid
        // air resists temperature change), envelope cooled by elevation.
        float tempSwing = sim.TempDiurnalDepth * (1f - baselineHumidity * sim.TempHumidityDamping);
        float tempEnvelope = tempMax * (1f - elevation * sim.TempFromElevation);
        // Map diurnal in [0,1] onto [tempEnvelope*(1-tempSwing), tempEnvelope].
        float baselineTemp = tempEnvelope * (1f - tempSwing) + tempEnvelope * tempSwing * diurnal;

        // Baseline wind. Diurnal lift + signed cooling-rate forcing +
        // elevation. The cooling-rate forcing is the NEGATED diurnal
        // slope (positive when temperature is dropping, negative when
        // rising), clamped to [-1, +1]. Wind rises when the air column
        // is collapsing thermally (afternoon → evening) and falls when
        // it's heating up (morning). Combined with WindDiurnalDepth
        // (which peaks at the afternoon diurnal max), the total
        // baseline lands its peak in the late afternoon / early evening
        // and bottoms out late-morning to pre-dawn — closer to how real
        // ground wind behaves than the symmetric |slope| convection it
        // replaces.
        float coolingForcing = Mathf.Clamp(-diurnalSlope * DiurnalSwingMagnitude, -1f, 1f);
        float windDiurnalScale = Mathf.Lerp(1f - sim.WindDiurnalDepth, 1f, diurnal);
        float baselineWind = Mathf.Max(0f, windMax
            * windDiurnalScale
            * (1f + coolingForcing * sim.WindFromTempDiff)
            * (1f + elevation * sim.WindFromElevation));

        // Baseline cloud. Wind brings cloud in; humid+warm air rises into
        // the cloud layer.
        float windFraction = windMax > 1e-3f ? Mathf.Clamp(baselineWind / Mathf.Max(windMax, 1e-3f), 0f, 2f) : 0f;
        float humidityWarmth = baselineHumidity * diurnal;
        float cloudDiurnalScale = Mathf.Lerp(1f - sim.CloudDiurnalDepth, 1f, diurnal);
        float baselineCloudCeiling = Mathf.Clamp(
            cloudMax
            * (1f + (windFraction - 1f) * sim.CloudFromWind)
            * (1f + humidityWarmth * sim.CloudFromHumidityWarmth - sim.CloudFromHumidityWarmth * 0.5f),
            0f, 1f);
        float baselineCloud = Mathf.Clamp(baselineCloudCeiling * cloudDiurnalScale, 0f, 1f);

        // --- Variance perturbation ---------------------------------------
        float variance = Mathf.Clamp(ws?.WeatherVariance ?? 0.5f, 0f, 1f);
        float varianceCenter = variance - 0.5f;            // -0.5..+0.5
        // Analytical slope of the variance across the sunrise/sunset
        // crossfade window, normalized to a peak magnitude equal to
        // |next - prev| (so |slope| ∈ [0, 1] for the worst-case
        // 0→1 swing). 0 outside the window — wind / temperature
        // transients only fire DURING a frontal handover, not while
        // weather is steady. Frame-rate independent and continuous,
        // so no pops at the variance edges.
        float varianceSlope = ws?.WeatherVarianceSlope ?? 0f;
        float absVarianceDelta = Mathf.Abs(varianceSlope);

        // Wind: bidirectional center term (stormy variance lifts wind,
        // fair variance damps it) plus a non-negative |slope| frontal
        // kick. Computed first so it can gate the humidity/cloud
        // advection below.
        float simWind = Mathf.Max(0f, baselineWind
            * (1f - varianceCenter * 2f * sim.WindVarianceK)
            * (1f + absVarianceDelta * sim.WindVarianceDeltaK));

        // Temperature: positive to variance, inverse to |dVariance|.
        float simTemp = baselineTemp
            * (1f + varianceCenter * 2f * sim.TempVarianceK)
            * (1f - absVarianceDelta * sim.TempVarianceDeltaK);

        // Humidity & cloud cover use INDEPENDENT variance channels and
        // their effect is gated by simulated wind speed (modelling
        // advection — a calm day stays at the regional baseline; a
        // strong wind blows the neighboring weather pattern in). At
        // zero wind, baseline holds; at AdvectedVarianceWindRef the
        // perturbation is fully expressed. Inverse relationship: high
        // variance = drier / clearer than baseline, low = wetter /
        // cloudier.
        float windGate = Mathf.Clamp(simWind / Mathf.Max(sim.AdvectedVarianceWindRef, 1e-3f), 0f, 1f);
        float humidityVarianceCenter = Mathf.Clamp(ws?.HumidityVariance ?? 0.5f, 0f, 1f) - 0.5f;
        float cloudVarianceCenter = Mathf.Clamp(ws?.CloudVariance ?? 0.5f, 0f, 1f) - 0.5f;
        float simHumidity = Mathf.Clamp(
            baselineHumidity * (1f - humidityVarianceCenter * 2f * sim.HumidityVarianceK * windGate),
            0f, 1f);
        float simCloud = Mathf.Clamp(
            baselineCloud * (1f - cloudVarianceCenter * 2f * sim.CloudVarianceK * windGate),
            0f, 1f);

        // --- Simulated derived (rain / dust) -----------------------------
        // Fog is no longer simulated here — WeatherDerivation derives it
        // straight from simulated humidity + cool-diurnal so there's no
        // intermediate `weather.fog` field to round-trip through.

        // Rain: needs cloud above the threshold AND temperature dropping.
        float cloudGate = Mathf.SmoothStep(sim.RainCloudThreshold, 1f, simCloud);
        float rainSignal = cloudGate * sim.RainFromCloudCover * (1f + coolingRate * sim.RainFromCoolingRate);
        float simRain = Mathf.Clamp(rainMax * rainSignal, 0f, 1f);

        // Dust: lifted by wind, elevation, and warmth; suppressed by
        // humidity and rain.
        float windLift = windMax > 1e-3f ? Mathf.Clamp(simWind / windMax, 0f, 2f) : 0f;
        float dustSignal = windLift * sim.DustFromWind
            * (1f + elevation * sim.DustFromElevation)
            * (1f + diurnal * sim.DustFromWarmth);
        float dustSuppress = (1f - simHumidity * sim.DustHumiditySuppression)
            * (1f - simRain * sim.DustRainSuppression);
        float simDust = Mathf.Clamp(dustMax * dustSignal * dustSuppress, 0f, 1f);

        // Write back. Wind direction and authored temperature unit
        // (degrees C) flow through unchanged.
        weather.humidity = simHumidity;
        weather.temperature = simTemp;
        weather.windSpeed = simWind;
        weather.cloudCover = simCloud;
        weather.rainAmount = simRain;
        weather.dustAmount = simDust;
    }

}
