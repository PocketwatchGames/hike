using Godot;
using Godot.Collections;

// Static, authored world-level simulation constants. Mutable runtime state
// (TimeOfDay01, WindDirection, ShadowLightDirection, etc.) lives on WorldState
// — `Data` is never used for mutable values (see CLAUDE.md conventions).
[GlobalClass]
public partial class SimData : Resource
{
    [Export] public float gravity = 9.8f;

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
    [Export] public float playerStuckTimeoutSeconds = 1.5f;
    [Export] public float playerStuckVelocityThreshold = 0.1f;

    // Master recipe library. CookingScreen iterates this list to match the
    // current cooking inputs against an authored recipe. Discovery for any
    // hit is recorded in SimState.DiscoveredRecipes keyed by the same
    // RecipeData reference. Adding a recipe = adding it here.
    [Export] public Array<RecipeData> recipes = new();

    // Master alchemy-spell library. The alchemy campfire screen iterates this to
    // list the spells the player can attune, filtered to those currently known
    // (SimState.IsSpellKnown → Knowledge.KnownSpells). Each SpellData owns its
    // reagent cost; which spells start known is authored on
    // WorldGenData.initialKnowledge (SpellTeachable). Adding a spell = adding it here.
    [Export] public Array<SpellData> spells = new();

    // Master mob-type library — one entry per bestiary PAGE. BestiaryScreen
    // iterates this list for stable page ordering, then groups the player's
    // discovered species (SimState.DiscoveredSpecies) under the matching
    // page by SpeciesData.mob. Authored order here controls page order rather
    // than discovery order. Adding a new mob type = adding its base MobData here
    // so its species can appear in the bestiary once spotted.
    [Export] public Array<MobData> mobs = new();

    // Central registry of named scripting variables (quest flags, world
    // state). Seeded into SimState.ScriptVars at world creation so
    // ScriptVarCondition / ScriptVarTransition / SetScriptVarAction can branch
    // conversations and behaviors by name. Null = no variables in this world.
    [Export] public ScriptVariableRegistry scriptVariables;

    // Status effect applied to every elite mob at spawn, in addition to the
    // signature effect(s) the elite's own descriptor authors (MobDescriptor
    // .statusEffects). Authored once here so the shared elite buff — larger
    // health, etc. — is consistent across all elites rather than copy-pasted into
    // every *_elite.tres. Null = no shared effect.
    [Export] public StatusEffectData eliteStatusEffect;

    // Spinning, bobbing emissive halo floated over every elite mob (Mob.IsElite)
    // as an at-a-glance "this one's tougher" marker. Authored once here — the
    // crown is species-agnostic — so Mob can spawn it for any elite without
    // per-species .tscn wiring. Null disables the marker (elites still scale +
    // carry their effects). See EliteCrown / crown_lit.tres.
    [Export] public PackedScene eliteCrownScene;

    // Loot ejected by every elite mob on death, on top of its species loot.
    // Authored once here — the drop is species-agnostic, the trophy form of the
    // EliteCrownScene halo — so any elite drops it without per-species wiring.
    // Null disables the trophy drop. See Mob.EjectLoot.
    [Export] public LootData eliteLoot;

    // Daily "well rested" buff. Each sunrise one idle party member is drawn (see
    // Party.AdvanceRestAndPickWellRested) and wears this until the next — a small
    // all-round boost (stamina / damage / fortitude / health). Authored once here
    // since it's shared party-wide, mirroring eliteStatusEffect. Null = no buff.
    [Export] public StatusEffectData wellRestedEffect;

    // Warm campfire glow particle shown on the well-rested member ONLY while they
    // sit at the fire (Player.UpdateWellRestedFx gates it) — a looping Fx scene.
    // Kept off the status effect's own loopFx, which would show it all day. Null =
    // no particle.
    [Export] public PackedScene wellRestedCampfireFx;

    // Status effect a mob wears while wet (swimming / caught in the rain).
    // Shared — every mob uses the same status_wet the player does, so its
    // Electrical-vulnerability / Fire-resistance modifiers stay consistent
    // across mobs and player. Null = mobs never get wet. Unlike the player's
    // ContinuousArm buildup meter, mob wetness is a hard on/off toggle keyed to
    // the mob's current circumstance — see Mob.UpdateMobWet.
    [Export] public StatusEffectData mobWetStatusEffect;
    // Open-sky fraction (WorldState.GetSkyExposure01, 0 = fully covered, 1 = open
    // sky) at or above which falling rain counts as reaching a mob. Below it a
    // roof / dense canopy / cave ceiling shelters the mob and it stays dry.
    [Export(PropertyHint.Range, "0,1,0.01")] public float mobWetRainSkyThreshold = 0.5f;
    // Seconds for a mob out of water / rain to drain its Wet meter 1 → 0. Mobs
    // snap to fully wet instantly (no gradual soak like the player), then dry over
    // this window. It doubles as the anti-flicker grace: the effect's disarm
    // hysteresis keeps a mob wet until the meter drains past disarmThreshold, so a
    // mob straddling a water edge (re-snapped to full each in-water tick) never
    // blinks wet/dry. Short so mob wetness doesn't linger.
    [Export] public float mobWetDrySeconds = 1.5f;
    // Fire status a sun-vulnerable mob (MobData.sunburnBuildupPerSecond > 0, i.e.
    // gellies) accrues while standing in direct sunlight — shared so every darkness
    // creature ignites with the same fire DoT + flame FX a flaming weapon applies.
    // Null = sunlight never ignites anything. See Mob.TickSunburn.
    [Export] public StatusEffectData mobSunburnStatusEffect;

    // Anti-cheese for safety zones: while the player stands in any safety zone
    // (Player.IsSafe), every wounded hostile regenerates this fraction of its
    // max health per second toward full, so the player can't pop in and out of
    // a zone to whittle a tough enemy down. 0 disables. See Mob.TickSafeZoneHeal.
    [Export(PropertyHint.Range, "0,1,0.01")] public float safeZoneEnemyHealFractionPerSecond = 0.1f;

    // Shape knob shared by every hazard profile (DamageData.hazardProfile): the
    // fraction of a receiver's max health at which a hazard's `strength` puts it
    // exactly halfway between its floor and ceiling bands. Lower = hazards stay near
    // their ceiling against tougher targets. One value world-wide, so retuning it
    // reshapes how every trap responds to toughness at once.
    // See HazardProfileData.Outmatch.
    [Export(PropertyHint.Range, "0.05,3,0.01")] public float hazardDamageHalfPointPercent = 0.6f;

    // Fairy-loot boons. A fairy corpse (FairyLoot) draws its candidate boons
    // from FairyBoons, composed onto the corpse's per-instance ItemState when it
    // spawns (Sim.SpawnLoot) so one can be applied on use and chosen by the
    // player. Centralized here — rather than on the loot entry in the fairy's
    // .tres — so the boon pool is tuned in one place, mirroring the Elite
    // loot/effect pairing above. Empty list (or null FairyLoot) = the corpse
    // bestows nothing.
    // Apply-on-pickup loot (ConsumableData) — the fairy's boon-pick runs on world
    // pickup via Loot.OffersBoons (possibleBoons composed in Sim.ComposeFairyBoons),
    // so this only needs to be an ItemData, not a consumable.
    [Export] public ItemData fairyLoot;
    [Export] public Array<BoonData> fairyBoons = new();

    // Filler boon for the fairy upgrade screen. The screen only offers boons
    // from FairyBoons that are currently VIABLE (a restorative boon is hidden at
    // full health; a lasting buff is hidden when already active), so the choice
    // count can fall below the three cards the screen wants. When it does, this
    // boon pads the list out. Authored as the Gold boon — a never-wasted
    // consolation — and deliberately NOT in FairyBoons so it's only ever offered
    // as a filler, never a random roll. Null = no padding (the screen just shows
    // however many viable boons remain).
    [Export] public BoonData fairyBoonGold;

    // Number of boons a fairy corpse offers on the upgrade screen. The corpse
    // rolls a random subset of FairyBoons this size when it spawns
    // (Sim.ComposeFairyBoons) — a fixed offering, so reopening the pick screen
    // shows the same choices — and the gold filler pads back up to this count
    // when too few of the rolled boons are viable at pick time.
    [Export] public int fairyBoonChoiceCount = 3;

    // Upgrade pool a Forge draws its single offered upgrade from. Each entry is a
    // slot-locked StatusEffectData (upgradeSlot != None) applied at the forge's
    // level and expiring at the next sunrise (author with durationType TimeOfDay).
    // A given forge deterministically offers one of these per day. Centralized here
    // so the pool is tuned in one place, mirroring the fairy-boon pool above.
    // Empty = the forge offers nothing.
    [Export] public Array<StatusEffectData> forgeUpgrades = new();

    // Map / minimap marker icons for a forge, chosen by the slot it currently
    // offers (sword / bow / shield). The forge marker draws one of these instead of
    // a generic forge icon so the player can read the offered slot from the map.
    // Null leaves the marker's default icon.
    [Export] public Texture2D forgeMeleeIcon;
    [Export] public Texture2D forgeRangedIcon;
    [Export] public Texture2D forgeArmorIcon;

    // The map icon for a forge with the given (concrete, single) upgrade slot.
    public Texture2D GetForgeSlotIcon(EUpgradeSlot slot)
    {
        return slot switch
        {
            EUpgradeSlot.Melee => forgeMeleeIcon,
            EUpgradeSlot.Ranged => forgeRangedIcon,
            EUpgradeSlot.Armor => forgeArmorIcon,
            _ => null,
        };
    }

    [ExportGroup("Level Scaling")]
    // Single per-level power curve driving BOTH a mob's difficulty Level and the
    // player's forge-upgrade level (StatusEffectState.level, stamped from the forge
    // tier). Offense and defense are SYMMETRIC by construction — offense multiplies
    // by levelScalePerLevel^level, defense multiplies by its reciprocal
    // levelScalePerLevel^-level — so a level-N attacker and a level-N defender
    // exactly cancel (net 1x). Separate from the weapon-item scaling (WeaponState
    // .DamageMultiplier = 2^level): at the default 1.5, each level multiplies a
    // leveled attacker's outgoing damage/buildup by 1.5 and divides what a leveled
    // defender takes by 1.5.
    // For the player these are slot-specific — the Melee/Ranged upgrade drives
    // offense on that weapon, the Armor upgrade drives defense; a mob applies both
    // from its single Level. See ItemEventHandlers.ResolveHit / Projectile (offense)
    // and Player/Mob.ApplyResistance (defense).
    //
    // This is the SINGLE per-star knob: it also drives the mob health/armor pool
    // (LevelPoolMultiplier / Mob.LevelMultiplier), so one value tunes the whole
    // difficulty curve. At 1.5 each star multiplies both a mob's pool AND its
    // outgoing damage by 1.5 — so an under-geared fight's composite lethality
    // (durability × their damage) rises ~2.25x per star rather than quadrupling.
    [Export(PropertyHint.Range, "1,4,0.01")] public float levelScalePerLevel = 1.5f;

    // Outgoing damage / buildup multiplier for a leveled attacker (>=1). Level 0
    // (unleveled / no upgrade in the slot) is a neutral 1.
    public float LevelOutgoingScale(int level) => level <= 0 ? 1f : Mathf.Pow(levelScalePerLevel, level);

    // Incoming damage / buildup multiplier for a leveled defender — the exact
    // reciprocal of LevelOutgoingScale, so equal levels cancel. Level 0 is a
    // neutral 1. NOTE: mobs no longer apply this (their level defense is the pool
    // alone — see Mob.IncomingLevelResist); it remains the player's Armor-upgrade
    // resist, whose reciprocal cancels a same-level attacker.
    public float LevelIncomingResist(int level) => level <= 0 ? 1f : Mathf.Pow(levelScalePerLevel, -level);

    // Health/armor POOL multiplier for a leveled mob (>=1) — same curve as the
    // outgoing scale, so both share the one levelScalePerLevel knob. Level 0 is a
    // neutral 1. Drives Mob.LevelMultiplier (runtime cap reads) and the spawn-time
    // vital bake (MobSimState constructor), which must agree.
    public float LevelPoolMultiplier(int level) => level <= 0 ? 1f : Mathf.Pow(levelScalePerLevel, level);

    // Shared interactive verbs auto-injected on any mob whose runtime
    // SimState carries a Conversation. Authored here once so adding a new
    // talking NPC species doesn't require copy-pasting Talk / GiveItem
    // sub-resources into each mob's .tscn — give the mob a Conversation
    // (via WorldGen or SimState) and these verbs surface automatically.
    // TradeAction replaces GiveItemAction on mobs whose MobSimState.WillTrade
    // is true; the two are mutually exclusive on any given mob.
    [Export] public InteractiveAction talkAction;
    [Export] public InteractiveAction giveItemAction;
    [Export] public InteractiveAction tradeAction;

    // Shared interactive verb surfaced on a fallen party member's body (see
    // Player corpse interactive). Reviving a party member — walk up and
    // interact; the completion event's fx plays and the member respawns at the
    // campfire. Null disables party revival.
    [Export] public InteractiveAction partyReviveAction;

    // Grammar's contribution to TextScrambler.ComputeComprehension. Final
    // understanding = translatedPct × ((1 - this) + orderPct × this), so:
    //   0   = grammar irrelevant, only word translation counts.
    //   1   = grammar fully gates — words landing in the wrong order
    //         multiply translation down toward zero.
    //   0.2 = grammar contributes 20% of the final score; a player who
    //         knows every vocab bucket but no grammar still understands
    //         ~80% of any text, missing a soft tax for jumbled order.
    [Export(PropertyHint.Range, "0,1,0.01")] public float languageGrammarWeight = 0.2f;

    [Export] public float visibleTime = 0.25f;
    // World-wide threshold for "fully visible to perception". Light readings
    // at the target's sample point are clamped to [0, this] then divided
    // by it to produce a 0..1 light factor. One global value rather than
    // per-mob / per-discoverable so light contribution is consistent
    // across every percept; per-target tuning of how easily a thing is
    // spotted lives in `prominence` and the detected/discovered
    // thresholds instead.
    [Export] public float targetLightMax = 0.75f;

    [ExportGroup("Time of Day")]
    // Seconds of wall-clock time for a full day/night cycle at time_scale = 1.
    // The time_scale CVar multiplies this advancement for fast-forward testing.
    [Export] public float dayLengthSeconds = 600f;

    // Normalized time the day starts at: 0 = sunrise, 0.25 = noon, 0.5 = sunset,
    // 0.75 = midnight, 1 = the next sunrise. Applied when a fresh game is started;
    // a small value starts the player just after dawn. Sunrise/noon/sunset/midnight
    // positions themselves are fixed constants on WorldState (the clock's shape),
    // not authored here.
    [Export(PropertyHint.Range, "0,1,0.001")] public float initialTimeOfDay = 0.0375f;

    // Sun's elevation above the horizon at noon. 90 = sun passes through
    // zenith; lower values produce a shallower arc (higher-latitude look).
    // Drives both visual sky placement AND the simulation-side
    // ShadowLightDirection that gameplay raycasts query.
    [Export(PropertyHint.Range, "10,90,1")] public float sunMaxElevationDegrees = 60f;

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
    [Export(PropertyHint.Range, "0,360,1")] public float noonAzimuthDegrees = 45f;

    // The effective horizon — the elevation above geometric 0° at which
    // sources are considered "at sunset/moonrise". Models an occluding
    // horizon line (mountains, tree ring, distant cliffs) so the sun can
    // visually set before it drops below the actual geometric horizon,
    // and the moon can visibly rise into view some minutes before it
    // would astronomically appear. Every horizon fade in SkyController
    // (light energy, shafts, cloud shadows, color blend) is an OFFSET
    // from this angle, and the gameplay `CurrentAmbient` blend pivots
    // on it too.
    [Export(PropertyHint.Range, "0,45,0.5")] public float sunsetAngleDegrees = 15f;

    // Width (degrees) of the sunrise/sunset color fade-out band, added
    // on each side of SunsetAngleDegrees. The sunset color variants are
    // at full strength across |elev| <= SunsetAngleDegrees (symmetric
    // across horizon crossing, so pre-dawn and post-dawn both stay warm),
    // then fade out between SunsetAngleDegrees and SunsetAngleDegrees
    // + this. Also parameterizes the ambient blend that gameplay
    // stealth/perception consumes.
    [Export(PropertyHint.Range, "1,45,0.5")] public float sunsetColorRangeDegrees = 10f;

    // Half-width (degrees) of the band around the horizon crossing where the
    // sunset colour is at FULL strength, before SunsetColorRangeDegrees fades it
    // out. Deliberately independent of (and much smaller than)
    // SunsetAngleDegrees: the sunset weight is applied as the final blend and
    // overrides the day/night mix wherever it saturates, so a wide plateau pins
    // the sky at sunset colour long after dark. Keep it small enough that the
    // night blend has taken over by the time this releases.
    [Export(PropertyHint.Range, "0,20,0.5")] public float sunsetColorPlateauDegrees = 4f;

    // Direct-sun intensity falls off as the sun descends:
    //     elevFactor = lerp(SunHorizonIntensityFactor, 1, s^SunElevationFalloffExponent)
    // where s = sin(elev) / sin(SunMaxElevationDegrees), clamped to [0,1].
    // Without this the sun is EQUALLY bright at 8am, noon and sunset — the only
    // dimming is the day→night color crossfade, which is why low sun used to read
    // as bright and flat. The floor keeps the sun from vanishing before that
    // crossfade takes over; 0 = fully dark at the horizon.
    // NOTE: SunsetIntensityFactor stacks multiplicatively on top of this.
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunHorizonIntensityFactor = 0.25f;
    // <1 holds brightness high and drops it late (a long plateau then a fast
    // dusk); >1 dims early and lingers. 1 = straight sine falloff.
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float sunElevationFalloffExponent = 0.75f;

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
    [Export(PropertyHint.Range, "0.5,2,0.01")] public float dayHorizonBrightness = 1.2f;
    // How much the day horizon tilts toward the zone's SunColor.
    // 0 = pure SkyColor; 1 = full SunColor warm wash near the horizon.
    [Export(PropertyHint.Range, "0,1,0.01")] public float dayHorizonWarmBias = 0.3f;
    // How much humidity pulls the horizon toward a pale haze color
    // (blend of white and DustColor weighted by dustAmount). 0 = no
    // effect; 1 = fully hazed out at humidity=1.
    [Export(PropertyHint.Range, "0,1,0.01")] public float dayHorizonHumidityHaze = 0.4f;
    // Scale applied to SkyColor at the night zenith. 0.05 = deep
    // near-black; 0.3 = moonlit blue.
    // Master brightness of the whole sunset DOME — horizon band and zenith
    // together. The two bands have independent scales (SunsetHorizonBrightness
    // and SunsetZenithSkyScale) which shape their RATIO; this is the one knob for
    // "the sunset sky is too bright", so pulling it down can't leave the other
    // band glowing behind. Dome only: the sunset LIGHT on the world is untouched,
    // and day/night are unaffected because it rides the sunset phase weight.
    [Export(PropertyHint.Range, "0,2,0.01")] public float sunsetSkyBrightness = 0.5f;

    // Brightness of the sunset HORIZON band. Every other phase/band derives its
    // sky colour from SkyColor with an explicit scale (day 1.2, night 0.05,
    // sunset zenith 0.4); the sunset horizon alone was the raw sunsetPrimary —
    // the LIGHT colour used directly as a SKY colour, pegged at 1.0 in red and
    // unscalable. That is why sunset was the one phase that read too bright.
    // Scales only the dome, so it never touches how the sunset lights the world.
    [Export(PropertyHint.Range, "0,2,0.01")] public float sunsetHorizonBrightness = 0.55f;

    [Export(PropertyHint.Range, "0,1,0.01")] public float nightZenithSkyScale = 0.05f;
    // Scale applied to SkyColor at the night horizon (before MoonColor
    // bleed adds on top). Brighter than the zenith since the atmosphere
    // scatters even faint moonlight toward the horizon.
    [Export(PropertyHint.Range, "0,1,0.01")] public float nightHorizonSkyScale = 0.05f;
    // How much of the zone's MoonColor bleeds into the night horizon.
    // 0 = horizon is a pure dark sky; 0.3 = visible moonlit wash. Keep this
    // small: it is ADDITIVE brightness on the brightest part of the night sky,
    // and at 0.15 it was over half the night horizon's total value — the single
    // biggest reason the night sky read as glowing blue rather than black.
    [Export(PropertyHint.Range, "0,1,0.01")] public float nightHorizonMoonBleed = 0.04f;

    // Horizon→zenith blend exponent for the sky dome, per phase. Below 1 the
    // horizon band is only a few degrees tall; above 1 it climbs the dome.
    // Sunset wants it WIDE so the warm band is actually visible (and lands in
    // water reflections, which under the iso camera sample 35-50° up and
    // otherwise only ever see the cool zenith). Night wants it tight so the
    // brighter horizon doesn't wash the whole sky.
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float skyGradientExponentDay = 0.6f;
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float skyGradientExponentSunset = 2.0f;
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float skyGradientExponentNight = 0.5f;
    // Sunset zenith is a mid-dark sky with a violet twilight push.
    // This scales the underlying SkyColor before mixing in purple.
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunsetZenithSkyScale = 0.4f;
    // Target purple for the twilight sky overhead. Humidity controls
    // how hard the zenith pushes toward this color.
    [Export] public Color sunsetZenithPurple = new Color(0.35f, 0.15f, 0.45f);
    // How much humidity strengthens the twilight purple push. 0 = never;
    // 1 = fully replaces sky zenith at humidity=1.
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunsetZenithHumidityPurple = 0.4f;

    [ExportSubgroup("Sunset Warmth")]
    // Target warm color for the sunset horizon / primary blend. Lean
    // toward amber/red; dust amount pushes harder toward this.
    [Export] public Color sunsetAmberTarget = new Color(1.0f, 0.5f, 0.2f);
    // Base sunset warmth: how strongly SunColor shifts toward the
    // amber target even in zero-dust air. 0 = sunset IS SunColor;
    // 1 = sunset IS the amber target.
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunsetWarmthBias = 0.7f;
    // Additional dust-driven push toward DustColor on the sunset
    // horizon and primary. Explains why "red sky at night" tracks with
    // atmospheric dust.
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunsetDustBias = 0.35f;

    [ExportSubgroup("Fills")]
    // Fills oppose the primary light and sculpt surface slope. fillA
    // pulls toward SkyColor (cool); fillB pulls toward a lightened
    // SunColor (warm). This slider is the mix weight on fillA's
    // sky bias — higher = more sky-dominant cool fill.
    [Export(PropertyHint.Range, "0,1,0.01")] public float fillAFromSkyBias = 0.7f;
    // fillB mix toward white. 0 = pure SunColor; 1 = pure white.
    // Small values keep fillB as a gentle warm bounce rather than
    // a bright wash.
    [Export(PropertyHint.Range, "0,1,0.01")] public float fillBWhiteMix = 0.2f;
    // How much atmospheric haze (humidity + fog + dustAmount) pulls
    // fill colors toward DustColor. Higher = fills pick up regional
    // character in dusty/humid weather.
    [Export(PropertyHint.Range, "0,1,0.01")] public float fillDustPullK = 0.35f;
    // How much humidity desaturates fills (toward their luminance).
    // Describes how humid air washes out slope-shading color.
    [Export(PropertyHint.Range, "0,1,0.01")] public float fillDesatK = 0.35f;

    [ExportSubgroup("Clouds")]
    // cloudThreshold when cloudCover=0 (clear sky). Higher = fewer
    // patches of cloud actually make it past the noise threshold.
    // 0.95 reads as "almost no cloud at all".
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudThresholdClear = 0.95f;
    // cloudThreshold when cloudCover=1 (overcast). Lower = more of
    // the noise field exceeds threshold. Combined with the symmetric
    // band shift in WeatherDerivation, 0.0 here means cloudCover=1
    // gives true full coverage — most noise values produce solid
    // cloud, with only thin variation where noise is lowest.
    [Export(PropertyHint.Range, "-0.5,1,0.01")] public float cloudThresholdOvercast = 0.0f;
    // cloudSharpness when humidity=0 (dry air). Higher = crisper cloud
    // edges. Dry desert skies have very hard-edged cumulus.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudSharpnessDry = 0.85f;
    // cloudSharpness when humidity=1. Soft edges read as translucent,
    // tropical cloud character.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudSharpnessHumid = 0.3f;
    // Exponent shaping cloudCover → threshold interpolation. 1.0 is
    // linear; <1 shifts mid-cover values toward the overcast end so
    // cc=0.5 reads as genuine half-cloudy (~50% of sky solid) rather
    // than "partly cloudy" (~30% with linear interpolation). Tuned
    // against a typical FBM noise distribution.
    [Export(PropertyHint.Range, "0.3,2,0.01")] public float cloudCoverExponent = 0.7f;
    // Day cloud color = lerp(white, SunColor, this). Higher = clouds
    // take on more of the sun's tint; lower = whiter clouds.
    [Export(PropertyHint.Range, "0,1,0.01")] public float dayCloudSunMix = 0.3f;
    // Sunset cloud color pulled toward DustColor by this amount. High
    // dust zones get dramatic warm-underbelly clouds at sunset.
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunsetCloudDustMix = 0.4f;
    // Night cloud color = lerp(dark gray, MoonColor, this). Keeps
    // night clouds visible against a dark sky without going full moon
    // tint.
    [Export(PropertyHint.Range, "0,1,0.01")] public float nightCloudMoonMix = 0.7f;

    [ExportSubgroup("Fog")]
    // Fog is fully derived: WeatherDerivation computes a [0, 1] fog
    // signal from simulated humidity and the cool-half-of-day diurnal
    // (FogFromHumidity / RadiationFogSharpness weights live in the
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
    [Export(PropertyHint.Range, "0,1,0.001")] public float fogDensityK = 0.1f;
    // Ambient (non-map) distance haze from the derived fog signal.
    // The shape is `pow(fog, FogCurveExponent) * K`: a concave curve
    // (exponent < 1) lets low fog values still read as visible haze
    // while damping high values so a fully humid pre-dawn fog doesn't
    // over-saturate into pea soup.
    [Export(PropertyHint.Range, "0,0.05,0.0005")] public float ambientFogK = 0.0025f;
    // Exponent shaping the fog → haze curve. 1.0 = linear; 0.5 = sqrt
    // (current default; low fog hits ~40% of max haze). Lower values
    // push the curve further toward "even a little fog is visible,
    // max fog is not much denser."
    [Export(PropertyHint.Range, "0.1,2,0.01")] public float fogCurveExponent = 0.5f;
    // Fog density scales with current direct-light intensity (palette
    // PrimaryIntensity) via a smoothstep: fog is visible proportional
    // to the light scattering through it, so dim-primary scenes (full
    // night, heavy storm) should read with dimmer fog regardless of
    // authored fog value. Below this threshold, fog falls toward the
    // floor; above it, fog is at full density.
    [Export(PropertyHint.Range, "0,1,0.01")] public float fogIntensityReference = 0.35f;
    // Minimum fog density multiplier when direct light is near zero.
    // 0 would kill fog entirely at night (too abrupt); 0.2 keeps a
    // visible trace so heavy-fog zones still read as foggy under
    // moonlight, just much dimmer than day.
    [Export(PropertyHint.Range, "0,1,0.01")] public float fogIntensityFloor = 0.2f;
    // Additional ambient haze from humidity. Zero by default — humid-
    // but-clear zones shouldn't look foggy. Re-enable if you want
    // tropical zones to feel hazier than their authored fog alone
    // would produce.
    [Export(PropertyHint.Range, "0,0.05,0.0005")] public float ambientFogHumidityK = 0f;

    [ExportSubgroup("Shafts")]
    // Shaft COLOUR only — how much the regional DustColor tints the shaft
    // away from pure SunColor / MoonColor (zone-appearance, so it lives with
    // the palette derivation). Shaft INTENSITY and its weather response are
    // client-side visual tuning on SkyController (shaftWash* / moonBeamScale),
    // NOT here.
    [Export(PropertyHint.Range, "0,1,0.01")] public float shaftDustColorMix = 0.3f;

    [ExportSubgroup("Direct Light Intensity")]
    // Floor for daytime intensity at full overcast. 1.0 = never dim;
    // ~0.4 = strongly dim. Applied via a smoothstep knee so partly-
    // cloudy days stay bright and only genuinely overcast skies duck.
    [Export(PropertyHint.Range, "0,1,0.01")] public float overcastDim = 0.4f;
    // BASELINE cloudCover at which the overcast dim knee starts (at
    // humidity=0.5). HumidityKneeShift slides both start and end left
    // or right per frame based on the current humidity — low-humidity
    // cloud is thin with gaps (knee shifts right → stays bright longer),
    // high-humidity cloud is thick stratus (knee shifts left → dims
    // sooner). The SAME knee drives AmbientCloudLift so ambient and
    // direct invert in lockstep; if they didn't match, cloudCover in
    // the gap would add ambient without losing direct, brightening
    // the scene instead of dimming it.
    [Export(PropertyHint.Range, "0,1,0.01")] public float overcastKneeStart = 0.5f;
    // Baseline cloudCover at which the overcast dim knee reaches
    // OvercastDim. Also shifts with HumidityKneeShift.
    [Export(PropertyHint.Range, "0,1,0.01")] public float overcastKneeEnd = 1.0f;
    // How far humidity slides the knee. At humidity=0 the knee shifts
    // RIGHT by this amount (a thin dry overcast barely dims — sun
    // still punches through gaps); at humidity=1 it shifts LEFT by
    // this amount (a humid stratus layer starts dimming at low cover).
    // humidity=0.5 is neutral (no shift). Effective spread: a
    // cloudCover=0.7 swamp with humidity=0.95 dims much harder than a
    // cloudCover=0.7 dry mountain day.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float humidityKneeShift = 0.3f;
    // Scale applied at humidity=1 as an always-on damper on direct
    // light. Humid air scatters more, noticeably dimming direct sun
    // in a humid swamp or jungle even when the sky isn't fully
    // overcast. 0.8 = 20% drop at full humidity, paired with a small
    // ambient lift so the net scene is dimmer AND flatter.
    [Export(PropertyHint.Range, "0,1,0.01")] public float humidityDim = 0.8f;
    // Sunset intensity as a fraction of day intensity. Sunsets are
    // mellower than noon; 0.7 reads as "softened but still warm".
    [Export(PropertyHint.Range, "0,2,0.01")] public float sunsetIntensityFactor = 0.7f;
    // Absolute clear-noon sunlight intensity — the single sun knob.
    // Pre-multiplied into _palette.PrimaryIntensity by WeatherDerivation,
    // then weather-modulated by cloudIntensityScale × humidityIntensityScale ×
    // aridBoost at runtime. SkyController feeds the result into both
    // CurrentPrimaryIntensity (scene illumination, sun_intensity shader
    // global) and SunLight.LightEnergy.
    [Export(PropertyHint.Range, "0,4,0.01")] public float dayIntensityBase = 2f;
    // Absolute clear-night moonlight intensity — the single moon knob.
    // Modulated by cloudIntensityScale and becomes _palette.NightPrimaryIntensity,
    // which SkyController feeds into both CurrentPrimaryIntensity (scene
    // illumination) and MoonLight.LightEnergy (Godot's shadow pass).
    [Export(PropertyHint.Range, "0,2,0.01")] public float nightIntensityBase = 0.75f;
    // Maximum day-intensity amplification when air is BOTH dry AND
    // cloudless. Desert sun is physically more intense than normal
    // noon (the sky dome doesn't absorb / scatter it as much) — this
    // lets arid zones exceed 1.0 while humid/cloudy zones stay at
    // or below 1.0. Uses min(1-humidity, 1-cloudCover) as the trigger
    // so EITHER condition being wet/cloudy cancels the boost.
    [Export(PropertyHint.Range, "1,2,0.01")] public float aridBoostMax = 1.5f;

    [ExportSubgroup("Ambient Light")]
    // Day ambient floor in CLEAR weather. Ambient is physically INVERSE
    // to direct intensity: a sunny day has crisp shadows (high direct,
    // low ambient); an overcast day has flat lighting (low direct,
    // high ambient). AmbientCloudLift does the inversion; this is the
    // clear-sky floor that even cloudless zones get. 0.15 keeps
    // crisp desert/mountain shadows visible (~7:1 contrast against
    // arid-boosted direct) without crushing them to near-black.
    [Export(PropertyHint.Range, "0,1,0.01")] public float dayAmbientBase = 0.15f;
    // Additional day ambient at humidity=1. Small — humid air scatters
    // more, but most of the ambient rise comes from clouds.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float ambientHumidityLift = 0.1f;
    // Additional day ambient at cloudCover=1. Applied via the direct-
    // dim knee so partly-cloudy scenes stay crisp (ambient doesn't
    // rise until the sky actually closes up). For CLOUD-shadow
    // softness on the ground, use SkyController.cloudShadowStrength
    // instead — ambient is a scene-wide floor, cloud opacity is the
    // surgical tool for "clouds shouldn't crush shadows to black."
    [Export(PropertyHint.Range, "0,1,0.01")] public float ambientCloudLift = 0.47f;
    // Sunset ambient as a multiplier on day ambient. Slightly elevated
    // because low sun = more atmosphere scattering.
    [Export(PropertyHint.Range, "0,2,0.01")] public float sunsetAmbientFactor = 1.1f;
    // Night ambient floor. Moonlit shadows are inky, so this stays low
    // (well below DayAmbientBase) to preserve the "crisp moon shadow"
    // look — see WeatherData comment on moon ambient for context.
    [Export(PropertyHint.Range, "0,1,0.01")] public float nightAmbientBase = 0.08f;
    // Additional night ambient at humidity=1. Foggy night = gloomy but
    // more ambient fill.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float nightAmbientHumidityLift = 0.05f;

    [ExportSubgroup("Nightfall")]
    // Time-of-day the slide into full darkness begins. The moonlit night holds
    // its brightness up to here; from here to the end of the day (1 = where the
    // sun would rise) the sky fades out and then stays out. Sits after midnight
    // (0.75) by default so the dark stretch is the pre-dawn hours.
    [Export(PropertyHint.Range, "0,1,0.001")] public float nightfallStartTimeOfDay = 0.85f;
    // Skylight decays across the NightfallStartTimeOfDay→end-of-day window as
    //     skyLight = (1 - t)^NightfallFalloff
    // where t is 0 at the window's start and 1 at the end of the day. 1 = linear;
    // >1 dims fast then lingers dim; <1 holds the brightness and plunges at the
    // end. Everything the sky lights rides this curve — ambient,
    // sun/moon intensity, the dome, stars, the moon disk (and so its water
    // reflection), moon shafts, and the water-foam light floor.
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float nightfallFalloff = 0.5f;
    // Skylight remaining once the window has closed. 0 = utterly black, block
    // lights only — raise it if pitch dark reads as unplayable rather than tense.
    [Export(PropertyHint.Range, "0,1,0.01")] public float nightfallSkylightFloor = 0f;
    // Direct-light intensity at or above which the open air counts as FULLY
    // lit, for the palette's Illumination scalar (fog haze color, water-foam
    // light floor). Well under moonlight by default, so day / dusk / moonlit
    // night all read as fully lit and only the vanishing end ramps down. This
    // is what makes anything self-lit-looking go dark for ANY reason the light
    // dies — nightfall, or an eclipse — instead of on a clock.
    [Export(PropertyHint.Range, "0.01,2,0.01")] public float skyLightReference = 0.35f;

    [ExportSubgroup("Water")]
    // Reference wind speed (m/s) at which ripple_strength saturates to 1.
    // Curve is quadratic: (wind / ref)² — low wind barely perturbs the
    // surface so the sun disk can reflect coherently, high wind fully
    // breaks it up. Below ~2 m/s the surface is near-mirror.
    [Export(PropertyHint.Range, "2,30,0.1")] public float rippleWindRef = 10f;
    // Per-unit of rainAmount, additional ripple strength. Rain patters
    // on water even without wind.
    [Export(PropertyHint.Range, "0,1,0.01")] public float rippleRainK = 0.3f;

    [ExportSubgroup("Wind Rhythm")]
    // Base frequency of the sprite-sway sine wave. Consumed by the
    // sprite sway shader via wind_phase integration.
    [Export(PropertyHint.Range, "0,5,0.01")] public float windFreqBase = 1.0f;
    // Additional windFrequency at cloudCover=1. Stormy skies have
    // more agitated sway rhythms.
    [Export(PropertyHint.Range, "0,5,0.01")] public float windFreqCloud = 0.8f;
    // Base gust frequency (Hz). Slow-breathing gust wave.
    [Export(PropertyHint.Range, "0,1,0.01")] public float gustFreqBase = 0.1f;
    // Additional gust frequency at cloudCover=1. Storms gust more.
    [Export(PropertyHint.Range, "0,1,0.01")] public float gustFreqCloud = 0.2f;
    // Gust peak as a fraction of windSpeed, clear-sky floor. At
    // cloudCover=0, gusts add up to this × windSpeed on top.
    [Export(PropertyHint.Range, "0,1,0.01")] public float gustMinFraction = 0.3f;
    // Additional fraction at cloudCover=1. Stormy skies gust harder
    // — peak adds GustMinFraction + GustCloudFraction × windSpeed.
    [Export(PropertyHint.Range, "0,1,0.01")] public float gustCloudFraction = 0.5f;

    [ExportSubgroup("Dust Density")]
    // Shader dustDensity = dustAmount * this. Old authored values
    // ranged 0.003 (clear) to 0.1 (dusty); K=0.1 maps dustAmount 0..1
    // onto that full range linearly. Our authored desert has
    // dustAmount=0.5 → dustDensity=0.05 (half of old dusty max).
    [Export(PropertyHint.Range, "0,0.2,0.001")] public float dustDensityK = 0.1f;
    // Humid air carries its own light-scattering medium (haze droplets), so
    // a humid zone can show beams through partial cloud even with low
    // authored dust. Adds humidity * this to the effective dust amount
    // before DustDensityK. 0 = humidity contributes no scattering medium;
    // 0.5 lets a fully-humid zone scatter like ~0.5 dustAmount.
    [Export(PropertyHint.Range, "0,2,0.01")] public float dustFromHumidity = 0.5f;

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
    [Export(PropertyHint.Range, "0,1,0.001")] public float diurnalPeak01 = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float diurnalTrough01 = 0.0f;
    // Half-width of the day plateau (and, symmetrically, the night
    // plateau). 0.125 puts the plateau edges at sunrise+sunset/2 and
    // noon±0.125 / midnight±0.125. Width 0 collapses the trapezoid to
    // a triangle wave with peaks at the plateau centers; 0.25 makes
    // the day and night plateaus touch at sunrise / sunset, killing
    // the ramps entirely.
    [Export(PropertyHint.Range, "0,0.249,0.005")] public float diurnalPlateauHalfWidth = 0.125f;

    [ExportSubgroup("Weather Variance")]
    // Game-hours between weather-variance re-rolls. The simulation
    // holds a `prev` and `next` value; the active value smooth-lerps
    // from prev→next across the sunrise/sunset window, so frontal
    // changes only "land" at dawn/dusk rather than mid-afternoon.
    [Export(PropertyHint.Range, "1,48,0.5")] public float varianceHours = 12f;
    // Half-width of the smooth-lerp band around sunrise / sunset, in
    // normalized time-of-day. 0.05 ≈ ~70m at a 600s day length: the
    // variance crosses from prev→next over a window centered on
    // sunrise (0.25) or sunset (0.75).
    [Export(PropertyHint.Range, "0.005,0.2,0.005")] public float varianceCrossfadeHalfWidth01 = 0.05f;

    [ExportSubgroup("Baseline (Diurnal)")]
    // Baseline humidity = humidityMax × diurnalCurveOffset(humidity) ×
    // (1 - elevation × ElevHumidity) × (1 - normalizedMaxTemp × HumidityFromMaxTemp)
    // Hot zones give up moisture (deserts dry out as the max temp rises),
    // cool zones hold humidity near the max.
    [Export(PropertyHint.Range, "0,1,0.01")] public float humidityFromMaxTemp = 0.35f;
    // Diurnal swing depth on humidity: 0 = humidity stays at max all day,
    // 1 = humidity hits 0 at the diurnal peak. Real-world humidity dips
    // mid-afternoon (warm air holds more before saturating) and peaks
    // pre-dawn — implemented via the INVERTED diurnal curve.
    [Export(PropertyHint.Range, "0,1,0.01")] public float humidityDiurnalDepth = 0.4f;
    // Elevation reduces baseline humidity (alpine air is dry).
    [Export(PropertyHint.Range, "0,1,0.01")] public float humidityFromElevation = 0.5f;

    // Baseline temperature follows the diurnal curve, damped by humidity
    // (humid air resists swings — warm nights, cool days). Elevation
    // pulls the whole curve down (alpine cool).
    [Export(PropertyHint.Range, "0,1,0.01")] public float tempDiurnalDepth = 0.55f;
    // Humidity damps the diurnal swing (humid jungle = small day/night
    // delta; dry desert = huge delta).
    [Export(PropertyHint.Range, "0,1,0.01")] public float tempHumidityDamping = 0.4f;
    // Elevation cools the baseline (subtracts from the diurnal envelope).
    // Multiplied against authored max temperature so it scales with the
    // zone's heat budget.
    [Export(PropertyHint.Range, "0,1,0.01")] public float tempFromElevation = 0.4f;

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
    [Export(PropertyHint.Range, "0,1,0.01")] public float windDiurnalDepth = 0.3f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float windFromTempDiff = 0.5f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float windFromElevation = 0.6f;

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
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudFromWind = 0.35f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float convectiveStrength = 1.0f;

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
    [Export(PropertyHint.Range, "0,1,0.01")] public float windVarianceK = 0.3f;
    [Export(PropertyHint.Range, "0,5,0.01")] public float windVarianceDeltaK = 1.5f;

    // Humidity uses its OWN independent variance channel. The
    // perturbation is GATED by simulated wind speed: 0 wind = no
    // advection, baseline holds; full wind = full influence. Models
    // "neighboring weather is being blown in". Symmetric around 0.5.
    [Export(PropertyHint.Range, "0,1,0.01")] public float humidityVarianceK = 0.4f;

    // Cloud cover uses its own independent variance channel, gated by
    // wind for the same reason — clouds are physically advected, so a
    // calm day stays at the regional baseline regardless of what the
    // variance rolled.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudVarianceK = 0.6f;

    // Wind speed (m/s) at which the wind-gated variance influence
    // (humidity & cloud) reaches its full strength. Below this the
    // perturbation is scaled linearly down to 0 at zero wind. Tuned
    // to roughly match the same wind range that breaks up the water
    // surface (RippleWindRef) — a "strong but not extreme" wind.
    [Export(PropertyHint.Range, "1,30,0.1")] public float advectedVarianceWindRef = 8f;

    // Temperature: positively related to variance (fair days are hot),
    // but |delta| in variance subtracts (changing weather is unstable
    // and cools the scene off).
    [Export(PropertyHint.Range, "0,1,0.01")] public float tempVarianceK = 0.2f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float tempVarianceDeltaK = 0.4f;

    [ExportSubgroup("Simulated Derived")]
    // Fog is air reaching saturation (RH → 100%). Humidity is the moisture that
    // must be present — the necessity gate; no vapor, no fog, ever. It's then
    // saturated by either of two independent routes (see WeatherDerivation):
    // radiation fog (nocturnal cooling) or precipitation fog (rain, any
    // temperature). The two exponents below shape a curve: > 1 narrows it so
    // only extreme values fog, < 1 widens it so moderate values lift some fog.
    //   FogFromHumidity      — sharpness of the moisture gate. Default 1.5 gives
    //                          dry zones (desert humidity ~0.04) almost no fog
    //                          while keeping swampy zones (~0.95) nearly fully
    //                          fogged. There's no per-zone fog ceiling — a swamp
    //                          fogs from its high baseline humidity, not an
    //                          authored fog field.
    //   RadiationFogSharpness — sharpness of the COOLING route only (rain, the
    //                          other route, has no exponent). > 1 confines
    //                          radiation fog to the deepest pre-dawn cold; < 1
    //                          lets it linger into dusk / morning.
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float fogFromHumidity = 1.5f;
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float radiationFogSharpness = 1.0f;
    // Strength of the evaporative-fog route: standing water / saturated ground
    // (the fog_map's domain) self-saturating the near-surface air with NEITHER
    // cooling nor rain — what keeps a humid swamp misty on a calm clear
    // afternoon. Capped below 1 so this persistent component stays LIGHTER than
    // full radiation / precipitation fog (most fog is still diurnal); the swamp
    // gets an afternoon mist, not pea soup. Wind disperses it (normalized
    // against the zone's own typical wind). 0 = no evaporative fog at all.
    [Export(PropertyHint.Range, "0,1,0.01")] public float evaporativeFogStrength = 0.35f;
    // Low-end dead-zone on the fog signal: fog below this collapses to 0, then
    // the remainder is rescaled to [0,1]. Stops the concave AmbientFog curve
    // from amplifying a trace humidity wisp into visible haze, so a nearly-dry
    // desert reads as genuinely clear. Heavy fog (swamp) is barely affected.
    [Export(PropertyHint.Range, "0,0.9,0.01")] public float fogFloor = 0.1f;

    // Rain needs heavy cloud AND falling temperature (cold front /
    // afternoon-thunderstorm pattern). Falling-temp signal = max(0,
    // -dDiurnalCurve/dt). Authored rainMax is the ceiling.
    [Export(PropertyHint.Range, "0,2,0.01")] public float rainFromCloudCover = 1.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float rainCloudThreshold = 0.5f;
    [Export(PropertyHint.Range, "0,5,0.01")] public float rainFromCoolingRate = 2.0f;

    // WET-MODE thunderstorm: heavy cloud × active rain. The air-mass /
    // frontal thunderstorm — warm humid afternoon convection. The
    // dominant mode for forest, swamp, and other temperate zones.
    // SmoothStep from threshold to 1.0 on both axes; both must be high
    // for the gate to open, so a wet day with thin cloud (or a stormy
    // sky with no rain) produces no wet-mode lightning.
    [Export(PropertyHint.Range, "0,1,0.01")] public float lightningCloudThreshold = 0.7f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float lightningRainThreshold = 0.4f;

    // DRY-MODE thunderstorm: cloud × low humidity × high temperature.
    // High-base storm in hot arid air — virga (rain evaporates before
    // reaching ground), wildfire-igniting strikes. Desert summers. NO
    // rain required, so the wet gate's rain threshold doesn't apply.
    // Cloud threshold is lower than wet because dry storms typically
    // have less total cloud coverage but the cloud they do have reaches
    // very high (cumulonimbus).
    [Export(PropertyHint.Range, "0,1,0.01")] public float dryLightningCloudThreshold = 0.3f;
    // Humidity inversion: humidity below this contributes, above shuts
    // the gate. Sweep is 0 → DryLightningHumidityMax (so humidity = 0
    // is full dry, humidity = max kills the gate).
    [Export(PropertyHint.Range, "0,1,0.01")] public float dryLightningHumidityMax = 0.3f;
    // Air temperature (°F) sweep for the heat axis. Below TempMin the
    // gate is shut; above TempMax it's fully open. ~75 → 95°F matches
    // the temperature range where atmospheric instability lifts dry
    // storm activity in real climates.
    [Export] public float dryLightningTempMin = 75f;
    [Export] public float dryLightningTempMax = 95f;

    // OROGRAPHIC-MODE thunderstorm: cloud × strong wind × high
    // elevation. Air forced up a mountainside, condenses on the
    // windward slope, lightning concentrates along ridgelines. Mountain
    // zones. NO rain required (orographic storms often have lighter
    // precipitation than air-mass storms but more dramatic lightning).
    [Export(PropertyHint.Range, "0,1,0.01")] public float orographicLightningCloudThreshold = 0.4f;
    // Wind speed (m/s) sweep. Below WindMin no lift; above WindMax full
    // gate. Matched roughly to AdvectedVarianceWindRef so "wind that
    // moves weather around" also drives orographic activity.
    [Export(PropertyHint.Range, "0,40,0.1")] public float orographicLightningWindMin = 6f;
    [Export(PropertyHint.Range, "0,40,0.1")] public float orographicLightningWindMax = 14f;
    // Zone elevation (0..1, blended runtime ZoneState.Elevation) sweep.
    // ElevationMin → 1.0. Default 0.5 means only the upper half of the
    // elevation range qualifies — flatland zones don't get orographic
    // lightning regardless of cloud/wind.
    [Export(PropertyHint.Range, "0,1,0.01")] public float orographicLightningElevationMin = 0.5f;

    // Dust: wind × elevation × diurnal-warmth, suppressed by humidity
    // and rain. Authored dustMax is the ceiling.
    [Export(PropertyHint.Range, "0,2,0.01")] public float dustFromWind = 1.0f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float dustFromElevation = 0.5f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float dustFromWarmth = 0.6f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustHumiditySuppression = 0.8f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustRainSuppression = 0.95f;

    [ExportSubgroup("Rain")]
    // Blended rainAmount (0..1) at or above which ESpawnConditions.Clear entries
    // refuse to spawn. Sampled from the live player-blended weather; since spawn
    // gating only runs at chunk activation (which streams around the player),
    // this is the right gameplay read. The lighter sibling of
    // HeavyRainSpawnThreshold — Clear suppresses on any meaningful rain, while
    // NotHeavyRain only suppresses in a real downpour. See Sim.SpawnConditionsMet.
    [Export(PropertyHint.Range, "0,1,0.01")] public float rainSpawnThreshold = 0.2f;
    // Blended rainAmount (0..1) at or above which weather counts as "heavy
    // rain" for spawn gating: mobs/chests flagged ESpawnConditions.NotHeavyRain
    // refuse to spawn once rain reaches this. Distinct from the lighter Clear
    // gate (any meaningful rain) — heavy rain only suppresses spawns in a real
    // downpour. See Sim.SpawnConditionsMet.
    [Export(PropertyHint.Range, "0,1,0.01")] public float heavyRainSpawnThreshold = 0.6f;

    // rainWeight at cloudCover=0 (scattered thin cloud). Light drizzle.
    // Multiplies rain fall velocity, drop alpha, streak length linearly,
    // and inversely scales wind tilt (lighter drops blow more).
    [Export(PropertyHint.Range, "0,3,0.01")] public float rainWeightMin = 0.3f;
    // rainWeight at cloudCover=1 (full overcast). Heavy downpour but
    // capped short of comically elongated streaks — 1.2 gives stormy
    // zones ~20% longer drops than default without turning rain into
    // lines across the whole screen.
    [Export(PropertyHint.Range, "0,3,0.01")] public float rainWeightMax = 1.2f;
    // Exponent shaping rainAmount → rainIntensity (drop COUNT). 1.0 is
    // linear; >1 compresses low authored values (a light drizzle at
    // rainAmount=0.3 emits fewer drops than a linear mapping would
    // suggest), while high values stay near the authored amount.
    [Export(PropertyHint.Range, "0.3,3,0.01")] public float rainIntensityExponent = 1.25f;

    // Rain-tier boundaries on the derived RainIntensity (0..1), the same scale
    // the wetness and lantern-douse paths already read. Below drizzle reads as
    // clear (no rain); [drizzle, light) is "drizzle" — visible falling rain too
    // fine to soak the player; [light, heavy) is "light rain"; at/above heavy is
    // "heavy rain". Only light and heavy rain accumulate the player's Wet status
    // (drizzle never does; swimming soaks regardless). Keep drizzle < light <
    // heavy. See WeatherDerivation.ClassifyRainTier.
    [Export(PropertyHint.Range, "0,1,0.01")] public float rainDrizzleThreshold = 0.02f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float rainLightThreshold = 0.15f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float rainHeavyThreshold = 0.6f;

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
    [Export(PropertyHint.Range, "1,32,1")] public int blockLightMaxDistance = 14;

    // CORNER AO — strength of the ambient-occlusion darkening on voxels with few
    // open neighbours (corners, crevices, against walls/ground). 0 = off; 1 = a
    // fully-enclosed-ish voxel goes dark. A free concavity hint, applied on top
    // of the lighting (absolute, like fog — it doesn't redistribute energy).
    [Export(PropertyHint.Range, "0,1,0.01")] public float blockLightAO = 0.5f;

    // FLICKER CULL DISTANCE (voxels). A flickering light beyond this from the
    // player stops re-rolling and holds a steady full brightness — each flicker
    // tick re-deposits a footprint and re-dirties its chunks, so this caps that
    // churn to the handful of lights near the player (where flicker is actually
    // visible). The player's own torch is always at distance ~0, so it never
    // culls. Large enough to cover the visible play area.
    [Export(PropertyHint.Range, "4,128,1")] public float blockLightFlickerCullDistance = 28f;

    // MOVING-LIGHT RESHADE CULL DISTANCE (voxels). A moving light (carried torch)
    // re-shades and re-deposits its footprint every frame for smooth sub-voxel
    // motion — each reshade re-dirties its chunks and forces a LightMap upload.
    // Beyond this distance from the player the per-frame reshade is skipped: the
    // light still snaps to a fresh field on each voxel crossing (so it follows
    // its carrier), it just stops paying the every-frame sub-voxel update where
    // the smoothing isn't visible. Larger than the flicker cull because a torch
    // lagging its carrier reads further out than a missing flicker pulse.
    [Export(PropertyHint.Range, "4,160,1")] public float blockLightMovingReshadeCullDistance = 40f;

    // Medium extinction (Beer-Lambert optical depth) added to the flood as it
    // passes through fog / foliage canopy. Each fully-dense voxel the light
    // crosses adds this much optical depth to the running total, and brightness
    // is multiplied by exp(-opticalDepth) on top of the geometric exp(-d/λ)
    // falloff. So a torch dims faster the more foggy / canopied air its light
    // threads through — independent of the geometric radius. 0 = the medium is
    // transparent to block light. Scales linearly with per-voxel density.
    [Export(PropertyHint.Range, "0,2,0.01")] public float blockLightFogExtinction = 0.15f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float blockLightCanopyExtinction = 0.15f;

    [ExportGroup("Foliage Canopy Shadow")]
    // FoliageStamper rasterizes every CastsSunShadow cluster's ellipsoid into
    // WorldState.CanopyAttenuation (leaves) and derives the shadow beneath the
    // tree into WorldState.CanopyShade; LightEngine reads them as extra sun +
    // block-light falloff during propagation. The knobs below shape how much a
    // tree shelters; per-cluster relative thickness is FoliageCluster.
    // ShadowDensity.

    // Density (0..1) of ONE NOMINAL canopy blob, before the cluster's own
    // ShadowDensity scales it. Stored as a byte 0..255 and saturating-added
    // wherever foliage genuinely overlaps — clusters stacked in 3D on one tree,
    // or neighbouring trees' canopies — so this is the scalar that sets how much
    // a single blob's worth of leaves dims the sun, and thickness does the rest.
    [Export(PropertyHint.Range, "0,1,0.01")] public float canopyDensity = 0.4f;

    // Minimum voxels of shadow stamped below the canopy. The column extends down
    // to whichever is LOWER: this fixed depth, or one voxel below the prop's base
    // — the base anchor keeps shadows reaching the ground under tall-trunk trees
    // (birch at ~10m foliage) without authoring per-species depths. Without the
    // column at all, lateral BFS spread from un-canopied neighbor columns refills
    // the player's voxel with near-full sun. It costs nothing in DEPTH of shade:
    // the column carries the canopy's own integral and only the lateral pass
    // reads it, so a taller trunk spreads the same shadow further, never darker.
    [Export(PropertyHint.Range, "0,32,1")] public int canopyShadowDepthVoxels = 6;

    // Sun-channel canopy extinction: the Beer-Lambert optical depth added per
    // voxel of fully-dense (255) canopy the sky light passes through. Sun is
    // MULTIPLIED by exp(-density * this) at each such voxel rather than having a
    // flat amount subtracted, so shadow compounds smoothly with depth and
    // approaches — but never snaps to — black. A lone tree's shadow column
    // stays dim-but-readable while a deep, dense forest interior (many canopy
    // voxels with no lateral sun to leak back in) drives toward very dark.
    // Mirrors BlockLightCanopyExtinction for the block-light flood. 0 = canopy
    // casts no sun shadow. Tune alongside CanopyDensity / CanopyShadowDepthVoxels;
    // ~0.6 keeps a typical tree under the rain shader's 0.7-of-MAX_LIGHT shelter
    // threshold while staying far from black.
    [Export(PropertyHint.Range, "0,3,0.01")] public float canopySunExtinction = 0.6f;
    // Same, for one voxel of fully-dense (255) fog / dust. Mirrors
    // BlockLightFogExtinction for the block-light flood. Deliberately far weaker
    // than the canopy figure: sun threads a long column through the whole air
    // mass, so a coefficient tuned to read as convincing view haze over tens of
    // metres strangles light transport over the ~10 voxels it takes to reach into
    // a room. Dust limits VISIBILITY; it should barely limit ILLUMINATION (our
    // model is absorption-only — real dust also forward-scatters light back in).
    [Export(PropertyHint.Range, "0,1,0.005")] public float fogSunExtinction = 0.05f;
    // Sun level lost per voxel of LATERAL spread away from a sunlit column (the
    // vertical column scan pays no falloff). Reach is LightEngine.MAX_LIGHT / this
    // — at 2, light carries 30 voxels in from a window or cave mouth. Lower =
    // deeper leakage, but the flood touches more voxels per relight, and a door
    // toggle re-encodes every chunk it reaches (plus their 6 neighbours) on the
    // main thread, so watch hitches when lowering it.
    [Export(PropertyHint.Range, "1,15,1")] public int sunFalloffPerVoxel = 2;

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
    // Sim.RefreshTimeOfDayEntities.
    [Export(PropertyHint.Range, "0,100,1")] public float spawnMinDistanceFromPlayer = 24f;

    [ExportGroup("Spawn Cleanup")]
    // Mirror of the spawn gate: a loaded mob whose ESpawnConditions no longer
    // hold (a night goblin caught at dawn, a clear-day sparrow once it starts
    // raining) is despawned back to its persistent sim state — but only once
    // it's far enough away, the player has lost track of it, and it isn't
    // hunting the player. Cleared mobs respawn naturally when their conditions
    // come back and their chunk is active. See Sim.CleanupOffConditionMobs.
    //
    // Distance (m) from the player beyond which an off-condition mob becomes
    // eligible for cleanup. Must comfortably exceed view distance so the
    // despawn never pops on-screen — the "player has lost track" gate already
    // means it's invisible, this is belt-and-suspenders against edge cases.
    [Export(PropertyHint.Range, "10,200,1")] public float spawnCleanupDistance = 50f;
    // Seconds between cleanup sweeps. The sweep walks every loaded mob, so it
    // runs on an interval rather than per-frame; a couple of seconds is plenty
    // since spawn conditions (time of day, weather) change slowly.
    [Export(PropertyHint.Range, "0.5,30,0.5")] public float spawnCleanupIntervalSeconds = 2f;

    [ExportGroup("Mob Tick LOD")]
    // Mobs farther than this from the player and not in combat drop their
    // rate-based upkeep (terrain speed, wetness / sunburn / safe-zone heal,
    // status + DoT timers, footstep and ripple emission) to
    // mobColdTickIntervalSeconds instead of every physics tick. Deliberately
    // excludes animation and steering: at this range a mob can still be on
    // screen, and throttling those reads as stutter (the same reason
    // mob_pose_distance defaults to off).
    [Export(PropertyHint.Range, "0,200,1")] public float mobColdTickDistance = 30f;
    // Cold-band period. Every skipped tick's delta accumulates and is handed to
    // the subsystems when they do run, so rate-based effects integrate to the
    // same totals — only their granularity coarsens.
    [Export(PropertyHint.Range, "0.016,1,0.001")] public float mobColdTickIntervalSeconds = 0.133f;

    [ExportGroup("Night Ambient Spawn")]
    // Composed mobs the NightMobSpawner materializes in dark spots around the
    // player after dark, one picked at random per spawn. These are TRANSIENT
    // (not persisted to WorldState) — the live population near the player IS the
    // whole mechanic. Empty = the night spawner stays dormant (no cost).
    [Export] public Array<MobDescriptor> nightSpawnMobs = new();

    // Live night-mob population the spawner drives toward at the peak of night
    // (midnight). Measured against currently-loaded night mobs near the player,
    // so it's effectively "how many surround you at the densest". Keep modest —
    // this is the "don't spawn too many" ceiling.
    [Export(PropertyHint.Range, "0,120,1")] public int nightSpawnMaxPopulation = 18;

    // The whole mechanic runs off ONE danger scalar = max(time-of-day term,
    // darkness dwell). Both live on [0,1]; danger drives spawn rate, population
    // cap, AND level together. The player's own light is NOT a spawn input — it's
    // the separate concealment axis (slime vision, MobData.darkness*). See
    // Sim.DarknessDwell / NightMobSpawner.

    // Shapes the TIME term: pow(nightProgress, this), where nightProgress is 0 at
    // sunset → 1 at midnight (and 0 all day, so daylight danger comes only from
    // darkness). >1 keeps early night calm and ramps hard toward midnight; 1 =
    // linear. The clock holds at midnight, so peak danger persists until sleep.
    [Export(PropertyHint.Range, "0.25,6,0.05")] public float nightTimeDangerCurve = 2.5f;

    // DARKNESS term. Total perceived light [0,1] at the player at/below which the
    // spot counts as fully dark (dwell targets 1); it ramps linearly to 0 as light
    // reaches this. Deliberately low: this gate answers only "am I somewhere
    // genuinely dark" (cave, dungeon, unlit interior) at any hour. Danger is
    // max(time-of-day, dwell), so NIGHT is carried by the time term and needs no
    // help from here — keep this below a windowed-but-roofed interior reading
    // (~0.13-0.17) so only near-black spaces accumulate.
    [Export(PropertyHint.Range, "0,1,0.01")] public float nightDarkThreshold = 0.1f;
    // Seconds for the darkness dwell to charge from 0 to full in pitch black — the
    // "lurking in the dark draws them" ramp. Shorter in dimmer-but-not-black spots
    // (it only eases toward that spot's partial darkness).
    [Export(PropertyHint.Range, "5,300,1")] public float nightDarkRiseSeconds = 90f;
    // Seconds for the darkness dwell to drain to 0 in full light — how fast the
    // danger cools once the player reaches a bright/open/daylit spot.
    [Export(PropertyHint.Range, "1,120,1")] public float nightDarkFallSeconds = 15f;
    // Direct-sun exposure (Sim.SunBurnExposure = sun elevation × open-sky) at
    // which the "shade" factor hits 0 — darkness stops building and slimes stop
    // spawning (they'd burn). Below it, shade ramps up linearly, so only deep
    // dawn/dusk twilight (sun barely up) tolerates slimes in the open. Low so any
    // real daytime sun fully excludes them; 0 makes it a hard on/off gate. Shared
    // by the darkness-dwell gate and the spawn-cell weight; the sunburn DoT reads
    // the same exposure so all three agree on where the sun burns.
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunShadeFullExposure = 0.15f;

    // Spawn interval (s) at full danger (fast) and near-zero danger (slow); the
    // live interval lerps between them by danger, so the dark ramps spawns up.
    [Export(PropertyHint.Range, "0.5,30,0.5")] public float nightSpawnIntervalSeconds = 4f;
    [Export(PropertyHint.Range, "0.5,120,0.5")] public float nightSpawnSlowIntervalSeconds = 20f;

    // Cap on new mobs spawned per interval, so a large deficit (a danger spike, or
    // after fast-forwarding the clock) fills in over several intervals rather than
    // a sudden wall of enemies at once.
    [Export(PropertyHint.Range, "1,20,1")] public int nightSpawnMaxPerSweep = 3;

    // Spawn ring around the player (m). Min keeps a mob from popping in within view
    // in the player's lap; max must stay inside the loaded entity radius so the
    // ground/collision under the spawn point actually exists. Max also sizes the
    // nav-grid window scanned each cycle ((2·max+1)² columns, bounded by the code's
    // MaxWindowHalfExtent), so keep it modest — larger costs more per scan and
    // spawns farther off.
    [Export(PropertyHint.Range, "4,80,1")] public float nightSpawnMinRadius = 8f;
    [Export(PropertyHint.Range, "4,120,1")] public float nightSpawnMaxRadius = 24f;

    // Location gate: a candidate position spawns only where BLOCK light (torches,
    // campfires, lanterns — peak channel) is at or below this, so gellies appear in
    // the dark around the player (moonlit ground and shadow both qualify) and never
    // inside a firelit circle. Independent of the danger scalar. Low = they hug the
    // dark just outside firelight.
    [Export(PropertyHint.Range, "0,1,0.01")] public float nightSpawnMaxBlockLight = 0.28f;
    // Among the valid (standable, not block-lit) spawn candidates each cycle,
    // selection is weighted toward darker ones (lower perceived light, shadow-only
    // reading) raised to this power — so gellies prefer deep shadow / caves over
    // open moonlight. 0 = ignore darkness (uniform pick among valid spots); 1 =
    // linear; higher = strongly favor the darkest candidate.
    [Export(PropertyHint.Range, "0,6,0.05")] public float nightSpawnDarknessBias = 2f;

    // Difficulty tier at full danger (midnight, or dwelling in total darkness). A
    // spawn's level is round(danger × this) — each level scales health / armor /
    // outgoing damage by levelScalePerLevel (~1.5x) and shows as level+1 HUD pips.
    // Already-spawned mobs keep
    // the level they arrived at; only new spawns scale up.
    [Export(PropertyHint.Range, "0,4,1")] public int nightSpawnMaxLevel = 4;

    [ExportGroup("Fairy Ambient Spawn")]
    // The fairy the FairySpawner materializes near the player at a few points across
    // the day (its daytime sibling to the NightMobSpawner). Null = the fairy spawner
    // stays dormant (no cost). WHICH zones spawn fairies — and how likely — is
    // authored per zone on ZoneData.canSpawnFairy / fairySpawnChance, read live at
    // the player's location. Spawns are TRANSIENT (Sim.SpawnMobTransient with
    // ESpawnConditions.None), so like the night gellies they live only near the
    // player and are never persisted.
    [Export] public MobDescriptor fairySpawnDescriptor;

    // The day (sunrise → midnight, WorldState.TimeOfDay01 in [0,1]) is split into
    // this many equal blocks. One spawn is attempted on entering each block EXCEPT
    // the first, so at most (fairyDayPeriods - 1) fairies are attempted per day —
    // further capped by FairyMaxSpawnsPerDay.
    [Export(PropertyHint.Range, "2,12,1")] public int fairyDayPeriods = 6;

    // Hard ceiling on fairies spawned in a single day, regardless of how many blocks
    // roll successfully. Counters reset on the day rollover (sleep-to-sunrise).
    [Export(PropertyHint.Range, "1,20,1")] public int fairyMaxSpawnsPerDay = 5;

    // Once the player has killed this many fairies in a day, no more spawn until the
    // next day.
    [Export(PropertyHint.Range, "1,20,1")] public int fairyKillStopCount = 3;

    // Spawn ring around the player (m). Min keeps a fairy from popping in the
    // player's lap; max must stay inside the loaded entity radius so the ground
    // under the spawn point actually exists (it also sizes the nav-grid scan window).
    [Export(PropertyHint.Range, "4,80,1")] public float fairySpawnMinRadius = 12f;
    [Export(PropertyHint.Range, "4,120,1")] public float fairySpawnMaxRadius = 28f;

    // Fairy level (→ HP) scales with how late in the day it spawns: the first
    // spawnable period is level 0 and the last period is FairyMaxLevel, so a
    // fairy met near dusk is tougher than one met mid-morning. 0 = all fairies
    // spawn at level 0 (no scaling).
    [Export(PropertyHint.Range, "0,20,1")] public int fairyMaxLevel = 4;

    // How long a spawned fairy lives, as a fraction of a day's length
    // (dayLengthSeconds). Once a fairy has lived past this it despawns — but only
    // while it is not currently visible to the player, so it never pops out of
    // sight mid-frame. Measured on the sim clock, so the countdown keeps running
    // through the end-of-day hold rather than freezing until the player sleeps.
    // 0.2 ≈ a fifth of a day.
    [Export(PropertyHint.Range, "0.02,1,0.01")] public float fairyLifetimeDayFraction = 0.2f;

    [ExportGroup("Companion")]
    // The persistent companion follows the player but can fall outside the
    // loaded world if the player outruns it (no resident collision under it).
    // Sim's per-frame leash (Sim.TickCompanionLeash) then snaps it onto one
    // of the player's recent footsteps instead of letting it fall through. These
    // two knobs size that breadcrumb trail: a sample is recorded every
    // CompanionRescueSampleSeconds and the last CompanionRescueHistoryCount
    // samples are kept. The OLDEST still-loaded sample is chosen as the
    // relocation target — furthest behind the player, so the pet pops back in
    // off-screen. The trail must reach far enough back in world space to still
    // land inside the loaded entity radius: count × sample-seconds × player
    // speed should stay under that radius (~ENTITY_LOAD_RADIUS chunks).
    [Export(PropertyHint.Range, "0.1,5,0.1")] public float companionRescueSampleSeconds = 1f;
    [Export(PropertyHint.Range, "1,64,1")] public int companionRescueHistoryCount = 16;

    // Distance backstop for the catch-up rescue. A FOLLOWING companion (not one
    // commanded to stay) that stays farther than CompanionRescueMaxDistance from
    // the player for CompanionRescueMaxDistanceGraceSeconds is snapped onto the
    // breadcrumb trail even while still on a resident chunk — so a dog that fell
    // behind or wedged on geometry catches up at this gap instead of trailing
    // off to the edge of the loaded world before the residency rescue fires.
    // Keep above the follow behavior's catchUpRadius so the dog gets a chance to
    // run the gap closed before teleporting.
    [Export(PropertyHint.Range, "5,100,1")] public float companionRescueMaxDistance = 30f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float companionRescueMaxDistanceGraceSeconds = 1.5f;

    [ExportGroup("Footprints")]
    // Template material for the batched footprint MultiMesh. FootprintScatter
    // duplicates it once per actor footprint texture (binding that texture's
    // albedo) and drives the per-print tint + animated alpha through
    // INSTANCE_COLOR — so the template must be unshaded, alpha-blended, and
    // have vertex_color_use_as_albedo enabled. See footprint_multimesh.tres.
    [Export] public Material footprintMaterial;

    // ===== Mob-print discovery gate =====
    // Prints a mob lays while the player hasn't yet noticed it stay invisible
    // until the player perceives the print itself (then they fade in). Player
    // prints — and mob prints laid while the mob was already perceived — skip
    // this and show immediately. These mirror the tunings that used to live on
    // the Discoverable child of the old footprint_discoverable scene.

    // Free scalar on the player's vision range for noticing a print. < 1 makes
    // prints subtler than a live target (a faint mark in the grass).
    [Export] public float footprintDiscoveryProminence = 0.3f;
    // Perception value at which a print flips to visible. ~1 (perception must
    // saturate); lower pops prints in sooner.
    [Export(PropertyHint.Range, "0,1,0.01")] public float footprintDiscoveryThreshold = 1f;
    // Height above the print to sample world light at when gauging whether the
    // player could notice it (just above the floor voxel).
    [Export] public float footprintDiscoveryLightSampleHeight = 0.05f;
    // Seconds for the noticed-fade-in to traverse 0..1 once a print is
    // discovered.
    [Export(PropertyHint.Range, "0.05,2,0.01")] public float footprintDiscoveryFadeSeconds = 0.4f;
    // Per-ground-type tint applied to every footprint laid down on that
    // surface. The Color's RGB tints the actor's footprint texture (sand
    // → warm tan, mud → dark brown, snow → white); the Color's ALPHA is
    // the baseline opacity at spawn and is what the runtime fades to 0
    // over FootprintDurationSeconds. Surfaces that shouldn't take prints
    // (wood, treated stone) leave their key out of the dictionary — the
    // emitter treats missing keys as no-emit. Wet status effects multiply
    // alpha and duration via the StatusEffectData footprint multipliers.
    [Export] public Godot.Collections.Dictionary<EGroundType, Color> footprintColors = new()
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
    [Export] public float footprintDurationSeconds = 15f;

    [ExportGroup("Roofs")]
    // Cap-mask material every generated roof renders a second copy of itself
    // with, on GameCamera.CapMaskLayer (cap_mask_prop.tres). Shared, not
    // per-style: it writes a flat black "geometry covers this pixel" mask and
    // never shows on screen. Without it a roof is invisible wherever no terrain
    // sits behind it — the mask viewport's white clear means "cap here", so the
    // cap plane paints straight over it. Null = skip the pass.
    [Export] public Material roofCapMaskMaterial;
    // Non-clipping shadow proxy material, shared with voxel terrain
    // (voxel_shadow_caster.gdshader via roof_shadow_caster.tres). A roof's
    // visible material DISCARDS above the cutaway, and Godot runs fragment()
    // in the shadow pass too — so without a proxy a roof stops casting the
    // moment it cuts away, and the interior it just revealed floods with sun.
    // Same failure voxel terrain solves the same way. Null = skip the pass.
    [Export] public Material roofShadowCasterMaterial;
    // Back-faces-only pass (roof_lit_interior.tres) drawn on roofs that have
    // holes, so each opening shows a ring of slab interior instead of a clean
    // shaft straight through. Shared: the per-style hole shape reaches it via
    // instance uniforms. Null = skip the pass (holes look paper-thin).
    [Export] public Material roofInteriorMaterial;

    [ExportGroup("Grounding Shadows")]
    // Shared material for the batched grounding-shadow blobs (GroundShadowScatter
    // uploads one MultiMesh instance per shadow-caster — the player plus every
    // qualifying mob). Must be unshaded, alpha-blended, and have
    // vertex_color_use_as_albedo enabled so each blob's per-instance alpha rides
    // INSTANCE_COLOR.a (ground_shadow_blob.tres). The blob's radial shape +
    // baseline darkness come from the material's gradient texture.
    [Export] public Material groundShadowMaterial;
    // Radius (world units) of the player's grounding-shadow blob. Mobs carry
    // their own MobData.groundShadowRadius; this is the player's. 0 = no player
    // blob.
    [Export(PropertyHint.Range, "0,4,0.05")] public float playerShadowRadius = 0.65f;
    // Master multiplier on every MOB blob's alpha (the player blob is always at
    // full base alpha). The shared "how dark are mob grounding shadows" knob;
    // lower it if clustered mobs pool too dark (overlap composites alpha-over in
    // the projector RT, so it self-limits but still deepens). 0 = mob blobs off
    // without touching the player's.
    [Export(PropertyHint.Range, "0,1,0.01")] public float mobShadowAlpha = 0.7f;
    // How strongly daylight suppresses the blobs. Each blob's alpha is scaled by
    // 1 - groundShadowDaylightFade * (DirectionalShadowStrength * skyExposure):
    // a blob fades out only where the sun/moon already throws a crisp shadow AND
    // the caster stands under open sky, so it substitutes for a real contact
    // shadow rather than doubling it. 1 = full suppression (default); 0 = blobs
    // ignore daylight and always render at full strength.
    [Export(PropertyHint.Range, "0,1,0.01")] public float groundShadowDaylightFade = 1f;

    [ExportGroup("Interior Ambience")]
    // Palette of space classes. A cell of ChunkState.EnvTag stores an INDEX
    // into this list, so what the air, wind and acoustics are like at a point
    // is authored per 4³-voxel cell — orthogonal to zone, which says where in
    // the world you are rather than what kind of space you're in.
    //
    // ORDER IS PART OF THE FILE FORMAT for the first four entries only: indices
    // 0..3 must stay Outdoor / Building / Cave / Tunnel (the legacy
    // EnvironmentTag values), because every .hike and .hikescene written before
    // this palette existed stores those bytes. APPEND new classes; never insert
    // or reorder within the first four.
    //
    // Null entries are skipped by the sampler and read as "no data", so a hole
    // in the list degrades to unweighted rather than throwing.
    [Export] public InteriorAmbienceData[] interiorAmbiences = System.Array.Empty<InteriorAmbienceData>();

    // What worldgen assigns to a cell with cover overhead. The only class it
    // can infer beyond outdoor — vertical cover can't tell a tidy hall from a
    // dusty cellar, so everything finer is painted. Must be a member of the
    // palette above; anything else resolves to index 0 and every sheltered
    // cell in a generated world silently reads outdoor.
    [Export] public InteriorAmbienceData worldgenEnclosedAmbience;

    // Interiorness at which a cell stops being outdoor and takes the enclosed
    // class. Only picks WHICH class; how strongly it applies rides the
    // continuous interiorness value, so this is far less delicate than a
    // threshold on a binary classifier would be.
    [Export(PropertyHint.Range, "0,1,0.01")] public float interiorEnclosureThreshold = 0.35f;

    [ExportSubgroup("Interiorness Flood")]
    // Travel cost at which a cell counts as fully enclosed. Roughly "voxels
    // from the outdoors through open space", since crossing open air costs 1
    // per voxel — so 24 means a cell two dozen voxels into a wide cave is
    // fully interior. Squeezing through apertures costs much more (below), so
    // a room behind a doorway saturates far sooner than this suggests.
    [Export(PropertyHint.Range, "4,128,1")] public int interiornessSaturationCost = 10;

    // Extra cost per BLOCKED neighbour when stepping into a voxel — the
    // aperture-width term, and the whole reason this works where light didn't.
    // A voxel in open air has 6 air neighbours and costs 1 to enter; one inside
    // a one-voxel doorway or roof hole has ~2 and costs 1 + 4×this. Raise it
    // and narrow gaps seal harder (a window stops mattering); lower it and the
    // measure degrades toward plain distance-from-outside.
    [Export(PropertyHint.Range, "0,8,1")] public int interiornessNarrowPenalty = 1;

    // How open to the sky a voxel must be to seed the flood as "outdoors",
    // as a fraction of full sky exposure. Below 1 so a voxel under light
    // canopy still counts as outside; low enough values would start seeding
    // inside cave mouths and flatten the measure.
    [Export(PropertyHint.Range, "0.1,1,0.01")] public float interiornessSeedSkyFraction = 0.9f;

    // Palette position of an entry, for the classification bake. Returns 0
    // (outdoor) for null or a resource that isn't in the list.
    // Index resolved against the palette above, clamped and null-checked.
    // Out-of-range indices (a world authored against a longer palette) fall
    // back to entry 0 rather than nothing, so a stale index reads as outdoor
    // instead of silently contributing zero weight everywhere.
    public InteriorAmbienceData GetInteriorAmbience(int index)
    {
        if (interiorAmbiences == null || interiorAmbiences.Length == 0)
        {
            return null;
        }
        if (index < 0 || index >= interiorAmbiences.Length)
        {
            return interiorAmbiences[0];
        }
        return interiorAmbiences[index];
    }

    // Palette position of an entry, for the classification bake. Returns 0
    // (outdoor) for null or a resource that isn't in the list.
    public int IndexOfInteriorAmbience(InteriorAmbienceData data)
    {
        if (data == null || interiorAmbiences == null)
        {
            return 0;
        }
        for (int i = 0; i < interiorAmbiences.Length; i++)
        {
            if (interiorAmbiences[i] == data)
            {
                return i;
            }
        }
        return 0;
    }

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
    [Export(PropertyHint.Range, "1,30,0.1")] public float perceptionWindReference = 12f;

    // Fraction of hearing (audible) range removed at PerceptionWindReference.
    // Turbulent air scatters sound, so a gale partially masks footsteps for
    // both the player and listening mobs. 0.5 = halved audible radius in a
    // strong wind.
    [Export(PropertyHint.Range, "0,1,0.01")] public float hearingWindSuppression = 0.5f;

    // Added fraction of hearing range at full fog. Still, damp foggy air
    // carries sound farther, so fog is a (small) boon to hearing — and a
    // counterweight to the vision loss fog also imposes. 0.3 = +30% audible
    // radius in thick fog.
    [Export(PropertyHint.Range, "0,2,0.01")] public float fogHearingBoost = 0.3f;

    // Fraction of vision range removed at full fog. Fog scatters light and is
    // the dominant weather reducer of sight. 0.6 = vision cut to 40% of its
    // clear-air reach in the thickest fog.
    [Export(PropertyHint.Range, "0,1,0.01")] public float fogVisionReduction = 0.6f;

    // Fraction of vision range removed at full rain. Rain is a slight extra
    // haze on top of any fog it brings — kept small so a downpour alone
    // doesn't blind anyone. 0.15 = -15% sight in heavy rain.
    [Export(PropertyHint.Range, "0,1,0.01")] public float rainVisionReduction = 0.15f;

    // Added fraction of smell range at full fog. Humid foggy air holds scent,
    // widening the radius a mob can pick up the player's trail. 0.5 = +50%
    // smell reach in thick fog.
    [Export(PropertyHint.Range, "0,2,0.01")] public float fogSmellBoost = 0.5f;

    // Smell potential multiplier ADDED when a scent source is fully downwind
    // of the smeller (wind blows from the source toward the nose). Scaled by
    // wind strength, so calm air carries no directional bias. 1.0 = a strong
    // downwind doubles the perceived scent.
    [Export(PropertyHint.Range, "0,3,0.01")] public float smellDownwindBoost = 1.0f;

    // Fraction of smell potential removed when a source is fully upwind
    // (wind blows the scent away from the nose). Scaled by wind strength.
    // 0.7 = a strong upwind drops the scent to 30% of its still-air value.
    [Export(PropertyHint.Range, "0,1,0.01")] public float smellUpwindReduction = 0.7f;

    // Fraction of smell range removed at PerceptionWindReference, regardless
    // of direction. High wind scatters and dilutes scent overall — a
    // counterweight to the downwind boost so a gale isn't a pure smelling
    // advantage. 0.4 = -40% smell reach in a strong wind.
    [Export(PropertyHint.Range, "0,1,0.01")] public float smellWindDisruption = 0.4f;
}
