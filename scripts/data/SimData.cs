using Godot;
using Godot.Collections;

// Static, authored world-level simulation constants. Mutable runtime state
// (TimeOfDay01, WindDirection, ShadowLightDirection, etc.) lives on WorldState
// — `Data` is never used for mutable values (see CLAUDE.md conventions).
[GlobalClass]
public partial class SimData : Resource
{
    [Export] public float Gravity = 9.8f;

    // Master recipe library. CookingScreen iterates this list to match the
    // current cooking inputs against an authored recipe. Discovery for any
    // hit is recorded in WorldSimState.DiscoveredRecipes keyed by the same
    // RecipeData reference. Adding a recipe = adding it here.
    [Export] public Array<RecipeData> Recipes = new();

    // Master mob library. BestiaryScreen iterates this list, filtering to
    // the entries the player has discovered (WorldSimState.DiscoveredMobs),
    // so the bestiary's row ordering tracks the authored order here rather
    // than discovery order. Adding a new mob species = adding it here so it
    // can appear in the bestiary once spotted.
    [Export] public Array<MobData> Mobs = new();

    // Shared item-leveling thresholds. Entry i is the cumulative exp required
    // to reach level (i+1); WeaponState.AddExp / ArmorState.AddExp walk this
    // list and promote level while the running total has crossed the next
    // entry. Per-item ItemData.maxLevel caps how many of these entries the
    // item is allowed to consume (a maxLevel=0 item never levels regardless).
    [Export] public Array<int> ExpPerLevel = new() { 100, 200, 500, 2000, 10000 };
    [Export] public float VisibleTime = 0.25f;
    // World-wide threshold for "fully visible to perception". Light readings
    // at the target's sample point are clamped to [0, this] then divided
    // by it to produce a 0..1 light factor. One global value rather than
    // per-mob / per-discoverable so light contribution is consistent
    // across every percept; per-target tuning of how easily a thing is
    // spotted lives in `prominence` and the detected/discovered
    // thresholds instead.
    [Export] public float TargetLightMax = 0.75f;

    [ExportGroup("Time of Day")]
    // Seconds of wall-clock time for a full day/night cycle at time_scale = 1.
    // The time_scale CVar multiplies this advancement for fast-forward testing.
    [Export] public float DayLengthSeconds = 600f;

    // Normalized time the world starts at: 0 = midnight, 0.25 = sunrise,
    // 0.5 = noon, 0.75 = sunset. Applied when a fresh game is started.
    [Export(PropertyHint.Range, "0,1,0.001")] public float InitialTimeOfDay = 0.3f;

    // Sun's elevation above the horizon at noon. 90 = sun passes through
    // zenith; lower values produce a shallower arc (higher-latitude look).
    // Drives both visual sky placement AND the simulation-side
    // ShadowLightDirection that gameplay raycasts query.
    [Export(PropertyHint.Range, "10,90,1")] public float SunMaxElevationDegrees = 60f;

    // Compass direction where the sun is at noon. 0° = +Z (world north),
    // 90° = +X (world east), 180° = -Z, 270° = -X. Combined with
    // SunMaxElevationDegrees, this fully specifies the noon sun direction.
    // The sun's orbit is a great circle in the plane containing this noon
    // direction and the horizontal axis perpendicular to it — sun rises
    // 90° clockwise from noon, sets 90° counter-clockwise, passes under
    // the anti-noon direction at midnight. This models both hemispheres:
    // set NoonAzimuthDegrees toward the sky hemisphere where the sun
    // actually passes (north for southern-hemisphere scenes, south for
    // northern-hemisphere scenes).
    //
    // Example: for a world where +X+Z is "north" and the observer is in
    // the southern hemisphere (so sun passes through north at noon),
    // set NoonAzimuthDegrees = 45 and SunMaxElevationDegrees to latitude-
    // derived value (90° - |latitude|).
    [Export(PropertyHint.Range, "0,360,1")] public float NoonAzimuthDegrees = 45f;

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

    // Width (degrees) of the sunrise/sunset color fade-out band, added
    // on each side of SunsetAngleDegrees. The sunset color variants are
    // at full strength across |elev| <= SunsetAngleDegrees (symmetric
    // across horizon crossing, so pre-dawn and post-dawn both stay warm),
    // then fade out between SunsetAngleDegrees and SunsetAngleDegrees
    // + this. Also parameterizes the ambient blend that gameplay
    // stealth/perception consumes.
    [Export(PropertyHint.Range, "1,45,0.5")] public float SunsetColorRangeDegrees = 10f;

    [ExportGroup("Weather Derivation Tuning")]
    // Every knob below shapes how WeatherDerivation turns (zone,
    // weather, time-of-day) into the concrete visual outputs pushed
    // to shaders and lights. Defaults are tuned to roughly match the
    // pre-simplification look; this is the one place to retune the
    // feel without editing code. Grouped by output channel.

    [ExportSubgroup("Sky Colors")]
    // Day horizon = SkyColor brightened by this factor (lightens the
    // near-horizon band). 1 = no lift; 1.3 = noticeable atmospheric
    // glow near the horizon.
    [Export(PropertyHint.Range, "0.5,2,0.01")] public float DayHorizonBrightness = 1.2f;
    // How much the day horizon tilts toward the zone's SunColor.
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
    // How much of the zone's MoonColor bleeds into the night horizon.
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
    // dust zones get dramatic warm-underbelly clouds at sunset.
    [Export(PropertyHint.Range, "0,1,0.01")] public float SunsetCloudDustMix = 0.4f;
    // Night cloud color = lerp(dark gray, MoonColor, this). Keeps
    // night clouds visible against a dark sky without going full moon
    // tint.
    [Export(PropertyHint.Range, "0,1,0.01")] public float NightCloudMoonMix = 0.7f;

    [ExportSubgroup("Fog")]
    // Fog is fully derived: WeatherDerivation computes a [0, 1] fog
    // signal from simulated humidity and the cool-half-of-day diurnal
    // (FogFromHumidity / FogFromCoolDiurnal weights live in the
    // Weather Simulation > Simulated Derived subgroup) and exposes it
    // as DerivedPalette.Fog. The constants below shape how that signal
    // turns into voxel density / ambient haze / disk dimming.
    //
    // Fog tint uses ZoneData.DustColor directly — DustColor is a
    // regional palette / theming color and is the right intrinsic fog
    // tint. Phase dimming and sun/moon warmth through fog come from
    // the shader's shaft_color (phase-blended) and lighting response,
    // not from pre-baking night/sunset tints here.
    // Voxel fog density = fog × this. fog=1 at K=0.1 saturates near
    // the high end, leaving headroom so "full fog" doesn't wall off
    // sight entirely.
    [Export(PropertyHint.Range, "0,1,0.001")] public float FogDensityK = 0.1f;
    // Ambient (non-map) distance haze from the derived fog signal.
    // The shape is `pow(fog, FogCurveExponent) * K`: a concave curve
    // (exponent < 1) lets low fog values still read as visible haze
    // while damping high values so a fully humid pre-dawn fog doesn't
    // over-saturate into pea soup.
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
    // visible trace so heavy-fog zones still read as foggy under
    // moonlight, just much dimmer than day.
    [Export(PropertyHint.Range, "0,1,0.01")] public float FogIntensityFloor = 0.2f;
    // Additional ambient haze from humidity. Zero by default — humid-
    // but-clear zones shouldn't look foggy. Re-enable if you want
    // tropical zones to feel hazier than their authored fog alone
    // would produce.
    [Export(PropertyHint.Range, "0,0.05,0.0005")] public float AmbientFogHumidityK = 0f;

    [ExportSubgroup("Shafts")]
    // Base sun-shaft intensity before dust / cloud modulation. 12 lets
    // a dusty zone (mountain/desert at dustAmount=0.4) produce shafts
    // comfortably into the "visible beam" range (effective 12-16) while
    // low-dust zones stay subtle. Old authored values ran 3-20 across
    // presets with 8 as the default.
    [Export] public float ShaftBaseIntensity = 12f;
    // Dust amount at which shafts hit their base intensity. Lower =
    // shafts are visible even in thin dust; higher = only very dusty
    // air produces visible shafts. 0.3 keeps a "typical dusty" zone
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
    // Absolute clear-noon sunlight intensity — the single sun knob.
    // Pre-multiplied into _palette.PrimaryIntensity by WeatherDerivation,
    // then weather-modulated by cloudIntensityScale × humidityIntensityScale ×
    // aridBoost at runtime. SkyController feeds the result into both
    // CurrentPrimaryIntensity (scene illumination, sun_intensity shader
    // global) and SunLight.LightEnergy.
    [Export(PropertyHint.Range, "0,4,0.01")] public float DayIntensityBase = 2f;
    // Absolute clear-night moonlight intensity — the single moon knob.
    // Modulated by cloudIntensityScale and becomes _palette.NightPrimaryIntensity,
    // which SkyController feeds into both CurrentPrimaryIntensity (scene
    // illumination) and MoonLight.LightEnergy (Godot's shadow pass).
    [Export(PropertyHint.Range, "0,2,0.01")] public float NightIntensityBase = 0.75f;
    // Maximum day-intensity amplification when air is BOTH dry AND
    // cloudless. Desert sun is physically more intense than normal
    // noon (the sky dome doesn't absorb / scatter it as much) — this
    // lets arid zones exceed 1.0 while humid/cloudy zones stay at
    // or below 1.0. Uses min(1-humidity, 1-cloudCover) as the trigger
    // so EITHER condition being wet/cloudy cancels the boost.
    [Export(PropertyHint.Range, "1,2,0.01")] public float AridBoostMax = 1.5f;

    [ExportSubgroup("Ambient Light")]
    // Day ambient floor in CLEAR weather. Ambient is physically INVERSE
    // to direct intensity: a sunny day has crisp shadows (high direct,
    // low ambient); an overcast day has flat lighting (low direct,
    // high ambient). AmbientCloudLift does the inversion; this is the
    // clear-sky floor that even cloudless zones get. 0.15 keeps
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

    [ExportSubgroup("Mob Torches")]
    // Hysteresis pair for the mob "should I light my torch" decision. With a
    // single threshold, ambient drifting around the cutoff (e.g. dawn/dusk,
    // partial torchlight from another mob, weather variation) flickers the
    // torch on/off every tick. The gap is the noise margin: while the torch
    // is OFF, ambient must drop below the LIGHT threshold to ignite it;
    // while ON, ambient must rise above the DOUSE threshold to extinguish.
    // Light < Douse (enforced at read time in Mob.ShouldUseTorch).
    [Export(PropertyHint.Range, "0,2,0.01")] public float MobTorchLightThreshold = 0.20f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float MobTorchDouseThreshold = 0.30f;

    [ExportSubgroup("Water")]
    // Reference wind speed (m/s) at which ripple_strength saturates to 1.
    // Curve is quadratic: (wind / ref)² — low wind barely perturbs the
    // surface so the sun disk can reflect coherently, high wind fully
    // breaks it up. Below ~2 m/s the surface is near-mirror.
    [Export(PropertyHint.Range, "2,30,0.1")] public float RippleWindRef = 10f;
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

    [ExportGroup("Weather Simulation")]
    // Diurnal weather variation. Authored ZoneData.weather values are
    // treated as the zone's MAX for each channel; WeatherSimulation
    // perturbs a per-frame working copy in place using:
    //   1. A diurnal sine curve peaking at DiurnalPeak01, bottoming at
    //      DiurnalTrough01 — drives baseline humidity / temperature /
    //      wind / cloud cover with channel-specific weights.
    //   2. A 12-hour weather variance value that re-rolls every
    //      VarianceHours and smooth-lerps from prev→next across the
    //      sunrise/sunset window. The signed delta of that lerp drives
    //      wind transients; the variance itself drives the humidity /
    //      cloud / temperature swing around the diurnal baseline.
    //   3. Cross-couplings (humid air retains heat, wind brings cloud,
    //      humid+warm air rises into cloud, dust needs dry air & wind,
    //      fog settles in cool humid lows, etc.).
    // All weights live here so designers can retune the feel without
    // touching code. `Baseline*` knobs shape the diurnal max envelope;
    // `Variance*` knobs shape the per-12h perturbation around it.

    [ExportSubgroup("Diurnal Curve")]
    // Normalized time-of-day at which the diurnal curve peaks (max
    // temperature, max wind, peak dust lift). 0.6 ≈ early afternoon.
    [Export(PropertyHint.Range, "0,1,0.001")] public float DiurnalPeak01 = 0.6f;
    // Normalized time-of-day at which the diurnal curve troughs
    // (coolest point of the day, fog max, lowest wind). 0.275 ≈ just
    // after sunrise.
    [Export(PropertyHint.Range, "0,1,0.001")] public float DiurnalTrough01 = 0.275f;

    [ExportSubgroup("Weather Variance")]
    // Game-hours between weather-variance re-rolls. The simulation
    // holds a `prev` and `next` value; the active value smooth-lerps
    // from prev→next across the sunrise/sunset window, so frontal
    // changes only "land" at dawn/dusk rather than mid-afternoon.
    [Export(PropertyHint.Range, "1,48,0.5")] public float VarianceHours = 12f;
    // Half-width of the smooth-lerp band around sunrise / sunset, in
    // normalized time-of-day. 0.05 ≈ ~70m at a 600s day length: the
    // variance crosses from prev→next over a window centered on
    // sunrise (0.25) or sunset (0.75).
    [Export(PropertyHint.Range, "0.005,0.2,0.005")] public float VarianceCrossfadeHalfWidth01 = 0.05f;

    [ExportSubgroup("Baseline (Diurnal)")]
    // Baseline humidity = humidityMax × diurnalCurveOffset(humidity) ×
    // (1 - elevation × ElevHumidity) × (1 - normalizedMaxTemp × HumidityFromMaxTemp)
    // Hot zones give up moisture (deserts dry out as the max temp rises),
    // cool zones hold humidity near the max.
    [Export(PropertyHint.Range, "0,1,0.01")] public float HumidityFromMaxTemp = 0.35f;
    // Diurnal swing depth on humidity: 0 = humidity stays at max all day,
    // 1 = humidity hits 0 at the diurnal peak. Real-world humidity dips
    // mid-afternoon (warm air holds more before saturating) and peaks
    // pre-dawn — implemented via the INVERTED diurnal curve.
    [Export(PropertyHint.Range, "0,1,0.01")] public float HumidityDiurnalDepth = 0.4f;
    // Elevation reduces baseline humidity (alpine air is dry).
    [Export(PropertyHint.Range, "0,1,0.01")] public float HumidityFromElevation = 0.5f;

    // Baseline temperature follows the diurnal curve, damped by humidity
    // (humid air resists swings — warm nights, cool days). Elevation
    // pulls the whole curve down (alpine cool).
    [Export(PropertyHint.Range, "0,1,0.01")] public float TempDiurnalDepth = 0.55f;
    // Humidity damps the diurnal swing (humid jungle = small day/night
    // delta; dry desert = huge delta).
    [Export(PropertyHint.Range, "0,1,0.01")] public float TempHumidityDamping = 0.4f;
    // Elevation cools the baseline (subtracts from the diurnal envelope).
    // Multiplied against authored max temperature so it scales with the
    // zone's heat budget.
    [Export(PropertyHint.Range, "0,1,0.01")] public float TempFromElevation = 0.4f;

    // Baseline wind = windMax × (diurnal × WindDiurnalDepth + (1 -
    // WindDiurnalDepth)) × (1 + signedCoolingRate × WindFromTempDiff)
    //                         × (1 + elevation × WindFromElevation)
    // signedCoolingRate is the negated diurnal slope clamped to
    // [-1, +1]: +1 at the steepest cooling point (afternoon → evening
    // thermal collapse, when convection cells dump downslope and ground
    // wind rises), -1 at the steepest warming point (mid-morning, when
    // ground heats and the air column is still settled). Combined with
    // the WindDiurnalDepth scale (which itself peaks at the afternoon
    // diurnal max), this lands the daily wind peak in the late
    // afternoon / early evening, with a calm pre-dawn and a calmer
    // late-morning. Alpine zones get a fixed elevation boost on top.
    [Export(PropertyHint.Range, "0,1,0.01")] public float WindDiurnalDepth = 0.3f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float WindFromTempDiff = 0.5f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float WindFromElevation = 0.6f;

    // Baseline cloud cover depends on wind (clouds blowing in) and on
    // humid+warm air rising to the cloud layer.
    [Export(PropertyHint.Range, "0,1,0.01")] public float CloudFromWind = 0.35f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float CloudFromHumidityWarmth = 1.0f;
    // Diurnal damping on cloud cover. Storms can roll in any time, but
    // typical convective cloud builds across the day and dissipates
    // overnight. Small by design — 0 keeps clouds at the max regardless
    // of time of day.
    [Export(PropertyHint.Range, "0,1,0.01")] public float CloudDiurnalDepth = 0.2f;

    [ExportSubgroup("Variance (Per-12h)")]
    // Variance lives in [0, 1]; 0 = stormy / unstable, 1 = fair / stable.
    // Each channel's "K" is the AMPLITUDE of the perturbation around
    // baseline. variance=0.5 is neutral (no perturbation).

    // Wind picks up two variance contributions, both bidirectional:
    //   1. WindVarianceK — center term: stormy days (variance < 0.5,
    //      varianceCenter < 0) push wind ABOVE baseline; fair days
    //      (variance > 0.5) push it BELOW. Sustained, not transient.
    //   2. WindVarianceDeltaK — |dVariance/dt| frontal kick: any
    //      handover between variance values lifts wind for the
    //      duration of the sunrise/sunset crossfade window.
    // SimWind = baselineWind × (1 - varianceCenter·2·WindVarianceK)
    //                        × (1 + |slope|·WindVarianceDeltaK).
    [Export(PropertyHint.Range, "0,1,0.01")] public float WindVarianceK = 0.3f;
    [Export(PropertyHint.Range, "0,5,0.01")] public float WindVarianceDeltaK = 1.5f;

    // Humidity uses its OWN independent variance channel. The
    // perturbation is GATED by simulated wind speed: 0 wind = no
    // advection, baseline holds; full wind = full influence. Models
    // "neighboring weather is being blown in". Symmetric around 0.5.
    [Export(PropertyHint.Range, "0,1,0.01")] public float HumidityVarianceK = 0.4f;

    // Cloud cover uses its own independent variance channel, gated by
    // wind for the same reason — clouds are physically advected, so a
    // calm day stays at the regional baseline regardless of what the
    // variance rolled.
    [Export(PropertyHint.Range, "0,1,0.01")] public float CloudVarianceK = 0.6f;

    // Wind speed (m/s) at which the wind-gated variance influence
    // (humidity & cloud) reaches its full strength. Below this the
    // perturbation is scaled linearly down to 0 at zero wind. Tuned
    // to roughly match the same wind range that breaks up the water
    // surface (RippleWindRef) — a "strong but not extreme" wind.
    [Export(PropertyHint.Range, "1,30,0.1")] public float AdvectedVarianceWindRef = 8f;

    // Temperature: positively related to variance (fair days are hot),
    // but |delta| in variance subtracts (changing weather is unstable
    // and cools the scene off).
    [Export(PropertyHint.Range, "0,1,0.01")] public float TempVarianceK = 0.2f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float TempVarianceDeltaK = 0.4f;

    [ExportSubgroup("Simulated Derived")]
    // Fog forms ONLY when humid air cools — both axes are required
    // (cold dry air doesn't fog; warm humid air doesn't fog), so
    // WeatherDerivation multiplies them. The values below are the
    // EXPONENTS shaping each axis: > 1 narrows the curve so only
    // extreme humidity / cold produces fog, < 1 widens it so even
    // moderate values lift some fog. Default 1.5 on humidity gives
    // dry zones (desert humidity ~0.04) almost no fog while keeping
    // swampy zones (humidity ~0.95) nearly fully fogged at the
    // diurnal trough. There is no per-zone fog ceiling — a swamp
    // gets foggy because of its high baseline humidity, not a
    // separate authored fog field.
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float FogFromHumidity = 1.5f;
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float FogFromCoolDiurnal = 1.0f;

    // Rain needs heavy cloud AND falling temperature (cold front /
    // afternoon-thunderstorm pattern). Falling-temp signal = max(0,
    // -dDiurnalCurve/dt). Authored rainMax is the ceiling.
    [Export(PropertyHint.Range, "0,2,0.01")] public float RainFromCloudCover = 1.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RainCloudThreshold = 0.5f;
    [Export(PropertyHint.Range, "0,5,0.01")] public float RainFromCoolingRate = 2.0f;

    // Dust: wind × elevation × diurnal-warmth, suppressed by humidity
    // and rain. Authored dustMax is the ceiling.
    [Export(PropertyHint.Range, "0,2,0.01")] public float DustFromWind = 1.0f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float DustFromElevation = 0.5f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float DustFromWarmth = 0.6f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DustHumiditySuppression = 0.8f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DustRainSuppression = 0.95f;

    [ExportSubgroup("Rain")]
    // rainWeight at cloudCover=0 (scattered thin cloud). Light drizzle.
    // Multiplies rain fall velocity, drop alpha, streak length linearly,
    // and inversely scales wind tilt (lighter drops blow more).
    [Export(PropertyHint.Range, "0,3,0.01")] public float RainWeightMin = 0.3f;
    // rainWeight at cloudCover=1 (full overcast). Heavy downpour but
    // capped short of comically elongated streaks — 1.2 gives stormy
    // zones ~20% longer drops than default without turning rain into
    // lines across the whole screen.
    [Export(PropertyHint.Range, "0,3,0.01")] public float RainWeightMax = 1.2f;
    // Exponent shaping rainAmount → rainIntensity (drop COUNT). 1.0 is
    // linear; >1 compresses low authored values (a light drizzle at
    // rainAmount=0.3 emits fewer drops than a linear mapping would
    // suggest), while high values stay near the authored amount.
    [Export(PropertyHint.Range, "0.3,3,0.01")] public float RainIntensityExponent = 1.25f;

    [ExportGroup("Footprints")]
    // Two shared scenes — one always-visible (player prints, and mob prints
    // laid while the player was already aware of the mob), one with an
    // internal Discoverable child that gates visibility on the player
    // perceiving the decal itself. Authoring this here rather than
    // per-actor: the visible/discoverable choice is a binary that doesn't
    // vary per-character, and the textures are what differ between actors
    // (carried per-actor via Player/Mob's _footprintTexture).
    // World.SpawnFootprint picks the scene from this pair using the
    // actor-supplied `gated` flag.
    [Export] public PackedScene FootprintVisible;
    [Export] public PackedScene FootprintDiscoverable;
    // Per-ground-type tint applied to every footprint laid down on that
    // surface. The Color's RGB tints the actor's footprint texture (sand
    // → warm tan, mud → dark brown, snow → white); the Color's ALPHA is
    // the baseline opacity at spawn and is what the runtime fades to 0
    // over FootprintDurationSeconds. Surfaces that shouldn't take prints
    // (wood, treated stone) leave their key out of the dictionary — the
    // emitter treats missing keys as no-emit. Wet status effects multiply
    // alpha and duration via the StatusEffectData footprint multipliers.
    [Export] public Godot.Collections.Dictionary<EGroundType, Color> FootprintColors = new()
    {
        { EGroundType.Grass, new Color(0.18f, 0.14f, 0.08f, 0.45f) },
        { EGroundType.Sand,  new Color(0.22f, 0.18f, 0.12f, 0.75f) },
        { EGroundType.Mud,   new Color(0.10f, 0.07f, 0.04f, 0.85f) },
        { EGroundType.Dirt,  new Color(0.18f, 0.14f, 0.08f, 0.55f) },
        { EGroundType.Stone, new Color(0.15f, 0.15f, 0.15f, 0.18f) },
    };
    // Global fade lifetime — seconds for a fresh print to dim from its
    // baseline alpha to zero (then despawn). One global value rather than
    // per-ground because surface-specific persistence is already encoded
    // in the per-ground baseline alpha; a faint print can't visually
    // outlast a deep one anyway.
    [Export] public float FootprintDurationSeconds = 15f;
}
