using Godot;
using Godot.Collections;

// Static, authored world-level simulation constants. Mutable runtime state
// (TimeOfDay01, WindDirection, ShadowLightDirection, etc.) lives on WorldState
// — `Data` is never used for mutable values (see CLAUDE.md conventions).
[GlobalClass]
public partial class SimData : Resource
{
    [Export] public float Gravity = 9.8f;

    // Stuck-recovery: if the player is airborne with effective speed below
    // PlayerStuckVelocityThreshold for PlayerStuckTimeoutSeconds, they're
    // wedged (e.g. in a 1-voxel crevice where IsOnFloor never trips because
    // the bottom of the capsule pinches against wall normals instead of the
    // floor). Player teleports back to the last position they were grounded.
    // "Effective speed" is measured as actual per-tick displacement, NOT the
    // Velocity field: MoveAndSlide's slide projection barely damps Y against
    // near-vertical wall normals, so Velocity runs to a huge terminal value
    // in the wedged case even when the body isn't moving. Threshold is m/s
    // of real displacement; deadline is pushed forward every tick the
    // player is actually moving, so this only fires on true motion stalls.
    [Export] public float PlayerStuckTimeoutSeconds = 1.5f;
    [Export] public float PlayerStuckVelocityThreshold = 0.1f;

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

    // Central registry of named scripting variables (quest flags, world
    // state). Seeded into WorldSimState.ScriptVars at world creation so
    // ScriptVarCondition / ScriptVarTransition / SetScriptVarAction can branch
    // conversations and behaviors by name. Null = no variables in this world.
    [Export] public ScriptVariableRegistry ScriptVariables;

    // Status effect applied to every elite mob at spawn, in addition to the
    // signature effect(s) the elite's own descriptor authors (MobDescriptor
    // .statusEffects). Authored once here so the shared elite buff — larger
    // health, etc. — is consistent across all elites rather than copy-pasted into
    // every *_elite.tres. Null = no shared effect.
    [Export] public StatusEffectData EliteStatusEffect;

    // Spinning, bobbing emissive halo floated over every elite mob (Mob.IsElite)
    // as an at-a-glance "this one's tougher" marker. Authored once here — the
    // crown is species-agnostic — so Mob can spawn it for any elite without
    // per-species .tscn wiring. Null disables the marker (elites still scale +
    // carry their effects). See EliteCrown / crown_lit.tres.
    [Export] public PackedScene EliteCrownScene;

    // Loot ejected by every elite mob on death, on top of its species loot.
    // Authored once here — the drop is species-agnostic, the trophy form of the
    // EliteCrownScene halo — so any elite drops it without per-species wiring.
    // Null disables the trophy drop. See Mob.EjectLoot.
    [Export] public LootData EliteLoot;

    // Fairy-loot boons. A fairy corpse (FairyLoot) draws its candidate boons
    // from FairyBoons, composed onto the corpse's per-instance ItemState when it
    // spawns (World.SpawnLoot) so one can be applied on use and chosen by the
    // player. Centralized here — rather than on the loot entry in the fairy's
    // .tres — so the boon pool is tuned in one place, mirroring the Elite
    // loot/effect pairing above. Empty list (or null FairyLoot) = the corpse
    // bestows nothing.
    [Export] public ConsumableData FairyLoot;
    [Export] public Array<BoonData> FairyBoons = new();

    // Filler boon for the fairy upgrade screen. The screen only offers boons
    // from FairyBoons that are currently VIABLE (a restorative boon is hidden at
    // full health; a lasting buff is hidden when already active), so the choice
    // count can fall below the three cards the screen wants. When it does, this
    // boon pads the list out. Authored as the Gold boon — a never-wasted
    // consolation — and deliberately NOT in FairyBoons so it's only ever offered
    // as a filler, never a random roll. Null = no padding (the screen just shows
    // however many viable boons remain).
    [Export] public BoonData FairyBoonGold;

    // Shared interactive verbs auto-injected on any mob whose runtime
    // SimState carries a Conversation. Authored here once so adding a new
    // talking NPC species doesn't require copy-pasting Talk / GiveItem
    // sub-resources into each mob's .tscn — give the mob a Conversation
    // (via WorldGen or SimState) and these verbs surface automatically.
    // TradeAction replaces GiveItemAction on mobs whose MobSimState.WillTrade
    // is true; the two are mutually exclusive on any given mob.
    [Export] public InteractiveAction TalkAction;
    [Export] public InteractiveAction GiveItemAction;
    [Export] public InteractiveAction TradeAction;

    // Shared interactive verb auto-injected on any dead tamed companion's
    // corpse (see Mob.CanRevive) so reviving doesn't require per-species
    // authoring — give a mob a positive MobData.tameLoyalty and it becomes
    // revivable once tamed and killed. Authored once here as a 3-second
    // hold whose OpenInteractive completion event calls Mob.Complete →
    // Revive(). Null disables companion revival entirely. The health a
    // companion comes back with is per-species (MobData.reviveHealth).
    [Export] public InteractiveAction ReviveAction;

    // Grammar's contribution to TextScrambler.ComputeComprehension. Final
    // understanding = translatedPct × ((1 - this) + orderPct × this), so:
    //   0   = grammar irrelevant, only word translation counts.
    //   1   = grammar fully gates — words landing in the wrong order
    //         multiply translation down toward zero.
    //   0.2 = grammar contributes 20% of the final score; a player who
    //         knows every vocab bucket but no grammar still understands
    //         ~80% of any text, missing a soft tax for jumbled order.
    [Export(PropertyHint.Range, "0,1,0.01")] public float LanguageGrammarWeight = 0.2f;

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

    // Normalized time-of-day of sunrise / sunset (0 = midnight, 0.5 = noon).
    // Daytime is [SunriseTimeOfDay, SunsetTimeOfDay); the camp screen's "Until
    // Sunrise/Sunset" rest reads these to pick its label and target time.
    [Export(PropertyHint.Range, "0,1,0.001")] public float SunriseTimeOfDay = 0.25f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float SunsetTimeOfDay = 0.75f;

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
    // Shaft COLOUR only — how much the regional DustColor tints the shaft
    // away from pure SunColor / MoonColor (zone-appearance, so it lives with
    // the palette derivation). Shaft INTENSITY and its weather response are
    // client-side visual tuning on SkyController (shaftWash* / moonBeamScale),
    // NOT here.
    [Export(PropertyHint.Range, "0,1,0.01")] public float ShaftDustColorMix = 0.3f;

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
    // Humid air carries its own light-scattering medium (haze droplets), so
    // a humid zone can show beams through partial cloud even with low
    // authored dust. Adds humidity * this to the effective dust amount
    // before DustDensityK. 0 = humidity contributes no scattering medium;
    // 0.5 lets a fully-humid zone scatter like ~0.5 dustAmount.
    [Export(PropertyHint.Range, "0,2,0.01")] public float DustFromHumidity = 0.5f;

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
    // The diurnal curve is a flat-topped trapezoid: a day plateau
    // centered on DiurnalPeak01 (full diurnal = 1), a night plateau
    // centered on DiurnalTrough01 (diurnal = 0), and SmoothStep ramps
    // in between. Plateau half-width is DiurnalPlateauHalfWidth.
    //
    // Defaults: day plateau centered on noon (tod = 0.5) with
    // half-width 0.125 — so the day stays "at peak" from tod = 0.375
    // through tod = 0.625 (halfway between sunrise and noon through
    // halfway between noon and sunset). Night plateau centered on
    // midnight (tod = 0.0, wrapping) covers tod = 0.875 through 0.125.
    // Warming ramp lives between [0.125, 0.375], cooling between
    // [0.625, 0.875]; coolingRate (= max(0, -slope)) is therefore
    // strictly positive only inside the cooling ramp and exactly zero
    // on either plateau and on the warming half.
    [Export(PropertyHint.Range, "0,1,0.001")] public float DiurnalPeak01 = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float DiurnalTrough01 = 0.0f;
    // Half-width of the day plateau (and, symmetrically, the night
    // plateau). 0.125 puts the plateau edges at sunrise+sunset/2 and
    // noon±0.125 / midnight±0.125. Width 0 collapses the trapezoid to
    // a triangle wave with peaks at the plateau centers; 0.25 makes
    // the day and night plateaus touch at sunrise / sunset, killing
    // the ramps entirely.
    [Export(PropertyHint.Range, "0,0.249,0.005")] public float DiurnalPlateauHalfWidth = 0.125f;

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

    // Cloud cover is the sum of two physically-distinct components:
    //
    //   STRATIFORM (authored): cloudMax × (1 + (windFraction-1) × CloudFromWind)
    //     The "weather system" present in this zone — overcast / nimbostratus
    //     from frontal systems, low pressure cells. Wind brings systems in
    //     (CloudFromWind). Persists day AND night — no diurnal scale. Variance
    //     perturbs this channel only.
    //
    //   CONVECTIVE (derived): simHumidity × diurnal × ConvectiveStrength
    //     Cumulus / cumulonimbus from warm humid air rising. Peaks in the
    //     afternoon (× diurnal), zero overnight (× 0). Not authorable — falls
    //     out of the humidity and temperature simulation automatically.
    //
    // simCloud = clamp(stratiform + convective, 0, 1). A storm front + hot
    // humid afternoon produces saturated cloud; a clear hot humid afternoon
    // produces afternoon-only cumulus; a stormy night stays cloudy because
    // stratiform doesn't diurnal-fade.
    [Export(PropertyHint.Range, "0,1,0.01")] public float CloudFromWind = 0.35f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float ConvectiveStrength = 1.0f;

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
    // Low-end dead-zone on the fog signal: fog below this collapses to 0, then
    // the remainder is rescaled to [0,1]. Stops the concave AmbientFog curve
    // from amplifying a trace humidity wisp into visible haze, so a nearly-dry
    // desert reads as genuinely clear. Heavy fog (swamp) is barely affected.
    [Export(PropertyHint.Range, "0,0.9,0.01")] public float FogFloor = 0.1f;

    // Rain needs heavy cloud AND falling temperature (cold front /
    // afternoon-thunderstorm pattern). Falling-temp signal = max(0,
    // -dDiurnalCurve/dt). Authored rainMax is the ceiling.
    [Export(PropertyHint.Range, "0,2,0.01")] public float RainFromCloudCover = 1.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RainCloudThreshold = 0.5f;
    [Export(PropertyHint.Range, "0,5,0.01")] public float RainFromCoolingRate = 2.0f;

    // WET-MODE thunderstorm: heavy cloud × active rain. The air-mass /
    // frontal thunderstorm — warm humid afternoon convection. The
    // dominant mode for forest, swamp, and other temperate zones.
    // SmoothStep from threshold to 1.0 on both axes; both must be high
    // for the gate to open, so a wet day with thin cloud (or a stormy
    // sky with no rain) produces no wet-mode lightning.
    [Export(PropertyHint.Range, "0,1,0.01")] public float LightningCloudThreshold = 0.7f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float LightningRainThreshold = 0.4f;

    // DRY-MODE thunderstorm: cloud × low humidity × high temperature.
    // High-base storm in hot arid air — virga (rain evaporates before
    // reaching ground), wildfire-igniting strikes. Desert summers. NO
    // rain required, so the wet gate's rain threshold doesn't apply.
    // Cloud threshold is lower than wet because dry storms typically
    // have less total cloud coverage but the cloud they do have reaches
    // very high (cumulonimbus).
    [Export(PropertyHint.Range, "0,1,0.01")] public float DryLightningCloudThreshold = 0.3f;
    // Humidity inversion: humidity below this contributes, above shuts
    // the gate. Sweep is 0 → DryLightningHumidityMax (so humidity = 0
    // is full dry, humidity = max kills the gate).
    [Export(PropertyHint.Range, "0,1,0.01")] public float DryLightningHumidityMax = 0.3f;
    // Air temperature (°F) sweep for the heat axis. Below TempMin the
    // gate is shut; above TempMax it's fully open. ~75 → 95°F matches
    // the temperature range where atmospheric instability lifts dry
    // storm activity in real climates.
    [Export] public float DryLightningTempMin = 75f;
    [Export] public float DryLightningTempMax = 95f;

    // OROGRAPHIC-MODE thunderstorm: cloud × strong wind × high
    // elevation. Air forced up a mountainside, condenses on the
    // windward slope, lightning concentrates along ridgelines. Mountain
    // zones. NO rain required (orographic storms often have lighter
    // precipitation than air-mass storms but more dramatic lightning).
    [Export(PropertyHint.Range, "0,1,0.01")] public float OrographicLightningCloudThreshold = 0.4f;
    // Wind speed (m/s) sweep. Below WindMin no lift; above WindMax full
    // gate. Matched roughly to AdvectedVarianceWindRef so "wind that
    // moves weather around" also drives orographic activity.
    [Export(PropertyHint.Range, "0,40,0.1")] public float OrographicLightningWindMin = 6f;
    [Export(PropertyHint.Range, "0,40,0.1")] public float OrographicLightningWindMax = 14f;
    // Zone elevation (0..1, blended runtime ZoneState.Elevation) sweep.
    // ElevationMin → 1.0. Default 0.5 means only the upper half of the
    // elevation range qualifies — flatland zones don't get orographic
    // lightning regardless of cloud/wind.
    [Export(PropertyHint.Range, "0,1,0.01")] public float OrographicLightningElevationMin = 0.5f;

    // Dust: wind × elevation × diurnal-warmth, suppressed by humidity
    // and rain. Authored dustMax is the ceiling.
    [Export(PropertyHint.Range, "0,2,0.01")] public float DustFromWind = 1.0f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float DustFromElevation = 0.5f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float DustFromWarmth = 0.6f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DustHumiditySuppression = 0.8f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DustRainSuppression = 0.95f;

    [ExportSubgroup("Rain")]
    // Blended rainAmount (0..1) at or above which ESpawnConditions.Clear entries
    // refuse to spawn. Sampled from the live player-blended weather; since spawn
    // gating only runs at chunk activation (which streams around the player),
    // this is the right gameplay read. The lighter sibling of
    // HeavyRainSpawnThreshold — Clear suppresses on any meaningful rain, while
    // NotHeavyRain only suppresses in a real downpour. See World.SpawnConditionsMet.
    [Export(PropertyHint.Range, "0,1,0.01")] public float RainSpawnThreshold = 0.2f;
    // Blended rainAmount (0..1) at or above which weather counts as "heavy
    // rain" for spawn gating: mobs/chests flagged ESpawnConditions.NotHeavyRain
    // refuse to spawn once rain reaches this. Distinct from the lighter Clear
    // gate (any meaningful rain) — heavy rain only suppresses spawns in a real
    // downpour. See World.SpawnConditionsMet.
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeavyRainSpawnThreshold = 0.6f;

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

    [ExportGroup("Block Light")]
    // ACTIVE model: a geodesic flood (LightEngine.ComputeFloodField/ShadeFloodField).
    // Each block light (torches, campfires, the carried player torch) floods
    // outward through open voxels out to a radius derived from its Distance,
    // weights each reached voxel by exp(-(euclideanDist/λ)^Falloff) (λ derived
    // from Distance+Falloff), then scales the field so the core peaks at its
    // Brightness. The flood gives occlusion + corner-wrap for free (light only
    // travels through open voxels). Cost is a single O(reached voxels) pass.
    //
    // Distance / Falloff / Brightness are PER-LIGHT — authored on each MovingLight
    // / StationaryLight. The knobs below are world-wide: the flood-radius cap, the
    // AO strength, and the fog/canopy medium extinction.

    // Hard cap on any light's flood radius (and thus the worst-case working
    // buffer, (2·MaxDistance+1)³). Each light DERIVES its own radius from its
    // Distance/Falloff (LightEngine.ResolveTuning) so a compact light floods a
    // small ball; this only clamps the far-reaching ones. Raise it if a
    // deliberately huge light gets truncated; lower it to bound worst-case cost.
    [Export(PropertyHint.Range, "1,32,1")] public int BlockLightMaxDistance = 14;

    // CORNER AO — strength of the ambient-occlusion darkening on voxels with few
    // open neighbours (corners, crevices, against walls/ground). 0 = off; 1 = a
    // fully-enclosed-ish voxel goes dark. A free concavity hint, applied on top
    // of the lighting (absolute, like fog — it doesn't redistribute energy).
    [Export(PropertyHint.Range, "0,1,0.01")] public float BlockLightAO = 0.5f;

    // FLICKER CULL DISTANCE (voxels). A flickering light beyond this from the
    // player stops re-rolling and holds a steady full brightness — each flicker
    // tick re-deposits a footprint and re-dirties its chunks, so this caps that
    // churn to the handful of lights near the player (where flicker is actually
    // visible). The player's own torch is always at distance ~0, so it never
    // culls. Large enough to cover the visible play area.
    [Export(PropertyHint.Range, "4,128,1")] public float BlockLightFlickerCullDistance = 28f;

    // MOVING-LIGHT RESHADE CULL DISTANCE (voxels). A moving light (carried torch)
    // re-shades and re-deposits its footprint every frame for smooth sub-voxel
    // motion — each reshade re-dirties its chunks and forces a LightMap upload.
    // Beyond this distance from the player the per-frame reshade is skipped: the
    // light still snaps to a fresh field on each voxel crossing (so it follows
    // its carrier), it just stops paying the every-frame sub-voxel update where
    // the smoothing isn't visible. Larger than the flicker cull because a torch
    // lagging its carrier reads further out than a missing flicker pulse.
    [Export(PropertyHint.Range, "4,160,1")] public float BlockLightMovingReshadeCullDistance = 40f;

    // Medium extinction (Beer-Lambert optical depth) added to the flood as it
    // passes through fog / foliage canopy. Each fully-dense voxel the light
    // crosses adds this much optical depth to the running total, and brightness
    // is multiplied by exp(-opticalDepth) on top of the geometric exp(-d/λ)
    // falloff. So a torch dims faster the more foggy / canopied air its light
    // threads through — independent of the geometric radius. 0 = the medium is
    // transparent to block light. Scales linearly with per-voxel density.
    [Export(PropertyHint.Range, "0,2,0.01")] public float BlockLightFogExtinction = 0.15f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float BlockLightCanopyExtinction = 0.15f;

    [ExportGroup("Foliage Canopy Shadow")]
    // FoliageStamper rasterizes every CastsSunShadow cluster's ellipsoid
    // plus a downward shadow column into WorldState.CanopyAttenuation;
    // LightEngine reads that field as extra sun + block-light falloff during
    // propagation. The four knobs below shape how much a tree shelters.

    // Per-cluster density (0..1) deposited into the canopy field. Stored as
    // a byte 0..255 and saturating-added across overlapping clusters, so a
    // lone tree contributes this fraction while two stacked clusters land
    // near saturation. 0.4 puts a single tree at "dappled" coverage (~half
    // sun under the shadow column) and a 2+ cluster overlap at the dense-
    // forest "proper shelter" reading.
    [Export(PropertyHint.Range, "0,1,0.01")] public float CanopyDensity = 0.4f;

    // Minimum voxels of constant-density shadow stamped directly below
    // each cluster's ellipsoid. The actual column extends down to whichever
    // is LOWER: this fixed depth, or one voxel below the prop's base — the
    // base anchor keeps shadow columns reaching the ground under tall-trunk
    // trees (birch at ~10m foliage) without authoring per-species depths.
    // Without the column at all, lateral BFS spread from un-canopied
    // neighbor columns refills the player's voxel with near-full sun.
    [Export(PropertyHint.Range, "0,32,1")] public int CanopyShadowDepthVoxels = 6;

    // Sun-channel falloff per propagation step when a voxel's canopy
    // density is saturated (255). With LightEngine.MAX_LIGHT=60, 18 means a
    // saturated voxel removes ~30% of max sun per step; combined with the
    // built-in 4-per-step distance falloff, that's enough to drop the
    // under-tree reading below the rain shader's 0.7-of-MAX_LIGHT threshold
    // and trigger rain shelter. Scales linearly with density at the voxel.
    [Export(PropertyHint.Range, "0,60,1")] public int CanopySunFalloffPeak = 18;

    // (Block light's canopy attenuation is the per-light flood term
    // BlockLightCanopyExtinction in the Block Light group, not here.)

    [ExportGroup("Spawn")]
    // Minimum distance (m) from the player at which a time-of-day refresh may
    // materialize a gated mob. When tod crosses sunset, RefreshTimeOfDayEntities
    // spawns night-only entities on chunks that are ALREADY active — including
    // the chunks right under the player's feet — so without this a goblin can
    // pop in a couple meters away the instant night falls. The normal chunk-load
    // spawn path streams entities in at the edge of the entity-load radius
    // (>=48m), so it's never this close; this gate only applies to the sunset
    // refresh. A mob skipped for being too close stays in its persistent sim
    // state and spawns later — when the player walks off and its chunk evicts +
    // reloads, or at the next nightfall once the player has moved away. See
    // World.RefreshTimeOfDayEntities.
    [Export(PropertyHint.Range, "0,100,1")] public float SpawnMinDistanceFromPlayer = 24f;

    [ExportGroup("Spawn Cleanup")]
    // Mirror of the spawn gate: a loaded mob whose ESpawnConditions no longer
    // hold (a night goblin caught at dawn, a clear-day sparrow once it starts
    // raining) is despawned back to its persistent sim state — but only once
    // it's far enough away, the player has lost track of it, and it isn't
    // hunting the player. Cleared mobs respawn naturally when their conditions
    // come back and their chunk is active. See World.CleanupOffConditionMobs.
    //
    // Distance (m) from the player beyond which an off-condition mob becomes
    // eligible for cleanup. Must comfortably exceed view distance so the
    // despawn never pops on-screen — the "player has lost track" gate already
    // means it's invisible, this is belt-and-suspenders against edge cases.
    [Export(PropertyHint.Range, "10,200,1")] public float SpawnCleanupDistance = 50f;
    // Seconds between cleanup sweeps. The sweep walks every loaded mob, so it
    // runs on an interval rather than per-frame; a couple of seconds is plenty
    // since spawn conditions (time of day, weather) change slowly.
    [Export(PropertyHint.Range, "0.5,30,0.5")] public float SpawnCleanupIntervalSeconds = 2f;

    [ExportGroup("Companion")]
    // The persistent companion follows the player but can fall outside the
    // loaded world if the player outruns it (no resident collision under it).
    // World's per-frame leash (World.TickCompanionLeash) then snaps it onto one
    // of the player's recent footsteps instead of letting it fall through. These
    // two knobs size that breadcrumb trail: a sample is recorded every
    // CompanionRescueSampleSeconds and the last CompanionRescueHistoryCount
    // samples are kept. The OLDEST still-loaded sample is chosen as the
    // relocation target — furthest behind the player, so the pet pops back in
    // off-screen. The trail must reach far enough back in world space to still
    // land inside the loaded entity radius: count × sample-seconds × player
    // speed should stay under that radius (~ENTITY_LOAD_RADIUS chunks).
    [Export(PropertyHint.Range, "0.1,5,0.1")] public float CompanionRescueSampleSeconds = 1f;
    [Export(PropertyHint.Range, "1,64,1")] public int CompanionRescueHistoryCount = 16;

    // Distance backstop for the catch-up rescue. A FOLLOWING companion (not one
    // commanded to stay) that stays farther than CompanionRescueMaxDistance from
    // the player for CompanionRescueMaxDistanceGraceSeconds is snapped onto the
    // breadcrumb trail even while still on a resident chunk — so a dog that fell
    // behind or wedged on geometry catches up at this gap instead of trailing
    // off to the edge of the loaded world before the residency rescue fires.
    // Keep above the follow behavior's catchUpRadius so the dog gets a chance to
    // run the gap closed before teleporting.
    [Export(PropertyHint.Range, "5,100,1")] public float CompanionRescueMaxDistance = 30f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float CompanionRescueMaxDistanceGraceSeconds = 1.5f;

    [ExportGroup("Footprints")]
    // Template material for the batched footprint MultiMesh. FootprintScatter
    // duplicates it once per actor footprint texture (binding that texture's
    // albedo) and drives the per-print tint + animated alpha through
    // INSTANCE_COLOR — so the template must be unshaded, alpha-blended, and
    // have vertex_color_use_as_albedo enabled. See footprint_multimesh.tres.
    [Export] public Material FootprintMaterial;

    // ===== Mob-print discovery gate =====
    // Prints a mob lays while the player hasn't yet noticed it stay invisible
    // until the player perceives the print itself (then they fade in). Player
    // prints — and mob prints laid while the mob was already perceived — skip
    // this and show immediately. These mirror the tunings that used to live on
    // the Discoverable child of the old footprint_discoverable scene.

    // Free scalar on the player's vision range for noticing a print. < 1 makes
    // prints subtler than a live target (a faint mark in the grass).
    [Export] public float FootprintDiscoveryProminence = 0.3f;
    // Perception value at which a print flips to visible. ~1 (perception must
    // saturate); lower pops prints in sooner.
    [Export(PropertyHint.Range, "0,1,0.01")] public float FootprintDiscoveryThreshold = 1f;
    // Height above the print to sample world light at when gauging whether the
    // player could notice it (just above the floor voxel).
    [Export] public float FootprintDiscoveryLightSampleHeight = 0.05f;
    // Seconds for the noticed-fade-in to traverse 0..1 once a print is
    // discovered.
    [Export(PropertyHint.Range, "0.05,2,0.01")] public float FootprintDiscoveryFadeSeconds = 0.4f;
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
        { EGroundType.Grass, new Color(0.18f, 0.14f, 0.08f, 0.90f) },
        { EGroundType.Sand,  new Color(0.22f, 0.18f, 0.12f, 1.0f) },
        { EGroundType.Mud,   new Color(0.10f, 0.07f, 0.04f, 1.0f) },
        { EGroundType.Dirt,  new Color(0.18f, 0.14f, 0.08f, 1.0f) },
        { EGroundType.Stone, new Color(0.15f, 0.15f, 0.15f, 0.36f) },
    };
    // Global fade lifetime — seconds for a fresh print to dim from its
    // baseline alpha to zero (then despawn). One global value rather than
    // per-ground because surface-specific persistence is already encoded
    // in the per-ground baseline alpha; a faint print can't visually
    // outlast a deep one anyway.
    [Export] public float FootprintDurationSeconds = 15f;

    [ExportGroup("Audio")]
    // Distant rolling-thunder scheduler asset + tuning. Sim-wide, since
    // the rolling-thunder bed sounds the same across zones (the per-zone
    // story is whether the zone gets lightning at all, controlled by
    // WeatherData.lightningAmount). Null = no thunder audio; the
    // ThunderScheduler node is still spawned but dormant.
    [Export] public ThunderSchedulerData thunderScheduler;

    [ExportGroup("Weather Lightning")]
    // Damaging lightning strikes spawned around the player by
    // WeatherLightningSpawner. Same intensity signal as the thunder
    // bed but a separate, much-rarer cadence. Null = no weather
    // strikes; the spawner stays dormant. Distinct from the
    // ThunderScheduler audio above (distant rumble atmosphere) — this
    // is the gameplay hazard.
    [Export] public LightningData weatherLightning;

    [ExportGroup("Perception Environment")]
    // World-wide weather modifiers on the perception senses, applied
    // identically to both perception paths (player→mob and mob→player) by
    // PlayerPerception's environmental helpers — wind, fog, and rain are
    // physics that shape a sense the same way regardless of who is
    // perceiving. All sampled at the perceiver's position (vision samples
    // fog at the target it mirrors the light-at-target convention).

    // Wind speed (m/s) at which the wind-driven perception effects (hearing
    // suppression, smell disruption, smell directionality) reach full
    // strength. Below this they scale linearly from 0 at dead calm. Tuned to
    // the same "strong but not extreme" band as the weather-advection and
    // water-ripple references.
    [Export(PropertyHint.Range, "1,30,0.1")] public float PerceptionWindReference = 12f;

    // Fraction of hearing (audible) range removed at PerceptionWindReference.
    // Turbulent air scatters sound, so a gale partially masks footsteps for
    // both the player and listening mobs. 0.5 = halved audible radius in a
    // strong wind.
    [Export(PropertyHint.Range, "0,1,0.01")] public float HearingWindSuppression = 0.5f;

    // Added fraction of hearing range at full fog. Still, damp foggy air
    // carries sound farther, so fog is a (small) boon to hearing — and a
    // counterweight to the vision loss fog also imposes. 0.3 = +30% audible
    // radius in thick fog.
    [Export(PropertyHint.Range, "0,2,0.01")] public float FogHearingBoost = 0.3f;

    // Fraction of vision range removed at full fog. Fog scatters light and is
    // the dominant weather reducer of sight. 0.6 = vision cut to 40% of its
    // clear-air reach in the thickest fog.
    [Export(PropertyHint.Range, "0,1,0.01")] public float FogVisionReduction = 0.6f;

    // Fraction of vision range removed at full rain. Rain is a slight extra
    // haze on top of any fog it brings — kept small so a downpour alone
    // doesn't blind anyone. 0.15 = -15% sight in heavy rain.
    [Export(PropertyHint.Range, "0,1,0.01")] public float RainVisionReduction = 0.15f;

    // Added fraction of smell range at full fog. Humid foggy air holds scent,
    // widening the radius a mob can pick up the player's trail. 0.5 = +50%
    // smell reach in thick fog.
    [Export(PropertyHint.Range, "0,2,0.01")] public float FogSmellBoost = 0.5f;

    // Smell potential multiplier ADDED when a scent source is fully downwind
    // of the smeller (wind blows from the source toward the nose). Scaled by
    // wind strength, so calm air carries no directional bias. 1.0 = a strong
    // downwind doubles the perceived scent.
    [Export(PropertyHint.Range, "0,3,0.01")] public float SmellDownwindBoost = 1.0f;

    // Fraction of smell potential removed when a source is fully upwind
    // (wind blows the scent away from the nose). Scaled by wind strength.
    // 0.7 = a strong upwind drops the scent to 30% of its still-air value.
    [Export(PropertyHint.Range, "0,1,0.01")] public float SmellUpwindReduction = 0.7f;

    // Fraction of smell range removed at PerceptionWindReference, regardless
    // of direction. High wind scatters and dilutes scent overall — a
    // counterweight to the downwind boost so a gale isn't a pure smelling
    // advantage. 0.4 = -40% smell reach in a strong wind.
    [Export(PropertyHint.Range, "0,1,0.01")] public float SmellWindDisruption = 0.4f;
}
