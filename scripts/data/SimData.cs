using Godot;

// Static, authored world-level simulation constants. Mutable runtime state
// (TimeOfDay01, WindDirection, ShadowLightDirection, etc.) lives on WorldState
// — `Data` is never used for mutable values (see CLAUDE.md conventions).
[GlobalClass]
public partial class SimData : Resource
{
    [Export] public float Gravity = 9.8f;
    [Export] public float VisibleTime = 0.25f;

    [ExportGroup("Time of Day")]
    // Seconds of wall-clock time for a full day/night cycle at time_scale = 1.
    // The time_scale CVar multiplies this advancement for fast-forward testing.
    [Export] public float DayLengthSeconds = 600f;

    // Normalized time the world starts at: 0 = midnight, 0.25 = sunrise,
    // 0.5 = noon, 0.75 = sunset. Applied when a fresh game is started.
    [Export(PropertyHint.Range, "0,1,0.001")] public float InitialTimeOfDay = 0.3f;

    // Sun's maximum elevation above the horizon at noon. 90 = sun passes
    // through zenith; lower values tilt the orbit so the sun peaks at a
    // shallower angle (higher-latitude look). Drives both visual sky
    // placement AND the simulation-side ShadowLightDirection that
    // gameplay raycasts (stealth, AI visibility) query.
    [Export(PropertyHint.Range, "10,90,1")] public float SunMaxElevationDegrees = 60f;

    // Horizontal sway of the sun's orbit. The sun sits at -SunSideSwayDegrees
    // yaw at sunrise, 0 at noon, +SunSideSwayDegrees at sunset. 0 locks the
    // sun to a single azimuth (unnatural); 30 reads as a mid-latitude day.
    // Same dual role: visual placement + simulation ShadowLightDirection.
    [Export(PropertyHint.Range, "0,89,1")] public float SunSideSwayDegrees = 30f;

    // The effective horizon — the elevation above geometric 0° at which
    // sources are considered "at sunset/moonrise". Models an occluding
    // horizon line (mountains, tree ring, distant cliffs) so the sun can
    // visually set before it drops below the actual geometric horizon,
    // and the moon can visibly rise into view some minutes before it
    // would astronomically appear. Every horizon fade in SkyController
    // (light energy, shafts, cloud shadows, color blend) is an OFFSET
    // from this angle, and the gameplay `CurrentAmbient` blend pivots
    // on it too.
    [Export(PropertyHint.Range, "0,45,0.5")] public float SunsetAngleDegrees = 15f;

    // Half-width (degrees) of the sunrise/sunset color blend band, measured
    // from SunsetAngleDegrees. The sunset color variants peak when the sun
    // (or moon) is exactly at SunsetAngleDegrees elevation, fade to day
    // colors at SunsetAngleDegrees + this, fade to night colors at
    // SunsetAngleDegrees - this. Also parameterizes the ambient blend
    // that gameplay stealth/perception consumes.
    [Export(PropertyHint.Range, "1,45,0.5")] public float SunsetColorRangeDegrees = 10f;

    [ExportGroup("Regions")]
    // Four corner regions around the world origin. SkyController
    // samples a blended RegionData + WeatherData each frame based on
    // the player's XZ position, with a 32m cross-blend band centered
    // on X=0 and Z=0. Convention: +X = east, +Z = north. A missing
    // slot contributes zero weight; neighbors scale up to cover.
    //
    // This 4-quadrant scaffolding is a transitional step. The long-
    // term design is an arbitrary region placement (regions authored
    // as polygons or a tiled atlas); the blender interface will stay
    // the same, only the sample-weight computation changes.
    [Export] public RegionData regionNE;
    [Export] public RegionData regionNW;
    [Export] public RegionData regionSE;
    [Export] public RegionData regionSW;

    [ExportGroup("Weather Derivation Tuning")]
    // Every knob below shapes how WeatherDerivation turns (region,
    // weather, time-of-day) into the concrete visual outputs pushed
    // to shaders and lights. Defaults are tuned to roughly match the
    // pre-simplification look; this is the one place to retune the
    // feel without editing code. Grouped by output channel.

    [ExportSubgroup("Sky Colors")]
    // Day horizon = SkyColor brightened by this factor (lightens the
    // near-horizon band). 1 = no lift; 1.3 = noticeable atmospheric
    // glow near the horizon.
    [Export(PropertyHint.Range, "0.5,2,0.01")] public float DayHorizonBrightness = 1.2f;
    // How much the day horizon tilts toward the region's SunColor.
    // 0 = pure SkyColor; 1 = full SunColor warm wash near the horizon.
    [Export(PropertyHint.Range, "0,1,0.01")] public float DayHorizonWarmBias = 0.3f;
    // How much humidity pulls the horizon toward a pale haze color
    // (blend of white and DustColor weighted by dustAmount). 0 = no
    // effect; 1 = fully hazed out at humidity=1.
    [Export(PropertyHint.Range, "0,1,0.01")] public float DayHorizonHumidityHaze = 0.4f;
    // Scale applied to SkyColor at the night zenith. 0.05 = deep
    // near-black; 0.3 = moonlit blue.
    [Export(PropertyHint.Range, "0,1,0.01")] public float NightZenithSkyScale = 0.05f;
    // Scale applied to SkyColor at the night horizon (before MoonColor
    // bleed adds on top). Brighter than the zenith since the atmosphere
    // scatters even faint moonlight toward the horizon.
    [Export(PropertyHint.Range, "0,1,0.01")] public float NightHorizonSkyScale = 0.18f;
    // How much of the region's MoonColor bleeds into the night horizon.
    // 0 = horizon is a pure dark sky; 0.3 = visible moonlit wash.
    [Export(PropertyHint.Range, "0,1,0.01")] public float NightHorizonMoonBleed = 0.15f;
    // Sunset zenith is a mid-dark sky with a violet twilight push.
    // This scales the underlying SkyColor before mixing in purple.
    [Export(PropertyHint.Range, "0,1,0.01")] public float SunsetZenithSkyScale = 0.4f;
    // Target purple for the twilight sky overhead. Humidity controls
    // how hard the zenith pushes toward this color.
    [Export] public Color SunsetZenithPurple = new Color(0.35f, 0.15f, 0.45f);
    // How much humidity strengthens the twilight purple push. 0 = never;
    // 1 = fully replaces sky zenith at humidity=1.
    [Export(PropertyHint.Range, "0,1,0.01")] public float SunsetZenithHumidityPurple = 0.4f;

    [ExportSubgroup("Sunset Warmth")]
    // Target warm color for the sunset horizon / primary blend. Lean
    // toward amber/red; dust amount pushes harder toward this.
    [Export] public Color SunsetAmberTarget = new Color(1.0f, 0.5f, 0.2f);
    // Base sunset warmth: how strongly SunColor shifts toward the
    // amber target even in zero-dust air. 0 = sunset IS SunColor;
    // 1 = sunset IS the amber target.
    [Export(PropertyHint.Range, "0,1,0.01")] public float SunsetWarmthBias = 0.35f;
    // Additional dust-driven push toward DustColor on the sunset
    // horizon and primary. Explains why "red sky at night" tracks with
    // atmospheric dust.
    [Export(PropertyHint.Range, "0,1,0.01")] public float SunsetDustBias = 0.35f;

    [ExportSubgroup("Fills")]
    // Fills oppose the primary light and sculpt surface slope. fillA
    // pulls toward SkyColor (cool); fillB pulls toward a lightened
    // SunColor (warm). This slider is the mix weight on fillA's
    // sky bias — higher = more sky-dominant cool fill.
    [Export(PropertyHint.Range, "0,1,0.01")] public float FillAFromSkyBias = 0.7f;
    // fillB mix toward white. 0 = pure SunColor; 1 = pure white.
    // Small values keep fillB as a gentle warm bounce rather than
    // a bright wash.
    [Export(PropertyHint.Range, "0,1,0.01")] public float FillBWhiteMix = 0.2f;
    // How much atmospheric haze (humidity + fog + dustAmount) pulls
    // fill colors toward DustColor. Higher = fills pick up regional
    // character in dusty/humid weather.
    [Export(PropertyHint.Range, "0,1,0.01")] public float FillDustPullK = 0.35f;
    // How much humidity desaturates fills (toward their luminance).
    // Describes how humid air washes out slope-shading color.
    [Export(PropertyHint.Range, "0,1,0.01")] public float FillDesatK = 0.35f;

    [ExportSubgroup("Clouds")]
    // cloudThreshold when cloudCover=0 (clear sky). Higher = fewer
    // patches of cloud actually make it past the noise threshold.
    // 0.95 reads as "almost no cloud at all".
    [Export(PropertyHint.Range, "0,1,0.01")] public float CloudThresholdClear = 0.95f;
    // cloudThreshold when cloudCover=1 (overcast). Lower = more of
    // the noise field exceeds threshold. Combined with the symmetric
    // band shift in WeatherDerivation, 0.0 here means cloudCover=1
    // gives true full coverage — most noise values produce solid
    // cloud, with only thin variation where noise is lowest.
    [Export(PropertyHint.Range, "-0.5,1,0.01")] public float CloudThresholdOvercast = 0.0f;
    // cloudSharpness when humidity=0 (dry air). Higher = crisper cloud
    // edges. Dry desert skies have very hard-edged cumulus.
    [Export(PropertyHint.Range, "0,1,0.01")] public float CloudSharpnessDry = 0.85f;
    // cloudSharpness when humidity=1. Soft edges read as translucent,
    // tropical cloud character.
    [Export(PropertyHint.Range, "0,1,0.01")] public float CloudSharpnessHumid = 0.3f;
    // Exponent shaping cloudCover → threshold interpolation. 1.0 is
    // linear; <1 shifts mid-cover values toward the overcast end so
    // cc=0.5 reads as genuine half-cloudy (~50% of sky solid) rather
    // than "partly cloudy" (~30% with linear interpolation). Tuned
    // against a typical FBM noise distribution.
    [Export(PropertyHint.Range, "0.3,2,0.01")] public float CloudCoverExponent = 0.7f;
    // Day cloud color = lerp(white, SunColor, this). Higher = clouds
    // take on more of the sun's tint; lower = whiter clouds.
    [Export(PropertyHint.Range, "0,1,0.01")] public float DayCloudSunMix = 0.3f;
    // Sunset cloud color pulled toward DustColor by this amount. High
    // dust regions get dramatic warm-underbelly clouds at sunset.
    [Export(PropertyHint.Range, "0,1,0.01")] public float SunsetCloudDustMix = 0.4f;
    // Night cloud color = lerp(dark gray, MoonColor, this). Keeps
    // night clouds visible against a dark sky without going full moon
    // tint.
    [Export(PropertyHint.Range, "0,1,0.01")] public float NightCloudMoonMix = 0.7f;

    [ExportSubgroup("Fog")]
    // Fog tint uses RegionData.DustColor directly (see
    // WeatherDerivation) — DustColor is a regional palette / theming
    // color and is the right intrinsic fog tint. Phase dimming and
    // sun/moon warmth through fog come from the shader's shaft_color
    // (phase-blended) and lighting response, not from pre-baking
    // night/sunset tints here.
    // Voxel fog density = fog * this. Authored old-preset fogDensity
    // ranged 0.001-0.02; fog=1 at K=0.1 saturates near the high end,
    // leaving headroom so "full fog" doesn't wall off sight entirely.
    [Export(PropertyHint.Range, "0,1,0.001")] public float FogDensityK = 0.1f;
    // Ambient (non-map) distance haze from the weather.fog variable.
    // The shape is `pow(fog, FogCurveExponent) * K`: a concave curve
    // (exponent < 1) lets low fog values still read as visible haze
    // while damping high values so authored fog=0.6 doesn't over-
    // saturate into pea soup. Linear fog scaling made users author
    // fog=0.08 for "medium" and end up with fog=0.6 = "can't see."
    [Export(PropertyHint.Range, "0,0.05,0.0005")] public float AmbientFogK = 0.0025f;
    // Exponent shaping the fog → haze curve. 1.0 = linear; 0.5 = sqrt
    // (current default; low fog hits ~40% of max haze). Lower values
    // push the curve further toward "even a little fog is visible,
    // max fog is not much denser."
    [Export(PropertyHint.Range, "0.1,2,0.01")] public float FogCurveExponent = 0.5f;
    // Fog density scales with current direct-light intensity (palette
    // PrimaryIntensity) via a smoothstep: fog is visible proportional
    // to the light scattering through it, so dim-primary scenes (full
    // night, heavy storm) should read with dimmer fog regardless of
    // authored fog value. Below this threshold, fog falls toward the
    // floor; above it, fog is at full density.
    [Export(PropertyHint.Range, "0,1,0.01")] public float FogIntensityReference = 0.35f;
    // Minimum fog density multiplier when direct light is near zero.
    // 0 would kill fog entirely at night (too abrupt); 0.2 keeps a
    // visible trace so heavy-fog regions still read as foggy under
    // moonlight, just much dimmer than day.
    [Export(PropertyHint.Range, "0,1,0.01")] public float FogIntensityFloor = 0.2f;
    // Additional ambient haze from humidity. Zero by default — humid-
    // but-clear regions shouldn't look foggy. Re-enable if you want
    // tropical regions to feel hazier than their authored fog alone
    // would produce.
    [Export(PropertyHint.Range, "0,0.05,0.0005")] public float AmbientFogHumidityK = 0f;

    [ExportSubgroup("Shafts")]
    // Base sun-shaft intensity before dust / cloud modulation. 12 lets
    // a dusty region (mountain/desert at dustAmount=0.4) produce shafts
    // comfortably into the "visible beam" range (effective 12-16) while
    // low-dust regions stay subtle. Old authored values ran 3-20 across
    // presets with 8 as the default.
    [Export] public float ShaftBaseIntensity = 12f;
    // Dust amount at which shafts hit their base intensity. Lower =
    // shafts are visible even in thin dust; higher = only very dusty
    // air produces visible shafts. 0.3 keeps a "typical dusty" region
    // (dustAmount ~0.3) near the base intensity; a full desert at
    // dustAmount=0.5 lands around 13 (within the old dusty preset's 20).
    [Export(PropertyHint.Range, "0.001,1,0.001")] public float ShaftDustReference = 0.3f;
    // How much cloudCover dims shaft intensity. 0 = clouds don't
    // affect shafts; 0.5 = full overcast halves them.
    [Export(PropertyHint.Range, "0,1,0.01")] public float ShaftCloudDim = 0.5f;
    // How much DustColor tints shaft color away from pure SunColor /
    // MoonColor. Higher = shafts pick up the regional dust hue.
    [Export(PropertyHint.Range, "0,1,0.01")] public float ShaftDustColorMix = 0.3f;
    // Moon shafts are dimmer than sun shafts by this ratio. Moonlight
    // just doesn't scatter as brightly as sunlight.
    [Export(PropertyHint.Range, "0,1,0.01")] public float MoonShaftFactor = 0.5f;

    [ExportSubgroup("Direct Light Intensity")]
    // Floor for daytime intensity at full overcast. 1.0 = never dim;
    // ~0.4 = strongly dim. Applied via a smoothstep knee so partly-
    // cloudy days stay bright and only genuinely overcast skies duck.
    [Export(PropertyHint.Range, "0,1,0.01")] public float OvercastDim = 0.4f;
    // BASELINE cloudCover at which the overcast dim knee starts (at
    // humidity=0.5). HumidityKneeShift slides both start and end left
    // or right per frame based on the current humidity — low-humidity
    // cloud is thin with gaps (knee shifts right → stays bright longer),
    // high-humidity cloud is thick stratus (knee shifts left → dims
    // sooner). The SAME knee drives AmbientCloudLift so ambient and
    // direct invert in lockstep; if they didn't match, cloudCover in
    // the gap would add ambient without losing direct, brightening
    // the scene instead of dimming it.
    [Export(PropertyHint.Range, "0,1,0.01")] public float OvercastKneeStart = 0.5f;
    // Baseline cloudCover at which the overcast dim knee reaches
    // OvercastDim. Also shifts with HumidityKneeShift.
    [Export(PropertyHint.Range, "0,1,0.01")] public float OvercastKneeEnd = 1.0f;
    // How far humidity slides the knee. At humidity=0 the knee shifts
    // RIGHT by this amount (a thin dry overcast barely dims — sun
    // still punches through gaps); at humidity=1 it shifts LEFT by
    // this amount (a humid stratus layer starts dimming at low cover).
    // humidity=0.5 is neutral (no shift). Effective spread: a
    // cloudCover=0.7 swamp with humidity=0.95 dims much harder than a
    // cloudCover=0.7 dry mountain day.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float HumidityKneeShift = 0.3f;
    // Scale applied at humidity=1 as an always-on damper on direct
    // light. Humid air scatters more, noticeably dimming direct sun
    // in a humid swamp or jungle even when the sky isn't fully
    // overcast. 0.8 = 20% drop at full humidity, paired with a small
    // ambient lift so the net scene is dimmer AND flatter.
    [Export(PropertyHint.Range, "0,1,0.01")] public float HumidityDim = 0.8f;
    // Sunset intensity as a fraction of day intensity. Sunsets are
    // mellower than noon; 0.7 reads as "softened but still warm".
    [Export(PropertyHint.Range, "0,2,0.01")] public float SunsetIntensityFactor = 0.7f;
    // Base night intensity (moonlight as a fraction of noon sun).
    // Also directly scales MoonLight.LightEnergy so Godot's shadow
    // pass dims proportionally.
    [Export(PropertyHint.Range, "0,1,0.01")] public float NightIntensityBase = 0.3f;
    // Maximum day-intensity amplification when air is BOTH dry AND
    // cloudless. Desert sun is physically more intense than normal
    // noon (the sky dome doesn't absorb / scatter it as much) — this
    // lets arid regions exceed 1.0 while humid/cloudy regions stay at
    // or below 1.0. Uses min(1-humidity, 1-cloudCover) as the trigger
    // so EITHER condition being wet/cloudy cancels the boost.
    [Export(PropertyHint.Range, "1,2,0.01")] public float AridBoostMax = 1.5f;

    [ExportSubgroup("Ambient Light")]
    // Day ambient floor in CLEAR weather. Ambient is physically INVERSE
    // to direct intensity: a sunny day has crisp shadows (high direct,
    // low ambient); an overcast day has flat lighting (low direct,
    // high ambient). AmbientCloudLift does the inversion; this is the
    // clear-sky floor that even cloudless regions get. 0.15 keeps
    // crisp desert/mountain shadows visible (~7:1 contrast against
    // arid-boosted direct) without crushing them to near-black.
    [Export(PropertyHint.Range, "0,1,0.01")] public float DayAmbientBase = 0.15f;
    // Additional day ambient at humidity=1. Small — humid air scatters
    // more, but most of the ambient rise comes from clouds.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float AmbientHumidityLift = 0.1f;
    // Additional day ambient at cloudCover=1. Applied via the direct-
    // dim knee so partly-cloudy scenes stay crisp (ambient doesn't
    // rise until the sky actually closes up). For CLOUD-shadow
    // softness on the ground, use SkyController.cloudShadowStrength
    // instead — ambient is a scene-wide floor, cloud opacity is the
    // surgical tool for "clouds shouldn't crush shadows to black."
    [Export(PropertyHint.Range, "0,1,0.01")] public float AmbientCloudLift = 0.47f;
    // Sunset ambient as a multiplier on day ambient. Slightly elevated
    // because low sun = more atmosphere scattering.
    [Export(PropertyHint.Range, "0,2,0.01")] public float SunsetAmbientFactor = 1.1f;
    // Night ambient floor. Moonlit shadows are inky, so this stays low
    // (well below DayAmbientBase) to preserve the "crisp moon shadow"
    // look — see WeatherData comment on moon ambient for context.
    [Export(PropertyHint.Range, "0,1,0.01")] public float NightAmbientBase = 0.08f;
    // Additional night ambient at humidity=1. Foggy night = gloomy but
    // more ambient fill.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float NightAmbientHumidityLift = 0.05f;

    [ExportSubgroup("Water")]
    // Per-m/s of wind, how much ripple strength to add. 0.01 gives
    // 15m/s wind → rippleStrength 0.15.
    [Export(PropertyHint.Range, "0,0.1,0.001")] public float RippleWindK = 0.01f;
    // Per-unit of rainAmount, additional ripple strength. Rain patters
    // on water even without wind.
    [Export(PropertyHint.Range, "0,1,0.01")] public float RippleRainK = 0.3f;

    [ExportSubgroup("Wind Rhythm")]
    // Base frequency of the sprite-sway sine wave. Consumed by the
    // sprite sway shader via wind_phase integration.
    [Export(PropertyHint.Range, "0,5,0.01")] public float WindFreqBase = 1.0f;
    // Additional windFrequency at cloudCover=1. Stormy skies have
    // more agitated sway rhythms.
    [Export(PropertyHint.Range, "0,5,0.01")] public float WindFreqCloud = 0.8f;
    // Base gust frequency (Hz). Slow-breathing gust wave.
    [Export(PropertyHint.Range, "0,1,0.01")] public float GustFreqBase = 0.1f;
    // Additional gust frequency at cloudCover=1. Storms gust more.
    [Export(PropertyHint.Range, "0,1,0.01")] public float GustFreqCloud = 0.2f;
    // Gust peak as a fraction of windSpeed, clear-sky floor. At
    // cloudCover=0, gusts add up to this × windSpeed on top.
    [Export(PropertyHint.Range, "0,1,0.01")] public float GustMinFraction = 0.3f;
    // Additional fraction at cloudCover=1. Stormy skies gust harder
    // — peak adds GustMinFraction + GustCloudFraction × windSpeed.
    [Export(PropertyHint.Range, "0,1,0.01")] public float GustCloudFraction = 0.5f;

    [ExportSubgroup("Dust Density")]
    // Shader dustDensity = dustAmount * this. Old authored values
    // ranged 0.003 (clear) to 0.1 (dusty); K=0.1 maps dustAmount 0..1
    // onto that full range linearly. Our authored desert has
    // dustAmount=0.5 → dustDensity=0.05 (half of old dusty max).
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float DustDensityK = 0.1f;

    [ExportSubgroup("Rain")]
    // rainWeight at cloudCover=0 (scattered thin cloud). Light drizzle.
    // Multiplies rain fall velocity, drop alpha, streak length linearly,
    // and inversely scales wind tilt (lighter drops blow more).
    [Export(PropertyHint.Range, "0,3,0.01")] public float RainWeightMin = 0.3f;
    // rainWeight at cloudCover=1 (full overcast). Heavy downpour but
    // capped short of comically elongated streaks — 1.2 gives stormy
    // regions ~20% longer drops than default without turning rain into
    // lines across the whole screen.
    [Export(PropertyHint.Range, "0,3,0.01")] public float RainWeightMax = 1.2f;
    // Exponent shaping rainAmount → rainIntensity (drop COUNT). 1.0 is
    // linear; >1 compresses low authored values (a light drizzle at
    // rainAmount=0.3 emits fewer drops than a linear mapping would
    // suggest), while high values stay near the authored amount.
    [Export(PropertyHint.Range, "0.3,3,0.01")] public float RainIntensityExponent = 1.25f;
}
