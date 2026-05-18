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
//   UpdateVariance — advances the WorldState's prev / cur / next
//                    variance scalars. Rotates the chain every
//                    SimData.VarianceHours and smooth-lerps the
//                    displayed value from prev → cur across the next
//                    sunrise / sunset window. `next` is the upcoming
//                    phase's variance, pre-rolled one handover early
//                    so the HUD weather forecast can read it before
//                    the transition begins.
//   Apply          — rewrites the working WeatherData fields in place
//                    using (diurnal curve, variance, zone max,
//                    elevation) per the user-spec couplings. Pure.
//
// Pure-data design: no node references, no allocations on the hot path.
public static class WeatherSimulation
{
    // Debug override: when set, Apply writes this value to
    // weather.lightningAmount after the normal gates+variance
    // computation, bypassing the cloud/rain thresholds and the
    // variance multiplier. Used by the `force_lightning` CVar to
    // immediately trigger the audio scheduler, visual flash, and HUD
    // thunder icon without waiting on a favorable variance roll.
    // null = no override (normal sim behavior). Affects every Apply
    // call — including the HUD's forecast for the day peak / night
    // trough — so the thunder icon also lights up when the override
    // is active.
    public static float? ForceLightningOverride { get; set; }


    // Flat-topped trapezoid in [0, 1]. Day plateau (value 1) covers
    //   |tod - DiurnalPeak01| ≤ DiurnalPlateauHalfWidth
    // (wrapping in normalized time); night plateau (value 0) covers
    // the symmetric band around DiurnalTrough01 = DiurnalPeak01 ± 0.5.
    // SmoothStep ramps fill the two quarter-day windows in between.
    //
    // The ramps are C¹ (zero derivative at the plateau boundaries
    // because SmoothStep's slope vanishes at its endpoints), so wind /
    // temperature / cloud / dust glide smoothly off and onto the
    // plateaus rather than popping at the boundary. Slope is exactly
    // zero on both plateaus, which is what kills coolingRate while the
    // day weather is "at peak."
    public static float DiurnalCurve(float timeOfDay01, SimData sim)
    {
        float peak = sim?.DiurnalPeak01 ?? 0.5f;
        float halfWidth = Mathf.Clamp(sim?.DiurnalPlateauHalfWidth ?? 0.125f, 0f, 0.249f);
        // Signed distance from the day-plateau center in normalized
        // time, wrapped to [-0.5, 0.5). Negative = before peak
        // (warming), positive = after peak (cooling).
        float dt = Mathf.PosMod(timeOfDay01 - peak + 0.5f, 1f) - 0.5f;
        float ad = Mathf.Abs(dt);
        if (ad <= halfWidth) { return 1f; }
        if (ad >= 0.5f - halfWidth) { return 0f; }
        float rampSpan = 0.5f - 2f * halfWidth;
        // p ∈ (0, 1): 0 at the day-plateau edge, 1 at the night-plateau edge.
        float p = (ad - halfWidth) / rampSpan;
        return 1f - Mathf.SmoothStep(0f, 1f, p);
    }

    // Signed time-derivative of DiurnalCurve at the current time, in
    // per-day-fraction units. Exactly zero on both plateaus and at the
    // plateau boundaries (SmoothStep's vanishing endpoint derivative).
    // Positive across the warming ramp, negative across the cooling
    // ramp; coolingRate = max(0, -slope) is therefore strictly positive
    // only inside [day-plateau-end, night-plateau-start].
    public static float DiurnalCurveSlope(float timeOfDay01, SimData sim)
    {
        float peak = sim?.DiurnalPeak01 ?? 0.5f;
        float halfWidth = Mathf.Clamp(sim?.DiurnalPlateauHalfWidth ?? 0.125f, 0f, 0.249f);
        float dt = Mathf.PosMod(timeOfDay01 - peak + 0.5f, 1f) - 0.5f;
        float ad = Mathf.Abs(dt);
        if (ad <= halfWidth) { return 0f; }
        if (ad >= 0.5f - halfWidth) { return 0f; }
        float rampSpan = 0.5f - 2f * halfWidth;
        float p = (ad - halfWidth) / rampSpan;
        // d/dp SmoothStep = 6p(1-p); diurnal = 1 - SmoothStep(p), so
        // d(diurnal)/dp = -6p(1-p). p = (ad - halfWidth) / rampSpan;
        // d(ad)/dt = sign(dt). Chain rule:
        //   d(diurnal)/dt = -6p(1-p) × (1/rampSpan) × sign(dt)
        // sign(dt) > 0 in the cooling ramp ⇒ slope negative ⇒
        // coolingRate positive.
        float magnitude = 6f * p * (1f - p) / rampSpan;
        return dt > 0f ? -magnitude : magnitude;
    }

    // Total swing magnitude of the curve over a day — used as the
    // baseline-wind "convection" forcing. Constant 1.0 by construction
    // (the curve spans 0..1) but kept named for readability.
    private const float DiurnalSwingMagnitude = 1.0f;

    // Number of handover events per game-day. With VarianceHours=12,
    // returns 2 (sunrise + sunset). Other cadences distribute evenly:
    // VarianceHours=24 → 1 (sunrise only); VarianceHours=6 → 4
    // (sunrise / noon / sunset / midnight). Clamped to >= 1.
    public static int HandoversPerDay(SimData sim)
    {
        return Mathf.Max(1, Mathf.RoundToInt(24f / Mathf.Max(sim?.VarianceHours ?? 12f, 1f)));
    }

    // Phase index whose crossfade window has STARTED at `gameDay`. Window
    // N starts at N/hpd + 0.25 - halfWidth (i.e. the leading edge of the
    // sunrise / sunset crossfade), so phase increments AT the start of
    // each window. Public so the HUD weather widget can pick variance
    // sources by phase parity without depending on
    // WorldState.WeatherVariancePhase having been bumped this frame.
    public static long CurrentPhase(double gameDay, SimData sim)
    {
        if (sim == null) { return 0; }
        return CurrentPhase(gameDay, HandoversPerDay(sim),
            Mathf.Max(sim.VarianceCrossfadeHalfWidth01, 1e-4f));
    }

    private static long CurrentPhase(double gameDay, int hpd, float halfWidth)
    {
        return (long)Math.Floor((gameDay - 0.25 + halfWidth) * hpd);
    }

    // Advance one variance channel using a HANDOVER-PHASE index:
    // - Phase N is the index of the most recently STARTED crossfade
    //   window. Window N opens at gameDay = N/hpd + 0.25 - halfWidth
    //   (i.e. just before sunrise / sunset), so phase increments AT
    //   the start of each window.
    // - When phase increments, the chain rotates: prev := cur,
    //   cur := next, next := fresh roll. At that moment the displayed
    //   value equals both the old `cur` (just before promotion) and
    //   the new `prev` (just after), so the transition is continuous.
    //   `next` always holds the variance for the UPCOMING phase, rolled
    //   one handover early so HUD forecasts can read it before the
    //   transition begins.
    // - Inside the window, the displayed value smooth-steps prev → cur.
    // - Outside the window (between the end of one and the start of
    //   the next), the value sits at cur.
    //
    // Returns (value, slopePerDayFraction). The slope is 0 outside the
    // window, peaks at the window center, and is the analytical
    // derivative of the SmoothStep blend — used by Apply for wind /
    // temperature transients without frame-rate-dependent diffs.
    private static void AdvanceChannel(
        double gameDay, int hpd, float halfWidth,
        ref float prev, ref float cur, ref float next, ref long phaseField,
        RandomNumberGenerator rng,
        out float value, out float slopePerDayFraction)
    {
        long currentPhase = CurrentPhase(gameDay, hpd, halfWidth);

        if (phaseField == long.MinValue)
        {
            // First call after WorldState construction — the seeded
            // prev/cur/next triple is what's "in flight" now; just snap
            // to the current phase without rolling.
            phaseField = currentPhase;
        }
        while (phaseField < currentPhase)
        {
            // Rotate the pre-roll chain: prev := the value that just
            // finished its phase, cur := the value pre-rolled one phase
            // ahead (now the current phase's settled value), next :=
            // freshly rolled value for the upcoming phase. The HUD
            // weather widget consumes `next` to forecast tomorrow's
            // day or tonight's night peak before the transition begins.
            prev = cur;
            cur = next;
            next = rng.Randf();
            phaseField++;
        }

        // Day-fraction position of the handover at the center of the
        // current phase's window. distInWindow = 0 at window start,
        // 2*halfWidth at window end, and grows past 2*halfWidth in the
        // hold period before the next window opens (clamped by
        // SmoothStep, so the displayed value sits at cur).
        double handoverGameDay = (double)currentPhase / hpd + 0.25;
        double windowStart = handoverGameDay - halfWidth;
        double distInWindow = gameDay - windowStart;
        float windowSpan = 2f * halfWidth;
        float crossfade = Mathf.SmoothStep(0f, windowSpan, (float)distInWindow);
        value = Mathf.Lerp(prev, cur, crossfade);

        // Analytical slope of SmoothStep across [0, windowSpan]:
        //   d/dx SmoothStep(0, w, x) = 6x(w-x) / w³, for x in [0, w], else 0.
        // Combined with d(value)/d(crossfade) = (cur - prev), we get
        // d(value)/d(gameDay). Normalized to a peak magnitude of 1 by
        // dividing by 1.5 / windowSpan (the SmoothStep slope max), so
        // the returned slope is in the same range as (cur - prev).
        if (distInWindow > 0 && distInWindow < windowSpan)
        {
            float x = (float)distInWindow;
            // 6x(w-x)/w^3 has max 1.5/w at x=w/2. Dividing by that max
            // gives 4x(w-x)/w^2 — clean shape function in [0, 1].
            float shape = 4f * x * (windowSpan - x) / (windowSpan * windowSpan);
            slopePerDayFraction = (cur - prev) * shape;
        }
        else
        {
            slopePerDayFraction = 0f;
        }
    }

    // Advance the WorldState's variance state. Three independent
    // channels — temperature/wind (`WeatherVariance`), humidity, and
    // cloud cover — each with their own prev/cur/next/phase index. Rolls
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
            ref ws.WeatherVariancePrev, ref ws.WeatherVarianceCur, ref ws.WeatherVarianceNext,
            ref ws.WeatherVariancePhase,
            ws.WeatherRng,
            out ws.WeatherVariance, out ws.WeatherVarianceSlope);
        AdvanceChannel(gameDay, hpd, halfWidth,
            ref ws.HumidityVariancePrev, ref ws.HumidityVarianceCur, ref ws.HumidityVarianceNext,
            ref ws.HumidityVariancePhase,
            ws.WeatherRng,
            out ws.HumidityVariance, out ws.HumidityVarianceSlope);
        AdvanceChannel(gameDay, hpd, halfWidth,
            ref ws.CloudVariancePrev, ref ws.CloudVarianceCur, ref ws.CloudVarianceNext,
            ref ws.CloudVariancePhase,
            ws.WeatherRng,
            out ws.CloudVariance, out ws.CloudVarianceSlope);
        AdvanceChannel(gameDay, hpd, halfWidth,
            ref ws.LightningVariancePrev, ref ws.LightningVarianceCur, ref ws.LightningVarianceNext,
            ref ws.LightningVariancePhase,
            ws.WeatherRng,
            out ws.LightningVariance, out ws.LightningVarianceSlope);
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
        Apply(weather, zone, elevation, sim,
            (float)(ws?.TimeOfDay01 ?? 0.5),
            ws?.WeatherVariance ?? 0.5f, ws?.WeatherVarianceSlope ?? 0f,
            ws?.HumidityVariance ?? 0.5f, ws?.CloudVariance ?? 0.5f,
            ws?.LightningVariance ?? 0.5f);
    }

    // Fully-explicit overload used by the HUD weather widget. Lets the
    // caller forecast peak-of-day / trough-of-night conditions with the
    // upcoming phase's pre-rolled variance (WorldState.*VarianceNext)
    // rather than whatever variance is currently in flight. Forecasts
    // pass weatherVarianceSlope = 0: slope is the in-transition forcing
    // term (per-day-fraction signed derivative of the SmoothStep), which
    // only matters while a handover is actually crossing, not for a
    // steady-state preview at peak/trough.
    public static void Apply(WeatherData weather, ZoneData zone, float elevation, SimData sim,
        float timeOfDay01,
        float weatherVariance, float weatherVarianceSlope,
        float humidityVariance, float cloudVariance,
        float lightningVariance)
    {
        if (weather == null || sim == null) { return; }

        float diurnal = DiurnalCurve(timeOfDay01, sim);   // 0=trough, 1=peak
        float diurnalSlope = DiurnalCurveSlope(timeOfDay01, sim);
        float coolDiurnal = 1f - diurnal;                  // 1 at trough, 0 at peak
        float coolingRate = Mathf.Max(0f, -diurnalSlope);  // >0 only when cooling

        // Authored max envelope. Fog has no authored max — WeatherDerivation
        // computes it directly from simulated humidity + cool-diurnal.
        float humidityMax = Mathf.Clamp(weather.humidity, 0f, 1f);
        float tempMax = weather.airTemperature;
        float windMax = Mathf.Max(weather.windSpeed, 0f);
        float cloudMax = Mathf.Clamp(weather.cloudCover, 0f, 1f);
        float rainMax = Mathf.Clamp(weather.rainAmount, 0f, 1f);
        float lightningMax = Mathf.Clamp(weather.lightningAmount, 0f, 1f);
        float dustMax = Mathf.Clamp(zone?.DustAmount ?? 0f, 0f, 1f);
        elevation = Mathf.Clamp(elevation, 0f, 1f);

        // --- Baselines (diurnal-modulated maxes) -------------------------
        // Baseline humidity. Real humidity is HIGHEST at the cool trough
        // (cold air is closer to saturation) and LOWEST at the warm peak,
        // so we use coolDiurnal as the curve. Hot zones and high-
        // elevation zones damp the ceiling.
        float humidityFromTempScale = Mathf.Clamp(tempMax / 104f, 0f, 1f); // ~0..1 over 0..104F
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
        float variance = Mathf.Clamp(weatherVariance, 0f, 1f);
        float varianceCenter = variance - 0.5f;            // -0.5..+0.5
        // Analytical slope of the variance across the sunrise/sunset
        // crossfade window, normalized to a peak magnitude equal to
        // |next - prev| (so |slope| ∈ [0, 1] for the worst-case
        // 0→1 swing). 0 outside the window — wind / temperature
        // transients only fire DURING a frontal handover, not while
        // weather is steady. Frame-rate independent and continuous,
        // so no pops at the variance edges.
        float absVarianceDelta = Mathf.Abs(weatherVarianceSlope);

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
        float humidityVarianceCenter = Mathf.Clamp(humidityVariance, 0f, 1f) - 0.5f;
        float cloudVarianceCenter = Mathf.Clamp(cloudVariance, 0f, 1f) - 0.5f;
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

        // Lightning: three independent storm-mode gates, max-merged.
        // Each gate models a different real-world storm physics
        // routed through variables we already simulate; the strongest
        // signal wins so a zone's character is determined by which
        // variables it authors high. The SmoothStep gates rise softly
        // through a crossfade window, so distant thunder rolls in
        // smoothly as conditions cross thresholds rather than popping.
        //
        // WET: warm humid air with active rain — air-mass / frontal
        //   thunderstorm. Forest, swamp, temperate.
        // DRY: high-base storm in hot arid air — desert summer virga
        //   storms. No rain required; humidity has to be LOW.
        // OROGRAPHIC: strong wind lifting air over high terrain —
        //   mountain ridge-line storms. No rain required.
        //
        // Variance reads through directly (no centering): full 0..1
        // range maps to "no lightning at all" → "full electrical
        // storm" so storms don't come in at half strength on average.
        // lightningMax above 1 doesn't make simLightning exceed 1
        // (final clamp) — it just widens the partial-gate range that
        // still produces a meaningful signal.
        float lightningWetGate =
            Mathf.SmoothStep(sim.LightningCloudThreshold, 1f, simCloud)
            * Mathf.SmoothStep(sim.LightningRainThreshold, 1f, simRain);
        float lightningDryGate =
            Mathf.SmoothStep(sim.DryLightningCloudThreshold, 1f, simCloud)
            * (1f - Mathf.SmoothStep(0f, sim.DryLightningHumidityMax, simHumidity))
            * Mathf.SmoothStep(sim.DryLightningTempMin, sim.DryLightningTempMax, simTemp);
        float lightningOrographicGate =
            Mathf.SmoothStep(sim.OrographicLightningCloudThreshold, 1f, simCloud)
            * Mathf.SmoothStep(sim.OrographicLightningWindMin, sim.OrographicLightningWindMax, simWind)
            * Mathf.SmoothStep(sim.OrographicLightningElevationMin, 1f, elevation);
        float lightningGateAny = Mathf.Max(lightningWetGate,
            Mathf.Max(lightningDryGate, lightningOrographicGate));
        float lightningVarianceFactor = Mathf.Clamp(lightningVariance, 0f, 1f);
        float simLightning = Mathf.Clamp(
            lightningMax * lightningGateAny * lightningVarianceFactor,
            0f, 1f);

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
        // (degrees F) flow through unchanged.
        weather.humidity = simHumidity;
        weather.airTemperature = simTemp;
        weather.windSpeed = simWind;
        weather.cloudCover = simCloud;
        weather.rainAmount = simRain;
        weather.lightningAmount = simLightning;
        weather.dustAmount = simDust;

        // Debug overrides applied AFTER the sim path so a force-on
        // value reaches every downstream consumer (audio, sky flash,
        // HUD forecast icon) without each having to query the CVar.
        if (ForceLightningOverride.HasValue)
        {
            weather.lightningAmount = Mathf.Clamp(ForceLightningOverride.Value, 0f, 1f);
        }
    }

}
