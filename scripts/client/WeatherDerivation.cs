using Godot;

// Pure function: (zone, weather, time-of-day, tuning) → DerivedPalette.
// The phase blend (day/sunset/night) happens INSIDE here, so callers
// receive one already-blended value per visual channel. The sun-vs-moon
// horizon crossfade for shafts and DirectionalLight3D energy stays in
// SkyController — that fade is a shadow-quality sculpt, not weather.
//
// No node references, no side effects. SkyController reads and writes;
// derivation just computes. Everything is tunable through SimData's
// Weather Derivation Tuning export group.
public static class WeatherDerivation
{
    // Blend weights for the three time-of-day phases from sun elevation.
    // sunsetT is a symmetric trapezoid centered on the horizon: full warmth
    // across |elev| <= plateau (so pre-dawn and post-dawn both stay warm across
    // the horizon crossing), fading out over SunsetColorRangeDegrees on each side.
    //
    // The plateau is its OWN knob, deliberately narrow, and NOT SunsetAngleDegrees
    // (25°). Every caller applies sunsetT as the final lerp — `day.Lerp(night,
    // nightT).Lerp(sunset, sunsetT)` — so wherever sunsetT saturates it discards
    // the day/night blend entirely. Keyed to SunsetAngleDegrees it saturated
    // across a 50°-wide band around the horizon, which held the sky at full
    // near-white sunset colour from well before sunset until long after (measured:
    // still (1.0, 0.896, 0.794) at tod 0.554), and the water mirrored that as a
    // sheet of white. Keep the plateau small enough that nightT has taken over by
    // the time it releases.
    private static void PhaseWeights(float sunElevDeg, float sunsetAngle, float colorRange, float sunsetPlateau, out float nightT, out float sunsetT)
    {
        colorRange = Mathf.Max(colorRange, 0.01f);
        float dayNightThreshold = sunsetAngle + colorRange;
        nightT = 1f - Mathf.SmoothStep(-dayNightThreshold, dayNightThreshold, sunElevDeg);

        sunsetPlateau = Mathf.Max(sunsetPlateau, 0f);
        sunsetT = 1f - Mathf.SmoothStep(sunsetPlateau, sunsetPlateau + colorRange, Mathf.Abs(sunElevDeg));
    }

    // `timeOfDay01` is the day clock (0 = sunrise … 1 = the next sunrise), NOT the
    // orbit phase — the nightfall pass at the bottom keys off clock position, and
    // only the diurnal weather curve wants the remapped phase.
    public static DerivedPalette Derive(ZoneData zone, WeatherData weather, float sunElevationDegrees, float timeOfDay01, SimData simData)
    {
        DerivedPalette p = default;
        float orbitPhase01 = (float)WorldState.OrbitPhase01(timeOfDay01);

        // Safe fallbacks so editor previews render even before zones
        // / weather / sim are fully wired.
        Color sunC = zone?.sunColor ?? new Color(1.0f, 0.96f, 0.88f);
        Color moonC = zone?.moonColor ?? new Color(0.55f, 0.6f, 0.75f);
        Color skyC = zone?.skyColor ?? new Color(0.25f, 0.48f, 0.82f);
        Color dustC = zone?.dustColor ?? new Color(0.85f, 0.78f, 0.6f);
        p.DustColor = dustC;

        float cloudCover = weather?.cloudCover ?? 0f;
        float humidity = weather?.humidity ?? 0.5f;
        float rainAmount = weather?.rainAmount ?? 0f;
        float dustAmount = weather?.dustAmount ?? 0.1f;
        float windSpeed = weather?.windSpeed ?? 0f;

        // Fog — emergent from simulated weather, no authored input; the single
        // canonical fog signal SkyController's disk / water reads pick up via
        // p.Fog below. Physically fog is air reaching SATURATION (relative
        // humidity → 100%, i.e. cooled OR moistened to its dew point):
        //   - the air mass's moisture is the vapor that must already be present —
        //     the multiplicative necessity (no moisture, no fog, ever), which also
        //     keeps fog out of dry desert / mountain zones whatever the trigger
        //     (the reason this stays a product, not a plain additive sum). Taken as
        //     the WETTER of the zone's authored climate humidity and the live
        //     (advected) humidity: the climate value is the time-invariant "is this
        //     a humid PLACE" floor — keyed to climate, not the live value alone,
        //     because the live humidity is diurnally suppressed (warm midday air
        //     holds more before saturating) and gating on it double-counted the
        //     day/night curve coolGate already carries — while the max lets a moist
        //     air mass blown in from elsewhere lift fog potential above the local
        //     baseline (humidity advection is its own fog source, not just local).
        //   - THREE independent routes then push that vapor to saturation, so fog
        //     forms at any temperature for different reasons:
        //       radiation fog     — nocturnal / clear-sky cooling drops the air to
        //                           its dew point (coolGate); burns off by day.
        //       precipitation fog — rain evaporating into the sub-cloud air
        //                           saturates it (rainAmount); any temperature.
        //       evaporative fog   — standing water / saturated ground (the fog_map's
        //                           domain) re-saturates the air from below, so a
        //                           humid place fogs with NEITHER cooling nor rain —
        //                           a swamp misty on a calm clear afternoon; wind
        //                           disperses it.
        //     Combined as a probabilistic union (1-(1-a)(1-b)(1-c)) so the routes
        //     reinforce — a cool rainy night is foggiest — without any being
        //     required. fog_map then localizes the result per-voxel downstream.
        // FogFromHumidity shapes the climate-moisture gate; RadiationFogSharpness
        // shapes the cooling route; EvaporativeFogStrength caps the evaporative
        // route below full so radiation/precipitation stay the HEAVIEST fog (most
        // fog stays diurnal). Rain has no exponent.
        float fogFromHumidity = simData?.fogFromHumidity ?? 1.5f;
        float radiationFogSharpness = simData?.radiationFogSharpness ?? 1.0f;
        float evaporativeStrength = simData?.evaporativeFogStrength ?? 0.35f;
        float coolDiurnal = 1f - WeatherSimulation.DiurnalCurve(orbitPhase01, simData);
        // Air-mass moisture: the wetter of this place's climate humidity (the value
        // worldgen bakes the fog_map from) and the live advected humidity. Editor
        // preview has no zone, so it falls back to the live value alone.
        float climateHumidity = zone?.weather?.humidity ?? humidity;
        float airMassMoisture = Mathf.Max(climateHumidity, humidity);
        float humidGate = airMassMoisture > 0f ? Mathf.Pow(airMassMoisture, fogFromHumidity) : 0f;
        float coolGate = coolDiurnal > 0f ? Mathf.Pow(coolDiurnal, radiationFogSharpness) : 0f;
        // Evaporative route: a persistent ground-moisture saturation source, dialed
        // down by wind (normalized against the zone's own typical wind so a calm
        // basin clears at a gentler breeze than a gusty one).
        float windMax = zone?.weather?.windSpeed ?? 0f;
        float windFraction = windMax > 0.01f ? Mathf.Clamp(windSpeed / windMax, 0f, 1f) : 0f;
        float evaporative = evaporativeStrength * (1f - windFraction);
        // Any route alone can saturate the air; together they reinforce.
        float saturation = 1f - (1f - coolGate) * (1f - rainAmount) * (1f - evaporative);
        float fog = Mathf.Clamp(humidGate * saturation, 0f, 1f);
        // Low-end dead-zone: trace fog (below FogFloor) collapses to 0, the
        // remainder rescaled to [0,1]. Keeps the concave AmbientFog curve from
        // turning a nearly-dry desert's residual humidity into visible haze;
        // heavy fog is barely touched. Applied at the source so the disk /
        // water fog reads agree that a dry zone has no fog.
        float fogFloor = simData?.fogFloor ?? 0.1f;
        fog = fogFloor < 1f ? Mathf.Clamp((fog - fogFloor) / (1f - fogFloor), 0f, 1f) : 0f;
        p.Fog = fog;

        // Phase weights (day / sunset / night).
        float sunsetAngle = simData?.sunsetAngleDegrees ?? 15f;
        float colorRange = simData?.sunsetColorRangeDegrees ?? 10f;
        float sunsetPlateau = simData?.sunsetColorPlateauDegrees ?? 4f;
        PhaseWeights(sunElevationDegrees, sunsetAngle, colorRange, sunsetPlateau, out float nightT, out float sunsetT);

        // Combined atmospheric haze — used everywhere fills / fog / etc.
        // need to shift with "how thick is the air today".
        float atmHaze = Mathf.Clamp(humidity + fog + dustAmount, 0f, 1f);

        // --- Sunset primary color -----------------------------------
        // SunColor shifted toward an amber target, with extra dust-
        // driven push toward DustColor. The amber bias scales with
        // dustAmount so a clean sky keeps a gentler sunset.
        float sunsetWarmth = simData?.sunsetWarmthBias ?? 0.35f;
        float sunsetDustBias = simData?.sunsetDustBias ?? 0.35f;
        Color sunsetAmber = simData?.sunsetAmberTarget ?? new Color(1.0f, 0.5f, 0.2f);
        // Dust DEEPENS the amber; it is not a prerequisite for it. The old
        // (0.5 + 0.5*dust) factor halved the warmth in clean air, so a clear
        // sunset landed only ~17% of the way to amber — measured (1.0, 0.896,
        // 0.794), which is white with a faint peach cast rather than a sunset.
        Color sunsetPrimary = sunC.Lerp(sunsetAmber,
            Mathf.Clamp(sunsetWarmth * (1f + sunsetDustBias * dustAmount), 0f, 1f));
        sunsetPrimary = sunsetPrimary.Lerp(dustC, sunsetDustBias * dustAmount);

        // --- Phase primary (SunTint) --------------------------------
        // day = SunColor; night = MoonColor; sunset = sunsetPrimary.
        Color daySun = sunC;
        Color nightSun = moonC;
        p.SunTint = daySun.Lerp(nightSun, nightT).Lerp(sunsetPrimary, sunsetT);

        // --- Primary intensity scalars ------------------------------
        // Day intensity stays near 1.0 through partly cloudy and only
        // ducks past the cloud knee. Humidity is an always-on damper.
        // The cloud KNEE is reused below to drive the ambient lift so
        // direct and ambient track together (inverse during dimming,
        // both at baseline below the knee).
        float overcastDim = simData?.overcastDim ?? 0.4f;
        float humidityDim = simData?.humidityDim ?? 0.8f;
        float kneeStartBase = simData?.overcastKneeStart ?? 0.5f;
        float kneeEndBase = simData?.overcastKneeEnd ?? 1.0f;
        float humidityKneeShift = simData?.humidityKneeShift ?? 0.3f;
        float sunsetIntFactor = simData?.sunsetIntensityFactor ?? 0.7f;
        float dayIntBase = simData?.dayIntensityBase ?? 2f;
        float nightIntBase = simData?.nightIntensityBase ?? 0.75f;

        // Slide the knee based on humidity, centered so humidity=0.5 is
        // neutral. Dry air lets sun break through even heavy cloud
        // (knee right); humid air dims the sky at modest coverage
        // because the cloud itself is thicker (knee left).
        float humidityKneeBias = (humidity - 0.5f) * humidityKneeShift * 2f;
        float effKneeStart = kneeStartBase - humidityKneeBias;
        float effKneeEnd = kneeEndBase - humidityKneeBias;
        float cloudKnee = Mathf.SmoothStep(effKneeStart, Mathf.Max(effKneeEnd, effKneeStart + 1e-4f), cloudCover);
        float cloudIntensityScale = Mathf.Lerp(1f, overcastDim, cloudKnee);
        float humidityIntensityScale = Mathf.Lerp(1f, humidityDim, humidity);

        // Arid boost: desert / mountain sun is physically more intense
        // than "standard" because neither clouds nor humidity scatter
        // it. min() means EITHER condition being wet/cloudy cancels the
        // boost — a cloudless humid jungle stays at baseline, a desert
        // with thin cloud drops it accordingly.
        float aridBoostMax = simData?.aridBoostMax ?? 1.5f;
        float aridFactor = Mathf.Min(1f - humidity, 1f - cloudCover);
        float aridBoost = Mathf.Lerp(1f, aridBoostMax, aridFactor);

        // Elevation falloff — the sun genuinely dims as it descends, rather than
        // holding full noon intensity right up to the horizon and relying on the
        // colour crossfade alone to sell dusk. Normalized against the day's peak
        // so noon is always 1.0 whatever the world's max elevation is.
        float sinMaxElevSafe = Mathf.Max(
            Mathf.Sin(Mathf.DegToRad(simData?.sunMaxElevationDegrees ?? 60f)), 1e-4f);
        float elevNorm = Mathf.Clamp(
            Mathf.Sin(Mathf.DegToRad(sunElevationDegrees)) / sinMaxElevSafe, 0f, 1f);
        float elevFalloffExp = Mathf.Max(simData?.sunElevationFalloffExponent ?? 0.75f, 0.01f);
        float horizonFactor = Mathf.Clamp(simData?.sunHorizonIntensityFactor ?? 0.25f, 0f, 1f);
        float elevFactor = Mathf.Lerp(horizonFactor, 1f, Mathf.Pow(elevNorm, elevFalloffExp));

        float dayIntensity = dayIntBase * cloudIntensityScale * humidityIntensityScale * aridBoost * elevFactor;
        float sunsetIntensity = dayIntensity * sunsetIntFactor;
        // Night moonlight is NOT arid-boosted — moonlight is already
        // dim; scaling it up would make dry nights feel unnaturally
        // bright and flatten the day/night contrast.
        float nightIntensity = nightIntBase * cloudIntensityScale;

        // Sun-side primary fraction only (day blended with sunset). The
        // day↔night blend happens in SkyController so it can apply separate
        // sun and moon multipliers from CVars. NightT is exposed below so
        // SkyController can do the same lerp.
        p.PrimaryIntensity = Mathf.Lerp(dayIntensity, sunsetIntensity, sunsetT);
        // Unblended night intensity — used by SkyController to scale
        // MoonLight.LightEnergy so Godot's shadow pass dims in proportion.
        p.NightPrimaryIntensity = nightIntensity;
        p.NightT = nightT;

        // --- Ambient -------------------------------------------------
        // Ambient inverts direct intensity via the SAME cloudKnee: below
        // the knee clear-sky values hold (low ambient, crisp shadows);
        // past it ambient rises as direct ducks, redistributing total
        // illumination from sun → sky. The TOTAL (direct + ambient)
        // still drops in thick cloud because the direct loss exceeds
        // the ambient gain. For CLOUD-shadow softness (not terrain-
        // shadow lift) use SkyController.cloudShadowStrength instead.
        float dayAmbBase = simData?.dayAmbientBase ?? 0.15f;
        float ambHum = simData?.ambientHumidityLift ?? 0.1f;
        float ambCloud = simData?.ambientCloudLift ?? 0.6f;
        float nightAmbBase = simData?.nightAmbientBase ?? 0.08f;
        float nightAmbHum = simData?.nightAmbientHumidityLift ?? 0.05f;
        float sunsetAmbFactor = simData?.sunsetAmbientFactor ?? 1.1f;

        float dayAmbient = Mathf.Clamp(dayAmbBase + humidity * ambHum + cloudKnee * ambCloud, 0f, 1f);
        float nightAmbient = Mathf.Clamp(nightAmbBase + humidity * nightAmbHum, 0f, 1f);
        float sunsetAmbient = Mathf.Clamp(dayAmbient * sunsetAmbFactor, 0f, 1f);
        p.Ambient = Mathf.Lerp(Mathf.Lerp(dayAmbient, nightAmbient, nightT), sunsetAmbient, sunsetT);

        // --- Sky horizon / zenith colors ----------------------------
        float horizonBrightness = simData?.dayHorizonBrightness ?? 1.2f;
        float horizonWarmBias = simData?.dayHorizonWarmBias ?? 0.3f;
        float horizonHumidityHaze = simData?.dayHorizonHumidityHaze ?? 0.4f;
        float nightZenithScale = simData?.nightZenithSkyScale ?? 0.05f;
        float nightHorizonScale = simData?.nightHorizonSkyScale ?? 0.18f;
        float nightHorizonMoonBleed = simData?.nightHorizonMoonBleed ?? 0.15f;
        float sunsetZenithScale = simData?.sunsetZenithSkyScale ?? 0.4f;
        Color sunsetPurple = simData?.sunsetZenithPurple ?? new Color(0.35f, 0.15f, 0.45f);
        float sunsetHumidityPurple = simData?.sunsetZenithHumidityPurple ?? 0.4f;

        Color hazeColor = new Color(1f, 1f, 1f).Lerp(dustC, dustAmount);
        Color dayHorizon = ScaleColor(skyC, horizonBrightness);
        dayHorizon = dayHorizon.Lerp(sunC, horizonWarmBias * (1f - cloudCover));
        dayHorizon = dayHorizon.Lerp(hazeColor, humidity * horizonHumidityHaze);

        Color dayZenith = skyC.Lerp(ScaleColor(skyC, 0.7f), cloudCover);

        // Sunset horizon is the already-computed sunsetPrimary — same
        // warm band, same dust influence.
        // Scaled like every other band. sunsetPrimary is the LIGHT colour; using it
        // raw as the sky made the sunset dome the one phase with no brightness
        // control, and the brightest horizon of the day.
        float sunsetSkyBrightness = Mathf.Max(simData?.sunsetSkyBrightness ?? 1f, 0f);
        Color sunsetHorizon = ScaleColor(sunsetPrimary,
            (simData?.sunsetHorizonBrightness ?? 0.55f) * sunsetSkyBrightness);
        // Scaled by the same master as the horizon — otherwise pulling the horizon
        // down just leaves the zenith as the bright blue thing the water mirrors.
        Color sunsetZenith = ScaleColor(skyC, sunsetZenithScale * sunsetSkyBrightness)
            .Lerp(ScaleColor(sunsetPurple, sunsetSkyBrightness), humidity * sunsetHumidityPurple);

        Color nightHorizon = ScaleColor(skyC, nightHorizonScale)
            .Lerp(moonC, nightHorizonMoonBleed);
        Color nightZenith = ScaleColor(skyC, nightZenithScale);

        p.HorizonTint = dayHorizon.Lerp(nightHorizon, nightT).Lerp(sunsetHorizon, sunsetT);
        p.ZenithTint = dayZenith.Lerp(nightZenith, nightT).Lerp(sunsetZenith, sunsetT);

        // Band width follows the same phase weights as the colours it blends.
        float gradDay = simData?.skyGradientExponentDay ?? 0.6f;
        float gradSunset = simData?.skyGradientExponentSunset ?? 2.0f;
        float gradNight = simData?.skyGradientExponentNight ?? 0.5f;
        p.SkyGradientExponent = Mathf.Max(
            Mathf.Lerp(Mathf.Lerp(gradDay, gradNight, nightT), gradSunset, sunsetT), 0.01f);

        // --- Fills --------------------------------------------------
        float fillASkyBias = simData?.fillAFromSkyBias ?? 0.7f;
        float fillBWhiteMix = simData?.fillBWhiteMix ?? 0.2f;
        float fillDustPullK = simData?.fillDustPullK ?? 0.35f;
        float fillDesatK = simData?.fillDesatK ?? 0.35f;

        // Day: fillA mostly sky (cool bounce), fillB mostly sun lightened.
        Color dayFillA = sunC.Lerp(skyC, fillASkyBias);
        Color dayFillB = sunC.Lerp(new Color(0.95f, 0.95f, 0.95f), fillBWhiteMix);
        // Sunset: keep the same RELATIONSHIP (cool sky side, warm primary
        // side) but against sunsetPrimary instead of SunColor.
        Color sunsetFillA = sunsetPrimary.Lerp(skyC, fillASkyBias);
        Color sunsetFillB = sunsetPrimary.Lerp(new Color(0.95f, 0.85f, 0.75f), fillBWhiteMix);
        // Night: fills pivot against MoonColor.
        Color nightFillA = moonC.Lerp(ScaleColor(skyC, 0.4f), fillASkyBias);
        Color nightFillB = moonC.Lerp(new Color(0.5f, 0.5f, 0.6f), fillBWhiteMix);

        // Atmospheric haze pulls all three toward DustColor, then desat.
        float dustPull = fillDustPullK * atmHaze;
        float desat = fillDesatK * humidity;

        dayFillA = DesaturateToward(dayFillA.Lerp(dustC, dustPull), desat);
        dayFillB = DesaturateToward(dayFillB.Lerp(dustC, dustPull), desat);
        sunsetFillA = DesaturateToward(sunsetFillA.Lerp(dustC, dustPull), desat);
        sunsetFillB = DesaturateToward(sunsetFillB.Lerp(dustC, dustPull), desat);
        nightFillA = DesaturateToward(nightFillA.Lerp(dustC, dustPull * 0.5f), desat);
        nightFillB = DesaturateToward(nightFillB.Lerp(dustC, dustPull * 0.5f), desat);

        p.FillA = dayFillA.Lerp(nightFillA, nightT).Lerp(sunsetFillA, sunsetT);
        p.FillB = dayFillB.Lerp(nightFillB, nightT).Lerp(sunsetFillB, sunsetT);

        // --- Cloud color --------------------------------------------
        // Physical cloud-lighting model: a white water-droplet volume gets
        // its top lit by direct sunlight, its bottom/shadow side picks up
        // bounce from the sky. Tint the result by direct light intensity
        // so dim/overcast scenes give dim clouds without a separate knob.
        //   cloudLit    = SunTint × intensity (direct component)
        //   cloudShadow = SkyColor (bounced component)
        //   CloudTint   = blend — mostly lit, some shadow bias
        // At night, SunTint has already blended toward MoonColor so clouds
        // pick up cool tones automatically. No phase-specific branches here.
        //
        // The intensity has to be the NIGHT-BLENDED one, not p.PrimaryIntensity:
        // that field is the day side only (the day/night pick happens downstream
        // via NightT), so using it lit the clouds with the sun's brightness in the
        // moon's color and blew the dome — and its water reflection — past white
        // all night.
        float cloudLightFactor = Mathf.Clamp(
            Mathf.Lerp(p.PrimaryIntensity, p.NightPrimaryIntensity, nightT), 0.15f, 2.0f);
        Color cloudLit = ScaleColor(p.SunTint, cloudLightFactor);
        // The bounce half is the sky the cloud is actually sitting under, so it
        // has to follow the dome into night (nightHorizonScale, the same knob the
        // gradient uses) — the raw authored skyColor is a DAY blue and left the
        // undersides glowing after the lit half had gone dim.
        Color cloudShadow = ScaleColor(skyC, 0.7f)
            .Lerp(ScaleColor(skyC, 0.7f * nightHorizonScale), nightT);
        // Shadow weight rises with cloudCover so overcast clouds read as
        // the flat gray-blue of their underside rather than sun-tinted white.
        float shadowMix = Mathf.Clamp(0.25f + cloudCover * 0.35f, 0.2f, 0.7f);
        p.CloudTint = cloudLit.Lerp(cloudShadow, shadowMix);

        // --- Fog tint + density -------------------------------------
        // DustColor IS the regional fog tint, used directly. Phase
        // dimming and direct-sun-through-fog warmth come from the
        // shader: shaft_color (already phase-blended above) carries
        // the sun/moon-tinted scattering, while fog_color here is
        // just the fog's intrinsic color per zone. Only the sunset
        // pass gets a small explicit warm push since shaft_color
        // doesn't cover the AMBIENT fog contribution at low sun.
        float fogDensityK = simData?.fogDensityK ?? 0.1f;
        float ambientFogK = simData?.ambientFogK ?? 0.005f;
        float ambientFogHumidityK = simData?.ambientFogHumidityK ?? 0f;

        Color dayFog = dustC;
        Color sunsetFog = dustC.Lerp(sunsetPrimary, 0.35f);
        Color nightFog = dustC;

        p.FogTint = dayFog.Lerp(nightFog, nightT).Lerp(sunsetFog, sunsetT);

        // Fog density scales with direct-light intensity: fog is only
        // visible when there's light scattering through it, so a dim
        // scene (night, heavy overcast) should have correspondingly
        // dim fog. Replaces the older phase/cloudCover scale which
        // could only distinguish "night" from "day" and missed the
        // equally dim "stormy day" case. Smoothstepped with a floor
        // so fog doesn't snap to invisible at zero direct.
        float fogIntensityReference = simData?.fogIntensityReference ?? 0.35f;
        float fogIntensityFloor = simData?.fogIntensityFloor ?? 0.2f;
        float fogIntensityFactor = Mathf.SmoothStep(0f, fogIntensityReference, p.PrimaryIntensity);
        float fogPhaseScale = Mathf.Lerp(fogIntensityFloor, 1f, fogIntensityFactor);

        p.FogDensity = fog * fogDensityK * fogPhaseScale;
        // Fog → ambient haze uses a concave curve (pow < 1) so low
        // authored fog values (mountain = 0.08) still read as visible
        // haze while high values (swamp = 0.6) don't over-saturate.
        // Old linear mapping made fog=0.6 look like pea soup.
        float fogCurveExp = simData?.fogCurveExponent ?? 0.5f;
        float fogShaped = fog > 0f ? Mathf.Pow(fog, fogCurveExp) : 0f;
        p.AmbientFogDensity = (fogShaped * ambientFogK + humidity * ambientFogHumidityK) * fogPhaseScale;

        // --- Dust density -------------------------------------------
        // Dust is the scattering medium that beams need. Humidity folds in
        // as additional haze droplets so humid zones can show shafts through
        // partial cloud even where authored dustAmount is low — the shader's
        // contrast gate still keeps them from washing out open sunlit air.
        float dustDensityK = simData?.dustDensityK ?? 0.03f;
        float dustFromHumidity = simData?.dustFromHumidity ?? 0.5f;
        float effectiveDustAmount = Mathf.Clamp(dustAmount + humidity * dustFromHumidity, 0f, 1f);
        p.DustDensity = effectiveDustAmount * dustDensityK;

        // --- Cloud shape --------------------------------------------
        // Authored cloudCover maps to a CENTER threshold for the
        // noise-→-opacity smoothstep. The shader (cloud_shadow.gdshaderinc)
        // uses the threshold as the LOWER edge (smoothstep from
        // threshold to threshold + band, where band = 1 - sharpness),
        // so we subtract half-band here to center the band around the
        // authored value. Without this shift, lowering sharpness (humid
        // air → softer edges) only expanded the band UPWARD, pushing
        // cloud coverage DOWN — so full cloudCover at high humidity
        // could never produce true overcast, and the cloud-shadow
        // pattern on the ground stayed sparse at partly-cloudy zones.
        // Centering keeps "cloudCover=1" meaning "fully overcast"
        // regardless of humidity, and lets the same cloudCover produce
        // the same visible COVERAGE across sharpness variations while
        // only softness changes.
        float cloudThresholdClear = simData?.cloudThresholdClear ?? 0.95f;
        float cloudThresholdOvercast = simData?.cloudThresholdOvercast ?? 0.2f;
        float cloudSharpnessDry = simData?.cloudSharpnessDry ?? 0.85f;
        float cloudSharpnessHumid = simData?.cloudSharpnessHumid ?? 0.3f;

        // Base sharpness is humidity-driven (wet air = soft edges). At
        // cloudCover EXTREMES (<~15% or >~85%) we override back toward
        // the dry-air maximum so the rare contrast surfaces — thin
        // wispy patches in a stormy overcast, scattered crisp cumulus
        // in a near-clear sky — get defined edges rather than washing
        // out into uniform soft gradient. Without this, forest at
        // cloudCover=0.95 produces a flat "everything is cloud"
        // surface with no shape detail readable against the ground.
        float humiditySharpness = Mathf.Lerp(cloudSharpnessDry, cloudSharpnessHumid, humidity);
        float extremeLow = Mathf.SmoothStep(0.25f, 0.15f, cloudCover);
        float extremeHigh = Mathf.SmoothStep(0.75f, 0.85f, cloudCover);
        float extremity = Mathf.Max(extremeLow, extremeHigh);
        p.CloudSharpness = Mathf.Lerp(humiditySharpness, cloudSharpnessDry, extremity);

        // Shape cloudCover before interpolating the threshold so mid-
        // range authored values (cc=0.5) produce visibly ~50% coverage
        // rather than underloading at ~30% through linear interpolation
        // against a typical FBM noise distribution.
        float cloudCoverExponent = simData?.cloudCoverExponent ?? 0.7f;
        float shapedCloudCover = Mathf.Pow(cloudCover, cloudCoverExponent);
        float authoredThreshold = Mathf.Lerp(cloudThresholdClear, cloudThresholdOvercast, shapedCloudCover);
        float halfBand = (1f - p.CloudSharpness) * 0.5f;
        p.CloudThreshold = authoredThreshold - halfBand;

        // --- Shaft colours ------------------------------------------
        // Only the COLOUR is derived here (it's a function of the zone's
        // sun / moon / dust palette + sunset bias). The shaft INTENSITY and
        // its weather response are client-side tuning — see SkyController's
        // sun-wash block. Shaft colours already include the sunset warm bias
        // (each channel's "when this source is primary" colour blended with
        // sunset primary by sunsetT). SkyController does the remaining
        // sun↔moon crossfade by horizon factors.
        float shaftDustColorMix = simData?.shaftDustColorMix ?? 0.3f;
        Color sunShaftDay = sunC.Lerp(dustC, shaftDustColorMix);
        Color moonShaftNight = moonC.Lerp(dustC, shaftDustColorMix * 0.5f);
        Color shaftSunset = sunsetPrimary.Lerp(dustC, shaftDustColorMix);
        p.SunShaftColor = sunShaftDay.Lerp(shaftSunset, sunsetT);
        p.MoonShaftColor = moonShaftNight.Lerp(shaftSunset, sunsetT);

        // --- Water --------------------------------------------------
        // ZoneData.WaterColor (RGB) drives the surface tint;
        // ZoneData.WaterOpacity is "muddiness" — physically how much
        // sediment/organic matter is suspended, which ripples through
        // into viscosity (damped ripples and waves), opacity (fast
        // depth falloff), reflection (denser surface = better mirror),
        // refraction (particles scatter before light can bend cleanly),
        // and whitecap formation (viscous water resists air entrainment).
        Color waterC = zone?.waterColor ?? new Color(0.3f, 0.45f, 0.5f, 1f);
        float muddy = Mathf.Clamp(zone?.waterOpacity ?? 0.5f, 0f, 1f);
        p.WaterMuddiness = muddy;

        // Shallow tint is just the authored RGB. Depth tint is derived two
        // ways and interpolated by muddiness:
        //  - clearDeep  : red absorbed first, then green — what clean water
        //                 does at depth regardless of zone (ocean physics).
        //  - murkyDeep  : dust-tinted sediment; particles scatter whatever
        //                 the regional DustColor carries (ochre desert pond,
        //                 cool violet glacial melt, green swamp).
        p.WaterShallowTint = new Color(waterC.R, waterC.G, waterC.B, 1f);
        // Volume scatter colour. At muddiness 0 this IS the authored waterColor,
        // so a zone's specific water colour survives untouched. Muddiness pulls it
        // and nothing dilutes it. Muddiness moves only the INTENSITY (scatter
        // albedo) and how far you can see in (absorption) — see water_optics.
        // Depth colour is NOT authored separately: absorption is tinted by the
        // complement of this colour, so the column reddens out on its own.
        p.WaterScatterColor = p.WaterShallowTint;
        // Deep-water colour IS the scatter colour — one derivation, not two.
        p.WaterDeepTint = p.WaterScatterColor;

        // Ripple strength: wind-driven with a quadratic curve so low-wind
        // scenes stay near-mirror (the sun disk reflects coherently) and
        // only meaningful wind produces visible normal perturbation. Linear
        // mapping produced too much ripple at 3–5 m/s — normals scattered
        // enough that the sun disk smeared across the whole surface.
        // Reference wind for ripple saturation (ripple_strength → 1 at this
        // speed pre-damping). Rain adds a flat contribution.
        float rippleWindRef = simData?.rippleWindRef ?? 10f;
        float rippleRainK = simData?.rippleRainK ?? 0.3f;
        float windFrac = Mathf.Clamp(windSpeed / Mathf.Max(rippleWindRef, 0.1f), 0f, 1f);
        float rippleBase = Mathf.Clamp(windFrac * windFrac + rainAmount * rippleRainK, 0f, 1f);
        p.RippleStrength = rippleBase * Mathf.Lerp(1.0f, 0.35f, muddy);

        // --- Wind rhythm --------------------------------------------
        float windFreqBase = simData?.windFreqBase ?? 1.0f;
        float windFreqCloud = simData?.windFreqCloud ?? 0.8f;
        float gustFreqBase = simData?.gustFreqBase ?? 0.1f;
        float gustFreqCloud = simData?.gustFreqCloud ?? 0.2f;
        float gustMinFraction = simData?.gustMinFraction ?? 0.3f;
        float gustCloudFraction = simData?.gustCloudFraction ?? 0.5f;

        p.WindFrequency = windFreqBase + cloudCover * windFreqCloud;
        p.GustFrequency = gustFreqBase + cloudCover * gustFreqCloud;
        p.GustStrength = windSpeed * (gustMinFraction + cloudCover * gustCloudFraction);

        // --- Rain ---------------------------------------------------
        float rainWeightMin = simData?.rainWeightMin ?? 0.3f;
        float rainWeightMax = simData?.rainWeightMax ?? 1.2f;
        float rainIntensityExp = simData?.rainIntensityExponent ?? 1.25f;
        // Drop COUNT shaped with pow>1 so a light authored rainAmount
        // (e.g. 0.3) emits visibly fewer drops than a linear mapping
        // would, while high values (≈1.0) stay near the authored count.
        p.RainIntensity = rainAmount > 0f ? Mathf.Pow(rainAmount, rainIntensityExp) : 0f;
        p.RainWeight = Mathf.Lerp(rainWeightMin, rainWeightMax, cloudCover);
        p.RainTier = ClassifyRainTier(p.RainIntensity, simData);

        // --- Moon disk color ----------------------------------------
        // Sky shader's moon disk is literally the moon; no phase blend.
        p.MoonDiskColor = moonC;

        // --- Nightfall ----------------------------------------------
        // The day ends where the sun would have risen, so the last stretch of the
        // clock is where the light goes: skylight holds through the moonlit night
        // and then slides to NightfallSkylightFloor (0 = utterly black) across the
        // sunrise hours, leaving block lights as the only thing the player can see
        // by. It stays there — the clock stops at 1 and only a sleep starts the
        // next day, so no dawn ever arrives on this side of it. Deliberately runs
        // after the whole day/sunset/night model above — that computes each
        // channel normally and this is a pure dimming pass over the result, so
        // the curve can't perturb any of that logic.
        //
        // Applied here rather than folded into the night phase weights because
        // those key off sun ELEVATION (saturated well before midnight) while this
        // keys off clock POSITION in the closing window.
        float nightfallFalloff = Mathf.Max(simData?.nightfallFalloff ?? 0.5f, 0.01f);
        float nightfallFloor = Mathf.Clamp(simData?.nightfallSkylightFloor ?? 0f, 0f, 1f);
        float nightfallStart = Mathf.Clamp(simData?.nightfallStartTimeOfDay ?? 0.85f, 0f, 0.999f);
        float nightfall01 = Mathf.Clamp(
            (timeOfDay01 - nightfallStart)
            / Mathf.Max((float)WorldState.EndOfDayTimeOfDay01 - nightfallStart, 1e-4f), 0f, 1f);
        p.SkyLight = Mathf.Lerp(nightfallFloor, 1f, Mathf.Pow(1f - nightfall01, nightfallFalloff));

        p.Ambient *= p.SkyLight;
        p.PrimaryIntensity *= p.SkyLight;
        p.NightPrimaryIntensity *= p.SkyLight;

        // --- Illumination -------------------------------------------
        // "Is there light in the open air at all", 1 whenever the scene is lit
        // by any normal amount and → 0 only as the light genuinely vanishes.
        // Derived from the RESULT (the blended direct intensity) rather than
        // from the clock, so every cause of darkness feeds it: nightfall above,
        // and anything future that dims intensity — an eclipse — for free.
        //
        // The ramp reaches 1 well below moonlight, so day, dusk and moonlit
        // night are all untouched; this only bites at the vanishing end.
        float litIntensity = Mathf.Lerp(p.PrimaryIntensity, p.NightPrimaryIntensity, p.NightT);
        float skyLightRef = Mathf.Max(simData?.skyLightReference ?? 0.35f, 1e-4f);
        p.Illumination = Mathf.SmoothStep(0f, skyLightRef, litIntensity);

        // Air that the sky lights — haze, the dome, its clouds, and with them
        // every water / wet-fresnel reflection sampling those colors. All of it
        // rides BOTH scalars, and the pair are not interchangeable:
        //   SkyLight     — the nightfall curve, the same one already applied to
        //                  Ambient / PrimaryIntensity above. Without it the sky
        //                  stays at full brightness while everything it lights
        //                  dims, so water mirrors a bright sky over black land.
        //   Illumination — a SATURATING gate (smoothstep against a reference
        //                  BELOW moonlight), so on its own it holds at exactly 1
        //                  through most of nightfall and then dumps the whole
        //                  fade into the last few percent of the clock. It is the
        //                  "is there any light at all" backstop — catching causes
        //                  of darkness the clock doesn't know about — not the
        //                  dimming curve.
        // The fog shader in particular gates haze on light_map.r (whether sky
        // REACHES a voxel, not how much it is giving), so a sealed cave already
        // contributes no haze but open ground would hold that gate at ~1 through
        // any darkness and go on washing the world toward a full-brightness
        // fog_color. Density is deliberately NOT scaled (it reads the
        // pre-nightfall PrimaryIntensity above): dark air still occludes, so
        // distant block lights fade toward black rather than toward white haze.
        float skyColorScale = p.SkyLight * p.Illumination;
        p.FogTint = ScaleColor(p.FogTint, skyColorScale);
        p.HorizonTint = ScaleColor(p.HorizonTint, skyColorScale);
        p.ZenithTint = ScaleColor(p.ZenithTint, skyColorScale);
        p.CloudTint = ScaleColor(p.CloudTint, skyColorScale);

        return p;
    }

    // Classify a derived RainIntensity (0..1) into a discrete tier using
    // SimData's boundaries. Shared source of truth so wet-status gating, HUD,
    // and audio all agree on where drizzle ends and light/heavy rain begin.
    public static ERainTier ClassifyRainTier(float rainIntensity, SimData simData)
    {
        float drizzle = simData?.rainDrizzleThreshold ?? 0.02f;
        float light = simData?.rainLightThreshold ?? 0.15f;
        float heavy = simData?.rainHeavyThreshold ?? 0.6f;
        if (rainIntensity >= heavy)
        {
            return ERainTier.Heavy;
        }
        if (rainIntensity >= light)
        {
            return ERainTier.Light;
        }
        if (rainIntensity >= drizzle)
        {
            return ERainTier.Drizzle;
        }
        return ERainTier.None;
    }

    private static Color ScaleColor(Color c, float k)
    {
        return new Color(Mathf.Clamp(c.R * k, 0f, 1f), Mathf.Clamp(c.G * k, 0f, 1f), Mathf.Clamp(c.B * k, 0f, 1f), c.A);
    }

    // Rec. 709 luminance lerp — push color toward its grayscale value
    // by k in [0,1]. 0 = unchanged; 1 = full desaturate.
    private static Color DesaturateToward(Color c, float k)
    {
        float y = 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
        return new Color(
            Mathf.Lerp(c.R, y, k),
            Mathf.Lerp(c.G, y, k),
            Mathf.Lerp(c.B, y, k),
            c.A);
    }
}
