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
        // Destination variances are the end-of-crossfade `Cur` values for
        // every channel — every variance channel rotates on the same
        // sunrise/sunset handover, so by the time the in-flight lerp
        // commits, ALL channels are sitting at their respective Cur
        // values. Slope is implicitly 0 at the destination (the chain
        // has settled).
        Apply(weather, zone, elevation, sim,
            (float)(ws?.TimeOfDay01 ?? 0.5),
            ws?.WeatherVariance ?? 0.5f, ws?.WeatherVarianceSlope ?? 0f,
            ws?.HumidityVariance ?? 0.5f, ws?.CloudVariance ?? 0.5f,
            ws?.LightningVariance ?? 0.5f,
            ws?.WeatherVarianceCur ?? 0.5f,
            ws?.HumidityVarianceCur ?? 0.5f,
            ws?.CloudVarianceCur ?? 0.5f,
            ws?.LightningVarianceCur ?? 0.5f);
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
        float lightningVariance,
        float destWeatherVariance,
        float destHumidityVariance, float destCloudVariance,
        float destLightningVariance)
    {
        if (weather == null || sim == null) { return; }
        elevation = Mathf.Clamp(elevation, 0f, 1f);

        // FOUR Compute calls: peak/trough × displayed/destination.
        //
        // The DISPLAYED pair uses the in-flight crossfade values
        // (WorldState.*Variance, *VarianceSlope) and produces the
        // weather state currently in effect.
        //
        // The DESTINATION pair uses the end-of-crossfade `*VarianceCur`
        // values with slope=0 (the chain has settled by definition at
        // the destination), and produces the weather state we'll have
        // once the active sunrise/sunset handover completes. All four
        // variance channels share the same handover window, so ALL of
        // them are settled at the destination — not just lightning.
        // Consumers (ThunderScheduler, WeatherLightningSpawner) read
        // weather.destinationLightningAmount to gate "are we lerping
        // TOWARD a real storm" so a transient mid-lerp blip that
        // happens to pass through the lightning gate doesn't fire
        // thunder or strikes.
        //
        // ALL FOUR CALLS HAPPEN BEFORE ANY WRITEBACK. ComputeChannelValuesAtDiurnal
        // reads weather.X as the zone-blended MAX for each channel
        // (the inputs); writing displayed values to weather.X first
        // would corrupt those inputs for the destination pass. Reads
        // are batched, then writes follow.
        //
        // Cost: 4 × Compute is a few dozen multiplies / smoothsteps —
        // not a hot path, runs once per frame on the main blended
        // WeatherData. ApplyAtDiurnal (HUD forecast) and the variance
        // values being predictable from WorldState mean nothing here
        // is performance-critical or approximate.
        ComputeChannelValuesAtDiurnal(weather, zone, elevation, sim,
            diurnal: 1f,
            weatherVariance, weatherVarianceSlope, humidityVariance, cloudVariance, lightningVariance,
            out float peakHumidity, out float peakTemp, out float peakWind,
            out float peakCloud, out float peakRain, out float peakLightning, out float peakDust);
        ComputeChannelValuesAtDiurnal(weather, zone, elevation, sim,
            diurnal: 0f,
            weatherVariance, weatherVarianceSlope, humidityVariance, cloudVariance, lightningVariance,
            out float troughHumidity, out float troughTemp, out float troughWind,
            out float troughCloud, out float troughRain, out float troughLightning, out float troughDust);
        // Destination pass: ALL channels at their *VarianceCur values,
        // slope=0 (settled). We discard the non-lightning outputs —
        // only destinationLightningAmount is consumed downstream — but
        // a unified Compute keeps the math identical to the displayed
        // pass, so the destination value reflects the real
        // cloud/rain/wind-gated lightning amount once the chain
        // settles, not an approximation that mixes in-flight cloud
        // with settled lightning.
        ComputeChannelValuesAtDiurnal(weather, zone, elevation, sim,
            diurnal: 1f,
            destWeatherVariance, 0f, destHumidityVariance, destCloudVariance, destLightningVariance,
            out _, out _, out _, out _, out _, out float peakDestLightning, out _);
        ComputeChannelValuesAtDiurnal(weather, zone, elevation, sim,
            diurnal: 0f,
            destWeatherVariance, 0f, destHumidityVariance, destCloudVariance, destLightningVariance,
            out _, out _, out _, out _, out _, out float troughDestLightning, out _);

        float diurnal = DiurnalCurve(timeOfDay01, sim);   // 0 = night plateau, 1 = day plateau
        weather.humidity = Mathf.Lerp(troughHumidity, peakHumidity, diurnal);
        weather.airTemperature = Mathf.Lerp(troughTemp, peakTemp, diurnal);
        weather.windSpeed = Mathf.Lerp(troughWind, peakWind, diurnal);
        weather.cloudCover = Mathf.Lerp(troughCloud, peakCloud, diurnal);
        weather.rainAmount = Mathf.Lerp(troughRain, peakRain, diurnal);
        weather.lightningAmount = Mathf.Lerp(troughLightning, peakLightning, diurnal);
        weather.destinationLightningAmount = Mathf.Lerp(troughDestLightning, peakDestLightning, diurnal);
        weather.dustAmount = Mathf.Lerp(troughDust, peakDust, diurnal);

        // Debug override applied AFTER the lerp so a force-on value
        // reaches every downstream consumer (audio, sky flash, HUD icon).
        // Force the destination value too so the storm-gate sees the
        // override immediately rather than waiting for the next variance
        // handover.
        if (ForceLightningOverride.HasValue)
        {
            float forced = Mathf.Clamp(ForceLightningOverride.Value, 0f, 1f);
            weather.lightningAmount = forced;
            weather.destinationLightningAmount = forced;
        }
    }

    // Compute the steady-state channel values for a given diurnal position
    // (0 = night plateau, 1 = day plateau). No slope-derived boost: at
    // plateaus the diurnal curve's derivative is zero by construction, so
    // any term that previously depended on coolingRate / diurnalSlope is
    // simply omitted here. Variance multipliers can push outputs above the
    // authored zone maxes — that's variance doing its job; the final
    // saturation cap is at the absolute 0..1 range.
    private static void ComputeChannelValuesAtDiurnal(WeatherData weather, ZoneData zone, float elevation, SimData sim,
        float diurnal,
        float weatherVariance, float weatherVarianceSlope,
        float humidityVariance, float cloudVariance,
        float lightningVariance,
        out float simHumidity, out float simTemp, out float simWind,
        out float simCloud, out float simRain, out float simLightning, out float simDust)
    {
        float coolDiurnal = 1f - diurnal;

        // Authored zone maxes.
        float humidityMax = Mathf.Clamp(weather.humidity, 0f, 1f);
        float tempMax = weather.airTemperature;
        float windMax = Mathf.Max(weather.windSpeed, 0f);
        float cloudMax = Mathf.Clamp(weather.cloudCover, 0f, 1f);
        float rainMax = Mathf.Clamp(weather.rainAmount, 0f, 1f);
        float lightningMax = Mathf.Clamp(weather.lightningAmount, 0f, 1f);
        float dustMax = Mathf.Clamp(zone?.DustAmount ?? 0f, 0f, 1f);

        // Baseline humidity. Real humidity is HIGHEST at the night plateau
        // (cold air is closer to saturation) and LOWEST at the day plateau.
        float humidityFromTempScale = Mathf.Clamp(tempMax / 104f, 0f, 1f); // ~0..1 over 0..104F
        float baseHumidityCeiling = humidityMax
            * (1f - elevation * sim.HumidityFromElevation)
            * (1f - humidityFromTempScale * sim.HumidityFromMaxTemp);
        float baselineHumidity = Mathf.Clamp(
            baseHumidityCeiling * Mathf.Lerp(1f, coolDiurnal, sim.HumidityDiurnalDepth),
            0f, 1f);

        // Baseline temperature. Humid air damps the day↔night swing.
        float tempSwing = sim.TempDiurnalDepth * (1f - baselineHumidity * sim.TempHumidityDamping);
        float tempEnvelope = tempMax * (1f - elevation * sim.TempFromElevation);
        float baselineTemp = tempEnvelope * (1f - tempSwing) + tempEnvelope * tempSwing * diurnal;

        // Baseline wind. NO cooling-rate forcing term — at plateaus the
        // diurnal slope is zero, so that contribution is zero by
        // construction. WindDiurnalDepth still scales between plateaus.
        float windDiurnalScale = Mathf.Lerp(1f - sim.WindDiurnalDepth, 1f, diurnal);
        float baselineWind = Mathf.Max(0f, windMax
            * windDiurnalScale
            * (1f + elevation * sim.WindFromElevation));

        // Variance perturbation. The variance crossfade |slope| frontal
        // kick on wind is preserved here — that slope is the VARIANCE
        // slope (sunrise/sunset handover), not the diurnal slope, so it
        // can be non-zero even at plateau diurnal values.
        float variance = Mathf.Clamp(weatherVariance, 0f, 1f);
        float varianceCenter = variance - 0.5f;
        float absVarianceDelta = Mathf.Abs(weatherVarianceSlope);

        // Wind: bidirectional center term + non-negative |slope| frontal
        // kick. Variance can legitimately push above baseline; only the
        // absolute zero floor is enforced.
        simWind = Mathf.Max(0f, baselineWind
            * (1f - varianceCenter * 2f * sim.WindVarianceK)
            * (1f + absVarianceDelta * sim.WindVarianceDeltaK));

        simTemp = baselineTemp
            * (1f + varianceCenter * 2f * sim.TempVarianceK)
            * (1f - absVarianceDelta * sim.TempVarianceDeltaK);

        // Humidity and cloud variance effects are wind-gated (advection).
        float windGate = Mathf.Clamp(simWind / Mathf.Max(sim.AdvectedVarianceWindRef, 1e-3f), 0f, 1f);
        float humidityVarianceCenter = Mathf.Clamp(humidityVariance, 0f, 1f) - 0.5f;
        float cloudVarianceCenter = Mathf.Clamp(cloudVariance, 0f, 1f) - 0.5f;
        simHumidity = Mathf.Clamp(
            baselineHumidity * (1f - humidityVarianceCenter * 2f * sim.HumidityVarianceK * windGate),
            0f, 1f);

        // Cloud is the sum of STRATIFORM (authored, system-driven) and
        // CONVECTIVE (derived from warm humid air rising). Stratiform
        // persists day and night — that's what allows rainy nights.
        // Convective peaks in the afternoon (× diurnal) and vanishes
        // overnight, layering afternoon cumulus on top of any stratiform
        // overcast. Variance perturbs the stratiform channel only;
        // convective is pure physics from the simulated humidity.
        float windFraction = windMax > 1e-3f ? Mathf.Clamp(baselineWind / Mathf.Max(windMax, 1e-3f), 0f, 2f) : 0f;
        float stratiformBaseline = cloudMax * (1f + (windFraction - 1f) * sim.CloudFromWind);
        float stratiformCloud = stratiformBaseline * (1f - cloudVarianceCenter * 2f * sim.CloudVarianceK * windGate);
        float convectiveCloud = simHumidity * diurnal * sim.ConvectiveStrength;
        simCloud = Mathf.Clamp(stratiformCloud + convectiveCloud, 0f, 1f);

        // Rain: pure cloud-gate × authored max. No coolingRate boost —
        // removing it eliminates the in-ramp spike. Rain saturates at 1.0
        // when conditions fully meet the threshold.
        float rainCloudGate = Mathf.SmoothStep(sim.RainCloudThreshold, 1f, simCloud);
        float rainSignal = rainCloudGate * sim.RainFromCloudCover;
        simRain = Mathf.Clamp(rainMax * rainSignal, 0f, 1f);

        // Lightning: three storm-mode gates, max-merged. Same as before.
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
        simLightning = Mathf.Clamp(
            lightningMax * lightningGateAny * lightningVarianceFactor,
            0f, 1f);

        // Dust: wind-lifted, elevation/warmth-boosted, humidity/rain-suppressed.
        float windLift = windMax > 1e-3f ? Mathf.Clamp(simWind / windMax, 0f, 2f) : 0f;
        float dustSignal = windLift * sim.DustFromWind
            * (1f + elevation * sim.DustFromElevation)
            * (1f + diurnal * sim.DustFromWarmth);
        float dustSuppress = (1f - simHumidity * sim.DustHumiditySuppression)
            * (1f - simRain * sim.DustRainSuppression);
        simDust = Mathf.Clamp(dustMax * dustSignal * dustSuppress, 0f, 1f);
    }

    // Public single-plateau pass. Writes the steady-state channel values
    // for the given diurnal position (0 or 1) into `weather`, bypassing
    // the trough/peak lerp Apply does. Used by the HUD weather icon so
    // it can read the day-plateau and night-plateau weather directly
    // without paying for the lerp (and without showing a value that
    // moves through the diurnal ramps).
    public static void ApplyAtDiurnal(WeatherData weather, ZoneData zone, float elevation, SimData sim,
        float diurnal,
        float weatherVariance, float weatherVarianceSlope,
        float humidityVariance, float cloudVariance, float lightningVariance)
    {
        if (weather == null || sim == null) { return; }
        elevation = Mathf.Clamp(elevation, 0f, 1f);
        // Forecast path: caller selected a single variance set (cur or
        // next) for the plateau they want to forecast — there's no
        // "displayed vs destination" distinction here. `weather.lightningAmount`
        // gets the forecast value; `destinationLightningAmount` is left
        // untouched (the HUD doesn't consume it).
        ComputeChannelValuesAtDiurnal(weather, zone, elevation, sim,
            diurnal, weatherVariance, weatherVarianceSlope, humidityVariance, cloudVariance, lightningVariance,
            out float h, out float t, out float w, out float c, out float r, out float l, out float d);
        weather.humidity = h;
        weather.airTemperature = t;
        weather.windSpeed = w;
        weather.cloudCover = c;
        weather.rainAmount = r;
        weather.lightningAmount = l;
        weather.dustAmount = d;
    }

}
