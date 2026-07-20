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
    public static float DiurnalCurve(float timeOfDay01, SimData simData)
    {
        float peak = simData?.diurnalPeak01 ?? 0.5f;
        float halfWidth = Mathf.Clamp(simData?.diurnalPlateauHalfWidth ?? 0.125f, 0f, 0.249f);
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
    public static float DiurnalCurveSlope(float timeOfDay01, SimData simData)
    {
        float peak = simData?.diurnalPeak01 ?? 0.5f;
        float halfWidth = Mathf.Clamp(simData?.diurnalPlateauHalfWidth ?? 0.125f, 0f, 0.249f);
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

    // Fraction [0, 1] of the day→night weather crossfade at `timeOfDay01`:
    // 0 before the sunset window opens, ramping to 1 across it (centered on
    // SunsetTimeOfDay01), then held at 1 through the night. `slopeShape` is the
    // SmoothStep derivative normalized to a [0, 1] peak (matches the old handover
    // shape), so the wind "frontal kick" reads a frame-rate-independent signal.
    // Sunrise has NO crossfade — the night→day handover happened while the player
    // slept, so a fresh day simply starts at the day slot.
    public static float SunsetBlend(float timeOfDay01, SimData simData, out float slopeShape)
    {
        float hw = Mathf.Max(simData?.varianceCrossfadeHalfWidth01 ?? 0.05f, 1e-4f);
        float a0 = (float)WorldState.SunsetTimeOfDay01 - hw;
        float a1 = (float)WorldState.SunsetTimeOfDay01 + hw;
        float blend = Mathf.SmoothStep(a0, a1, timeOfDay01);
        if (timeOfDay01 > a0 && timeOfDay01 < a1)
        {
            float x = timeOfDay01 - a0;
            float w = a1 - a0;
            // 6x(w-x)/w³ peaks at 1.5/w; ×(w²/1.5·2/w)… normalize to 4x(w-x)/w².
            slopeShape = 4f * x * (w - x) / (w * w);
        }
        else
        {
            slopeShape = 0f;
        }
        return blend;
    }

    // Compute the active (sunset-crossfaded) variance for the current frame from
    // the day/night slots pre-rolled at sunrise (WorldState.RollDailyWeather).
    // Before sunset the DAY slot is in effect; across the sunset window it
    // crossfades to the NIGHT slot; after, the night slot holds until the next
    // sleep re-rolls both. WeatherVarianceSlope carries the crossfade's signed
    // slope (day→night delta × shape) for the wind transient — nonzero only at
    // sunset. Channels are decoupled so a humid front needn't coincide with a
    // temperature swing; humidity/cloud effects are wind-gated in Apply.
    public static void UpdateVariance(WorldState ws, SimData simData)
    {
        if (ws == null || simData == null) { return; }
        float blend = SunsetBlend((float)ws.TimeOfDay01, simData, out float slopeShape);
        ws.WeatherVariance = Mathf.Lerp(ws.DayWeatherVariance, ws.NightWeatherVariance, blend);
        ws.HumidityVariance = Mathf.Lerp(ws.DayHumidityVariance, ws.NightHumidityVariance, blend);
        ws.CloudVariance = Mathf.Lerp(ws.DayCloudVariance, ws.NightCloudVariance, blend);
        ws.LightningVariance = Mathf.Lerp(ws.DayLightningVariance, ws.NightLightningVariance, blend);
        ws.WeatherVarianceSlope = (ws.NightWeatherVariance - ws.DayWeatherVariance) * slopeShape;
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
    public static void Apply(WeatherData weather, ZoneData zone, float elevation, WorldState ws, SimData simData)
    {
        if (ws == null)
        {
            Apply(weather, zone, elevation, simData, 0.5f,
                0.5f, 0f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f);
            return;
        }
        float tod = (float)ws.TimeOfDay01;
        float hw = Mathf.Max(simData?.varianceCrossfadeHalfWidth01 ?? 0.05f, 1e-4f);
        // Destination = the slot we're settling INTO: the day slot before the
        // sunset window opens, the night slot once it has — so the storm gate
        // (destinationLightningAmount) reads the stable target rather than a
        // mid-crossfade blip.
        bool headingNight = tod >= (float)WorldState.SunsetTimeOfDay01 - hw;
        float destWeather = headingNight ? ws.NightWeatherVariance : ws.DayWeatherVariance;
        float destHumidity = headingNight ? ws.NightHumidityVariance : ws.DayHumidityVariance;
        float destCloud = headingNight ? ws.NightCloudVariance : ws.DayCloudVariance;
        float destLightning = headingNight ? ws.NightLightningVariance : ws.DayLightningVariance;
        // The inner overload's timeOfDay01 feeds only DiurnalCurve, which is
        // authored in orbit-phase (peak at noon = 0.5), so pass the remapped
        // phase rather than the raw awake-day tod.
        Apply(weather, zone, elevation, simData, (float)WorldState.OrbitPhase01(tod),
            ws.WeatherVariance, ws.WeatherVarianceSlope,
            ws.HumidityVariance, ws.CloudVariance, ws.LightningVariance,
            destWeather, destHumidity, destCloud, destLightning);
    }

    // Fully-explicit overload used by the HUD weather widget. Lets the
    // caller forecast peak-of-day / trough-of-night conditions with the
    // upcoming phase's pre-rolled variance (WorldState.*VarianceNext)
    // rather than whatever variance is currently in flight. Forecasts
    // pass weatherVarianceSlope = 0: slope is the in-transition forcing
    // term (per-day-fraction signed derivative of the SmoothStep), which
    // only matters while a handover is actually crossing, not for a
    // steady-state preview at peak/trough.
    public static void Apply(WeatherData weather, ZoneData zone, float elevation, SimData simData,
        float timeOfDay01,
        float weatherVariance, float weatherVarianceSlope,
        float humidityVariance, float cloudVariance,
        float lightningVariance,
        float destWeatherVariance,
        float destHumidityVariance, float destCloudVariance,
        float destLightningVariance)
    {
        if (weather == null || simData == null) { return; }
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
        ComputeChannelValuesAtDiurnal(weather, zone, elevation, simData,
            diurnal: 1f,
            weatherVariance, weatherVarianceSlope, humidityVariance, cloudVariance, lightningVariance,
            out float peakHumidity, out float peakTemp, out float peakWind,
            out float peakCloud, out float peakRain, out float peakLightning, out float peakDust);
        ComputeChannelValuesAtDiurnal(weather, zone, elevation, simData,
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
        ComputeChannelValuesAtDiurnal(weather, zone, elevation, simData,
            diurnal: 1f,
            destWeatherVariance, 0f, destHumidityVariance, destCloudVariance, destLightningVariance,
            out _, out _, out _, out _, out _, out float peakDestLightning, out _);
        ComputeChannelValuesAtDiurnal(weather, zone, elevation, simData,
            diurnal: 0f,
            destWeatherVariance, 0f, destHumidityVariance, destCloudVariance, destLightningVariance,
            out _, out _, out _, out _, out _, out float troughDestLightning, out _);

        float diurnal = DiurnalCurve(timeOfDay01, simData);   // 0 = night plateau, 1 = day plateau
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
    private static void ComputeChannelValuesAtDiurnal(WeatherData weather, ZoneData zone, float elevation, SimData simData,
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
        float dustMax = Mathf.Clamp(zone?.dustAmount ?? 0f, 0f, 1f);

        // Baseline humidity. Real humidity is HIGHEST at the night plateau
        // (cold air is closer to saturation) and LOWEST at the day plateau.
        float humidityFromTempScale = Mathf.Clamp(tempMax / 104f, 0f, 1f); // ~0..1 over 0..104F
        float baseHumidityCeiling = humidityMax
            * (1f - elevation * simData.humidityFromElevation)
            * (1f - humidityFromTempScale * simData.humidityFromMaxTemp);
        float baselineHumidity = Mathf.Clamp(
            baseHumidityCeiling * Mathf.Lerp(1f, coolDiurnal, simData.humidityDiurnalDepth),
            0f, 1f);

        // Baseline temperature. Humid air damps the day↔night swing.
        float tempSwing = simData.tempDiurnalDepth * (1f - baselineHumidity * simData.tempHumidityDamping);
        float tempEnvelope = tempMax * (1f - elevation * simData.tempFromElevation);
        float baselineTemp = tempEnvelope * (1f - tempSwing) + tempEnvelope * tempSwing * diurnal;

        // Baseline wind. NO cooling-rate forcing term — at plateaus the
        // diurnal slope is zero, so that contribution is zero by
        // construction. WindDiurnalDepth still scales between plateaus.
        float windDiurnalScale = Mathf.Lerp(1f - simData.windDiurnalDepth, 1f, diurnal);
        float baselineWind = Mathf.Max(0f, windMax
            * windDiurnalScale
            * (1f + elevation * simData.windFromElevation));

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
            * (1f - varianceCenter * 2f * simData.windVarianceK)
            * (1f + absVarianceDelta * simData.windVarianceDeltaK));

        simTemp = baselineTemp
            * (1f + varianceCenter * 2f * simData.tempVarianceK)
            * (1f - absVarianceDelta * simData.tempVarianceDeltaK);

        // Humidity and cloud variance effects are wind-gated (advection).
        float windGate = Mathf.Clamp(simWind / Mathf.Max(simData.advectedVarianceWindRef, 1e-3f), 0f, 1f);
        float humidityVarianceCenter = Mathf.Clamp(humidityVariance, 0f, 1f) - 0.5f;
        float cloudVarianceCenter = Mathf.Clamp(cloudVariance, 0f, 1f) - 0.5f;
        simHumidity = Mathf.Clamp(
            baselineHumidity * (1f - humidityVarianceCenter * 2f * simData.humidityVarianceK * windGate),
            0f, 1f);

        // Cloud is the sum of STRATIFORM (authored, system-driven) and
        // CONVECTIVE (derived from warm humid air rising). Stratiform
        // persists day and night — that's what allows rainy nights.
        // Convective peaks in the afternoon (× diurnal) and vanishes
        // overnight, layering afternoon cumulus on top of any stratiform
        // overcast. Variance perturbs the stratiform channel only;
        // convective is pure physics from the simulated humidity.
        float windFraction = windMax > 1e-3f ? Mathf.Clamp(baselineWind / Mathf.Max(windMax, 1e-3f), 0f, 2f) : 0f;
        float stratiformBaseline = cloudMax * (1f + (windFraction - 1f) * simData.cloudFromWind);
        float stratiformCloud = stratiformBaseline * (1f - cloudVarianceCenter * 2f * simData.cloudVarianceK * windGate);
        float convectiveCloud = simHumidity * diurnal * simData.convectiveStrength;
        simCloud = Mathf.Clamp(stratiformCloud + convectiveCloud, 0f, 1f);

        // Rain: pure cloud-gate × authored max. No coolingRate boost —
        // removing it eliminates the in-ramp spike. Rain saturates at 1.0
        // when conditions fully meet the threshold.
        float rainCloudGate = Mathf.SmoothStep(simData.rainCloudThreshold, 1f, simCloud);
        float rainSignal = rainCloudGate * simData.rainFromCloudCover;
        simRain = Mathf.Clamp(rainMax * rainSignal, 0f, 1f);

        // Lightning: three storm-mode gates, max-merged. Same as before.
        float lightningWetGate =
            Mathf.SmoothStep(simData.lightningCloudThreshold, 1f, simCloud)
            * Mathf.SmoothStep(simData.lightningRainThreshold, 1f, simRain);
        float lightningDryGate =
            Mathf.SmoothStep(simData.dryLightningCloudThreshold, 1f, simCloud)
            * (1f - Mathf.SmoothStep(0f, simData.dryLightningHumidityMax, simHumidity))
            * Mathf.SmoothStep(simData.dryLightningTempMin, simData.dryLightningTempMax, simTemp);
        float lightningOrographicGate =
            Mathf.SmoothStep(simData.orographicLightningCloudThreshold, 1f, simCloud)
            * Mathf.SmoothStep(simData.orographicLightningWindMin, simData.orographicLightningWindMax, simWind)
            * Mathf.SmoothStep(simData.orographicLightningElevationMin, 1f, elevation);
        float lightningGateAny = Mathf.Max(lightningWetGate,
            Mathf.Max(lightningDryGate, lightningOrographicGate));
        float lightningVarianceFactor = Mathf.Clamp(lightningVariance, 0f, 1f);
        simLightning = Mathf.Clamp(
            lightningMax * lightningGateAny * lightningVarianceFactor,
            0f, 1f);

        // Dust: wind-lifted, elevation/warmth-boosted, humidity/rain-suppressed.
        float windLift = windMax > 1e-3f ? Mathf.Clamp(simWind / windMax, 0f, 2f) : 0f;
        float dustSignal = windLift * simData.dustFromWind
            * (1f + elevation * simData.dustFromElevation)
            * (1f + diurnal * simData.dustFromWarmth);
        float dustSuppress = (1f - simHumidity * simData.dustHumiditySuppression)
            * (1f - simRain * simData.dustRainSuppression);
        simDust = Mathf.Clamp(dustMax * dustSignal * dustSuppress, 0f, 1f);
    }

    // Public single-plateau pass. Writes the steady-state channel values
    // for the given diurnal position (0 or 1) into `weather`, bypassing
    // the trough/peak lerp Apply does. Used by the HUD weather icon so
    // it can read the day-plateau and night-plateau weather directly
    // without paying for the lerp (and without showing a value that
    // moves through the diurnal ramps).
    public static void ApplyAtDiurnal(WeatherData weather, ZoneData zone, float elevation, SimData simData,
        float diurnal,
        float weatherVariance, float weatherVarianceSlope,
        float humidityVariance, float cloudVariance, float lightningVariance)
    {
        if (weather == null || simData == null) { return; }
        elevation = Mathf.Clamp(elevation, 0f, 1f);
        // Forecast path: caller selected a single variance set (cur or
        // next) for the plateau they want to forecast — there's no
        // "displayed vs destination" distinction here. `weather.lightningAmount`
        // gets the forecast value; `destinationLightningAmount` is left
        // untouched (the HUD doesn't consume it).
        ComputeChannelValuesAtDiurnal(weather, zone, elevation, simData,
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
