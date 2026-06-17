public static class CVars
{
    public static CVarString savePath = new CVarString("savepath", "./savegame.dat");
    public static CVarString language = new CVarString("language", "");
    public static CVar version = new CVar("version", (cvar) => Godot.GD.Print(Version.Full));
    public static CVarBool ceilingCap = new CVarBool("ceiling_cap", true);

    // Master scale for the dark-adaptation ("night eyes") effect — lit shaders
    // lift dim surfaces and blow out bright ones based on the player's eye
    // dilation (Player.EyeDilation, sim-owned). 1 = GameClient's authored
    // strength; 0 = off (the shader curve collapses to its exact no-op). Live A/B
    // knob; the authored default lives on GameClient.eyeAdaptationStrength.
    public static CVarFloat eyeAdaptation = new CVarFloat("eye_adaptation", 1.0f, (cvar) =>
    {
        GameClient client = GameClient.Current;
        if (client != null)
        {
            client.eyeAdaptationStrength = ((CVarFloat)cvar).Value;
        }
    });

    // Console action: kills the player. Routes through Player.Kill so the
    // full death sequence (blood / VO / Die animation / onDied →
    // DeathScreen) fires exactly like a fatal hit. No-op if no active
    // player or the player is already dead.
    public static CVar die = new CVar("die", (cvar) =>
    {
        Player player = World.Current?.player;
        if (player == null)
        {
            Godot.GD.PushWarning("die: no active player.");
            return;
        }
        player.Kill();
    });

    // Cheat: when true, every mob's per-tick perception of the player
    // collapses to zero (vision + hearing + smell deltas), canSee is
    // forced false, and accumulated perception relaxes naturally. Triggered
    // mobs disengage as their perception drains below the alert threshold.
    public static CVarBool invisible = new CVarBool("invisible", false);

    // Cheat: when true, Player.OnHurtBoxHit early-returns so incoming
    // damage, status effects, hitstun, and knockback are all ignored.
    public static CVarBool invulnerable = new CVarBool("invulnerable", false);

    // When true, draws the off-screen cap-mask SubViewport texture as a
    // fullscreen overlay so you can see exactly what the cap shader is
    // sampling. White pixels = "cap should draw here", black = "no cap".
    public static CVarBool capMaskDebug = new CVarBool("cap_mask_debug", false, (cvar) =>
    {
        var client = GameClient.Current;
        if (client != null && client.camera != null)
        {
            client.camera.SetCapMaskDebugVisible(((CVarBool)cvar).Value);
        }
    });

    // Bitmask of worldgen entity categories to SKIP. Useful for iterating on
    // terrain shape, kit colors, or fog without the visual clutter — set the
    // bits for the category you want gone:
    //   1  = details      (painted detail-sprite scatter — grass blades etc.)
    //   2  = props        (trees + tall grass)
    //   4  = mobs         (goblins, kun_kun, including cave-pocket spawns)
    //   8  = interactives (loot + chests, including cave pocket variants)
    //   15 = everything (all four categories)
    // Combine with bitwise OR; e.g. 3 = details + props. Default 0 = no skip.
    public static CVarInt worldgenSkip = new CVarInt("worldgen_skip", 0);

    // When true, WorldGen.Generate output is cached to user://worldgen_cache
    // and reused on subsequent boots with the same WorldGenData fingerprint.
    // Invalidation is automatic on .tres edits and WORLDGEN_VERSION bumps;
    // see WorldGenCache.cs. Disable to force fresh generation each run.
    public static CVarBool worldCacheEnabled = new CVarBool("world_cache_enabled", true);
    // Wipe user://worldgen_cache. Use after editing files the fingerprint
    // doesn't cover (.cs helpers WorldGen calls into, .hikescene internals)
    // when you want to confirm a fresh regeneration.
    public static CVar worldCacheClear = new CVar("world_cache_clear", (cvar) => WorldGenCache.Clear());

    // Detaches the game camera from the player and lets WASD + right-mouse-look
    // fly it freely. Disables pixel snapping while active so mouse-look is smooth.
    public static CVarBool debugFlyCam = new CVarBool("debug_flycam", false);

    // Slope diagnostics. When true, the F3 overlay shows the current floor
    // angle + the last hit on an upward-facing surface too steep to climb
    // (FloorMaxAngle-gated), and prints each unique wall hit to the console.
    // Logs only fire while the player has move input, throttled so a single
    // contact doesn't spam per-tick.
    public static CVarBool debugSlopes = new CVarBool("debug_slopes", false);

    // Draw a translucent wireframe sphere at every ApplyAreaDamage burst
    // (status-effect impact/dash bursts, etc.) for one frame. Off by default —
    // the real hit feedback is the authored Fx; this is a dev visualizer for
    // tuning blast radii. Toggle with `debug_aoe 1` in the in-game console.
    public static CVarBool debugAoe = new CVarBool("debug_aoe", false);

    // Once-per-second console print of the sun + canopy reading at the
    // player's voxel — useful to verify foliage shadowing (a tree's
    // FoliageCluster with CastsSunShadow stamps into CanopyAttenuation;
    // stepping under the canopy should drop sky01 below 0.7 so rain is
    // sheltered). Prints player voxel, sunlight (raw + sky01), and the
    // canopy density byte at that voxel.
    public static CVarBool debugSkyLight = new CVarBool("debug_sky_light", false);

    // Max rate (Hz) the block-light LightMap re-uploads to the GPU. The upload
    // is a full-texture push (ImageTexture3D has no partial update), so anything
    // that dirties a chunk every frame — flicker, a moving torch — would force a
    // full re-upload per frame. Throttling caps that to this rate; light updates
    // lag at most 1/this seconds, invisible at 30 Hz. Raise toward the frame rate
    // for snappier light, lower to spend less on the upload.
    public static CVarFloat lightFlushHz = new CVarFloat("light_flush_hz", 30f);

    // Mouse aim sensitivity. Multiplies raw mouseMotion.Relative before
    // accumulating into the aim cursor (clamped to a fixed pixel radius in
    // GameClient). Higher = more cursor travel per pixel of mouse motion =
    // more responsive. 1.0 ≈ raw pixels.
    public static CVarFloat mouseSensitivity = new CVarFloat("mouse_sensitivity", 1.0f);

    // Typewriter speed (characters per second) for the dialogue HUD.
    // DialogueController advances revealed-char count by dt × this value;
    // ui_accept while typing skips to the end of the current line and a
    // second press advances to the next line in the list.
    public static CVarFloat dialogueTypingSpeed = new CVarFloat("dialogue_typing_speed", 40f);

    // When true, the conversation response chooser also shows roll-hidden
    // responses as disabled buttons and appends each response's visibility
    // diagnostic — "[score% / roll%]" — to its label. Score is
    // min(branchComprehension, responseComprehension); the response is
    // visible when the stable per-key roll is below the score. Condition-
    // gated responses are still hidden in debug since they're not a
    // language-comprehension affair.
    public static CVarBool conversationDebug = new CVarBool("conversation_debug", false);

    // Fog shader debug mode (see shaders/fog_volumetric.gdshader):
    //   0 = normal fog render
    //   1 = visualize reconstructed surface world Y as grayscale
    //   2 = visualize fog_map density sampled at surface
    public static CVarInt fogDebug = new CVarInt("fog_debug", 0, (cvar) =>
    {
        World.Current?.SetFogDebugMode(((CVarInt)cvar).Value);
    });

    // Gates AUTHORED voxel fog contribution only (fog_map). Dust + shafts
    // + halos keep working when this is off — they come from dust_density
    // and block-light accumulation which are independent of the fog_map.
    public static CVarBool fogEnabled = new CVarBool("fog_enabled", true, (cvar) =>
    {
        World.Current?.SetFogEnabled(((CVarBool)cvar).Value);
    });

    // Master kill-switch for the ENTIRE volumetric fog pass — haze, shafts,
    // halos, dust, everything. When false, the fog shader early-outs to
    // transparent before any raymarching or texture work. Use on low-spec
    // machines as a graphics option, or toggle while profiling to see how
    // much of the frame budget the fog pass accounts for.
    public static CVarBool fogVolumetricEnabled = new CVarBool("fog_volumetric", true, (cvar) =>
    {
        World.Current?.SetFogVolumetricEnabled(((CVarBool)cvar).Value);
    });

    // Disable just the sun-shaft inscatter contribution while leaving
    // haze, block-light halos, and dust extinction intact. Diagnostic for
    // separating "is this dark band shaft-shaped or haze-shaped?" — toggle
    // it and look at the same scene. Implemented by SkyController as a
    // gate on `sun_shaft_intensity` pushed to the fog material.
    public static CVarBool sunShafts = new CVarBool("sun_shafts", true);

    // TEST: fade the sun-wash DARKENING out where the air column has
    // (near-)zero sunlight. The darkening is wash×(1-lit_frac), so it's
    // maximal in fully-shadowed air — this gates it by
    // smoothstep(0, value, lit_frac) so genuinely lightless areas get no
    // wash. 0 = off (current behaviour); ~0.08 = fade out below ~8% lit.
    // Pushed to the fog material as `shaft_light_floor`.
    public static CVarFloat shaftLightFloor = new CVarFloat("shaft_light_floor", 0f);

    // Atmospheric visual state — sky dome, clouds, sun tint, fog haze,
    // inscatter shafts, animated dust — is derived each frame by
    // SkyController from (ZoneData, WeatherData, time-of-day) via
    // WeatherDerivation. Zones live on SimData (4-quadrant scaffolding);
    // authoring new looks means editing ZoneData.tres + WeatherData.tres
    // (or the derivation tuning group on SimData), not CVars.

    // Debug: when > 0, overrides simulated lightningAmount each frame
    // after WeatherSimulation.Apply runs, bypassing the cloud/rain
    // gates and the lightning-variance roll. 1.0 = full electrical
    // storm immediately. 0 = no override (normal sim behavior, lightning
    // emerges from the gated variance system). Affects every Apply
    // call including the HUD's forecast objects, so the thunder icon
    // also lights up while the override is on.
    public static CVarFloat forceLightning = new CVarFloat("force_lightning", 0f, (cvar) =>
    {
        float v = ((CVarFloat)cvar).Value;
        WeatherSimulation.ForceLightningOverride = v > 0f ? (float?)v : null;
    });

    // Debug: force the wind speed (m/s) that WindParticleManager gates on,
    // overriding the simulated weather wind (which WeatherSimulation.Apply
    // rewrites every frame, so the console can't otherwise hold a value).
    // < 0 = off (use real weather). Set e.g. `wind_force 20` to make leaves /
    // sand / foam emit regardless of the current calm; `wind_force -1` to clear.
    public static CVarFloat windForce = new CVarFloat("wind_force", -1f);

    // Debug: when true, WindParticleManager prints a once-per-second status
    // line (wind, rain, gate state, leased emitter count) so you can see
    // whether the system is activating and why not.
    public static CVarBool windParticleDebug = new CVarBool("wind_particle_debug", false);

    // Debug: spawn a single damaging lightning strike at a random
    // position in the weather-lightning spawn annulus around the
    // player. Bypasses the spawner's cadence and intensity floor so
    // you can hit-test the strike entity end-to-end (warning,
    // flash, screen overlay, radial damage) without waiting for a
    // storm to roll in. Uses World.Current.SimData.weatherLightning
    // — wire it in the resource for this to do anything.
    public static CVar strikeLightning = new CVar("strike_lightning", (cvar) =>
    {
        World world = World.Current;
        Player player = world?.player;
        LightningData data = world?.SimData?.weatherLightning;
        if (world == null || player == null || data == null)
        {
            Godot.GD.PushWarning("strike_lightning: need a running world, player, and SimData.weatherLightning");
            return;
        }
        var rng = new Godot.RandomNumberGenerator();
        rng.Randomize();
        float maxR = Godot.Mathf.Max(0f, data.weatherSpawnRadius);
        float yaw = rng.RandfRange(0f, Godot.Mathf.Tau);
        float r = Godot.Mathf.Sqrt(rng.Randf()) * maxR;
        Godot.Vector3 query2d = player.GlobalPosition + new Godot.Vector3(Godot.Mathf.Cos(yaw) * r, 0f, Godot.Mathf.Sin(yaw) * r);
        Godot.Vector3 from = query2d + new Godot.Vector3(0f, 80f, 0f);
        Godot.Vector3 to = query2d + new Godot.Vector3(0f, -80f, 0f);
        using var rayQuery = Godot.PhysicsRayQueryParameters3D.Create(from, to);
        rayQuery.CollisionMask = (uint)ECollisionLayer.Solid;
        var result = world.GetWorld3D().DirectSpaceState.IntersectRay(rayQuery);
        Godot.Vector3 strikePos = result.Count > 0 ? (Godot.Vector3)result["position"] : query2d;
        LightningStrike.Create(world, strikePos, data);
    });

    // Debug: dump the current weather state plus the variance prev/cur/next
    // triples and the lightning-gate breakdown. Use to diagnose why a
    // thunderstorm isn't firing — the print shows whether the bottleneck
    // is low simCloud, low simRain, or a fair lightningVariance roll.
    public static CVar weatherProbe = new CVar("weather", (cvar) =>
    {
        WorldState ws = World.Current?.WorldState;
        SkyController sky = SkyController.Current;
        if (ws == null || sky == null)
        {
            Godot.GD.Print("weather: no active world / sky.");
            return;
        }
        WeatherData w = sky.Weather;
        ZoneData zone = sky.Zone;
        SimData sim = ws.SimData;
        if (w == null || sim == null)
        {
            Godot.GD.Print("weather: world/sim not initialized.");
            return;
        }

        float tod = (float)ws.TimeOfDay01;
        float diurnal = WeatherSimulation.DiurnalCurve(tod, sim);
        float diurnalSlope = WeatherSimulation.DiurnalCurveSlope(tod, sim);
        float coolingRate = Godot.Mathf.Max(0f, -diurnalSlope);
        long phase = WeatherSimulation.CurrentPhase(ws.TimeOfDayAbsolute, sim);
        int hpd = WeatherSimulation.HandoversPerDay(sim);
        double nextHandover = ((double)(phase + 1) / hpd) + 0.25;
        double daysUntilHandover = nextHandover - ws.TimeOfDayAbsolute;

        // Three storm-mode gates — match WeatherSimulation.Apply.
        float wetGate = Godot.Mathf.SmoothStep(sim.LightningCloudThreshold, 1f, w.cloudCover)
            * Godot.Mathf.SmoothStep(sim.LightningRainThreshold, 1f, w.rainAmount);
        float dryGate = Godot.Mathf.SmoothStep(sim.DryLightningCloudThreshold, 1f, w.cloudCover)
            * (1f - Godot.Mathf.SmoothStep(0f, sim.DryLightningHumidityMax, w.humidity))
            * Godot.Mathf.SmoothStep(sim.DryLightningTempMin, sim.DryLightningTempMax, w.airTemperature);
        // Elevation: use blended ZoneState if available.
        float elev = SkyController.Current?.ZoneState.Elevation ?? 0f;
        float orographicGate = Godot.Mathf.SmoothStep(sim.OrographicLightningCloudThreshold, 1f, w.cloudCover)
            * Godot.Mathf.SmoothStep(sim.OrographicLightningWindMin, sim.OrographicLightningWindMax, w.windSpeed)
            * Godot.Mathf.SmoothStep(sim.OrographicLightningElevationMin, 1f, elev);
        float gateAny = Godot.Mathf.Max(wetGate, Godot.Mathf.Max(dryGate, orographicGate));
        string winner = wetGate >= dryGate && wetGate >= orographicGate ? "WET"
            : dryGate >= orographicGate ? "DRY" : "OROGRAPHIC";

        Godot.GD.Print("=== weather probe ===");
        Godot.GD.Print($"  time-of-day:    tod={tod:F3} (abs={ws.TimeOfDayAbsolute:F3})  diurnal={diurnal:F3}  slope={diurnalSlope:F3}  coolingRate={coolingRate:F3}");
        Godot.GD.Print($"  phase:          {phase} (next handover in {daysUntilHandover * sim.DayLengthSeconds:F0}s wall time @ time_scale=1)");
        if (zone != null)
        {
            Godot.GD.Print($"  blended zone:   {zone.ResourcePath}");
        }
        Godot.GD.Print($"  SIMULATED (post-Apply, what audio/visuals read):");
        Godot.GD.Print($"    cloudCover     = {w.cloudCover:F3}");
        Godot.GD.Print($"    rainAmount     = {w.rainAmount:F3}");
        Godot.GD.Print($"    lightningAmt   = {w.lightningAmount:F3}{(WeatherSimulation.ForceLightningOverride.HasValue ? "  [FORCED]" : "")}");
        Godot.GD.Print($"    humidity       = {w.humidity:F3}");
        Godot.GD.Print($"    windSpeed      = {w.windSpeed:F2} m/s");
        Godot.GD.Print($"    airTemperature = {w.airTemperature:F1}°F");

        // FOG breakdown — the values that actually drive the volumetric
        // shader, plus the night-dimming diagnostic. fogPhaseScale is
        // SUPPOSED to dim fog at night, but it's fed p.PrimaryIntensity
        // (sun-side only, never drops at night), so this shows whether
        // night fog is rendering at full daytime density.
        DerivedPalette pal = sky.Palette;
        float fogIntensityReference = sim.FogIntensityReference;
        float fogIntensityFloor = sim.FogIntensityFloor;
        // Reconstruct the CURRENT phase scale exactly as WeatherDerivation does.
        float curFactor = Godot.Mathf.SmoothStep(0f, fogIntensityReference, pal.PrimaryIntensity);
        float curPhaseScale = Godot.Mathf.Lerp(fogIntensityFloor, 1f, curFactor);
        // What it WOULD be if the reference used the night-blended intensity.
        float effIntensity = Godot.Mathf.Lerp(pal.PrimaryIntensity, pal.NightPrimaryIntensity, pal.NightT);
        float fixFactor = Godot.Mathf.SmoothStep(0f, fogIntensityReference, effIntensity);
        float fixPhaseScale = Godot.Mathf.Lerp(fogIntensityFloor, 1f, fixFactor);
        // Final values sent to the shader. Authored fog is no longer thinned by
        // the overview's FogVisibilityScale (it only stretches fog_max_distance
        // now); the general haze is thinned by AmbientFogScale instead.
        float ambientFogScale = sky.AmbientFogScale;
        // Sample authored fog_map around the player to see if painted volumes
        // (not ambient haze) are the source of the murk here.
        int maxAuthoredFog = 0;
        Player probePlayer = World.Current?.player;
        if (probePlayer != null)
        {
            Godot.Vector3 pp = probePlayer.GlobalPosition;
            int bx = Godot.Mathf.FloorToInt(pp.X);
            int by = Godot.Mathf.FloorToInt(pp.Y);
            int bz = Godot.Mathf.FloorToInt(pp.Z);
            for (int dx = -3; dx <= 3; dx++)
            {
                for (int dy = -2; dy <= 12; dy++)
                {
                    for (int dz = -3; dz <= 3; dz++)
                    {
                        maxAuthoredFog = Godot.Mathf.Max(maxAuthoredFog, ws.GetFogWorld(bx + dx, by + dy, bz + dz));
                    }
                }
            }
        }
        Godot.GD.Print($"  FOG (what the volumetric shader reads):");
        Godot.GD.Print($"    fog signal           = {pal.Fog:F3}   (post-floor humidity×coolDiurnal)");
        Godot.GD.Print($"    fog_density          = {pal.FogDensity:F4}   (scales painted fog_map)");
        Godot.GD.Print($"    ambient_fog_density  = {pal.AmbientFogDensity * ambientFogScale:F4}   (uniform whole-scene haze, NO height gate)");
        Godot.GD.Print($"    authored fog_map nearby = {maxAuthoredFog}/255   (>0 = painted fog volume present)");
        Godot.GD.Print($"  FOG NIGHT-DIMMING DIAGNOSTIC:");
        Godot.GD.Print($"    PrimaryIntensity (sun-side, used now) = {pal.PrimaryIntensity:F3}");
        Godot.GD.Print($"    NightPrimaryIntensity / NightT        = {pal.NightPrimaryIntensity:F3} / {pal.NightT:F3}");
        Godot.GD.Print($"    fogPhaseScale  CURRENT = {curPhaseScale:F3}   (1.0 = no night dimming)");
        Godot.GD.Print($"    fogPhaseScale  IF FIXED= {fixPhaseScale:F3}   (would scale fog by this/{curPhaseScale:F3} = {(curPhaseScale > 0 ? fixPhaseScale / curPhaseScale : 1f):F2}×)");

        Godot.GD.Print($"  VARIANCE  (prev → cur → next   |   currently displayed)");
        Godot.GD.Print($"    weather    = {ws.WeatherVariancePrev:F3} → {ws.WeatherVarianceCur:F3} → {ws.WeatherVarianceNext:F3}   |  {ws.WeatherVariance:F3}  slope={ws.WeatherVarianceSlope:F3}");
        Godot.GD.Print($"    humidity   = {ws.HumidityVariancePrev:F3} → {ws.HumidityVarianceCur:F3} → {ws.HumidityVarianceNext:F3}   |  {ws.HumidityVariance:F3}");
        Godot.GD.Print($"    cloud      = {ws.CloudVariancePrev:F3} → {ws.CloudVarianceCur:F3} → {ws.CloudVarianceNext:F3}   |  {ws.CloudVariance:F3}");
        Godot.GD.Print($"    lightning  = {ws.LightningVariancePrev:F3} → {ws.LightningVarianceCur:F3} → {ws.LightningVarianceNext:F3}   |  {ws.LightningVariance:F3}");
        Godot.GD.Print($"    (cloud variance is INVERSE: low = cloudier; lightning variance reads through directly)");
        Godot.GD.Print($"  LIGHTNING GATES (3 modes, max wins)  active mode: {winner}");
        Godot.GD.Print($"    WET        = {wetGate:F3}   (cloud × rain — warm humid w/ rain)");
        Godot.GD.Print($"    DRY        = {dryGate:F3}   (cloud × low-humidity × high-temp — desert virga)");
        Godot.GD.Print($"    OROGRAPHIC = {orographicGate:F3}   (cloud × wind × elevation={elev:F2} — mountain ridge)");
        Godot.GD.Print($"    × varianceFactor (lightningVar={ws.LightningVariance:F3}) × lightningMax = simLightning {w.lightningAmount:F3}");
    });

    // Multiplier on the time-of-day advance rate. 1 = SimData.DayLengthSeconds
    // is a real-time day; 60 fast-forwards the cycle 60x for testing sunset /
    // night look without waiting. Does not affect GameTimeMs so player
    // cooldowns, AI timers, etc. stay at real speed.
    public static CVarFloat timeScale = new CVarFloat("time_scale", 1f);

    // Set/read the current normalized time-of-day on the active world.
    // 0 = midnight, 0.25 = sunrise, 0.5 = noon, 0.75 = sunset. Writing wraps
    // into [0, 1). Setting via console jumps the sun/moon orbit immediately.
    public static CVarFloat timeOfDay = new CVarFloat("time_of_day", 0.3f, (cvar) =>
    {
        WorldState ws = World.Current?.WorldState;
        if (ws == null) { return; }
        double v = ((CVarFloat)cvar).Value;
        v -= System.Math.Floor(v);
        // Keep TimeOfDayAbsolute in sync within the current day so the
        // variance phase index lands at the same handover boundary the
        // user is jumping to. Without this, a console time jump would
        // desync the variance phase from the lighting cycle.
        double dayFloor = System.Math.Floor(ws.TimeOfDayAbsolute);
        ws.TimeOfDay01 = v;
        ws.TimeOfDayAbsolute = dayFloor + v;
    });

    // Swap the MainCamera between orthographic and narrow-FOV perspective.
    // Perspective mode is primarily a workaround so Godot's volumetric fog
    // froxel pipeline actually renders — it's known to misbehave / produce
    // nothing under ortho cameras in 4.x. FOV is chosen to roughly match the
    // ortho view extent so gameplay framing stays identical.
    public static CVarBool cameraPerspective = new CVarBool("camera_perspective", false, (cvar) =>
    {
        var client = GameClient.Current;
        if (client != null && client.camera != null)
        {
            client.camera.ApplyProjection(((CVarBool)cvar).Value);
        }
    });

    // Post-process. Vignette cvars feed the post_process canvas_item shader;
    // GameClient pushes them each frame. Pixel-art scale controls the chunky
    // pixel size (linear); 1 disables chunking.
    public static CVarFloat vignetteRadius = new CVarFloat("vignette_radius", 0.55f);
    public static CVarFloat vignetteSoftness = new CVarFloat("vignette_softness", 0.45f);
    public static CVarFloat vignetteStrength = new CVarFloat("vignette_strength", 0.5f);
    public static CVarInt pixelScale = new CVarInt("pixel_scale", 4);

    // Gates the directional motion blur in post_process.gdshader. When false,
    // GameClient zeros motion_blur_strength every frame so the shader skips
    // the blur loop entirely — perceptually identical to "no blur" with no
    // GPU cost. Drives the camera rotation effect today; future fly-up
    // overview should share the same gate.
    public static CVarBool rotationBlur = new CVarBool("rotation_blur", true);

    // Gates the bird's-eye volumetric cloud quad. When false, the cloud
    // mesh stays hidden during the overlook regardless of bird's-eye
    // state — useful on low-spec machines, for screenshotting without
    // clouds in the frame, or when iterating on terrain layout from
    // above without the fog obscuring the view. The bird's-eye state
    // machine and camera lift are unaffected; only the visible cloud
    // layer is suppressed.
    public static CVarBool clouds = new CVarBool("clouds", true);

    // Heat shimmer post-process. When true, HeatField populates a 2D heat
    // texture from ambient air temperature + active WarmthZones each tick;
    // heat_shimmer.gdshader (a fullscreen quad in SceneViewport/MainCamera)
    // warps SCREEN_TEXTURE UVs by a noise field scaled by per-fragment
    // heat. Disabling zeros the field — the pass is then a visual no-op
    // without removing the quad. Tuning lives on heat_shimmer.tres.
    public static CVarBool heatShimmer = new CVarBool("heat_shimmer", true);

    // When true, Mob._PhysicsProcess prints yaw/angular-velocity diagnostics
    // each frame for alive mobs. Used to diagnose yaw oscillation.
    public static CVarBool mobDebugYaw = new CVarBool("mob_debug_yaw", false);

    // When true, Fx prints a line each time it starts an audio
    // player — scene name, stream resource path, and a wall-clock timestamp.
    // Use to diagnose unexpected rapid-fire SFX (e.g. a per-frame land sound
    // when running into a mob): rapid repeats of the same scene name in the
    // log identify the culprit.
    public static CVarBool audioLog = new CVarBool("audio_log", false);

    // When true, prints a line each time WeatherLightningSpawner picks
    // a strike interval, skips a strike (no ground, no data), or fires
    // one. Use to verify the spawner is awake and observe its cadence
    // against the current lightning intensity. Lines look like:
    //   [lightning] intensity=0.18 interval=8.2s
    //   [lightning] FIRE at (12.3, 4.0, -6.1) (intensity=0.21)
    //   [lightning] skip: no ground at (12.3, 0.0, -6.1)
    public static CVarBool lightningLog = new CVarBool("lightning_log", false);

    // When true, draws each alive mob's active path as line segments via
    // DebugDraw — green for upcoming waypoints, yellow for the current
    // segment from the mob to its next waypoint, red dot at the goal.
    // Off by default; toggle from the in-game console.
    public static CVarBool mobDebugPath = new CVarBool("mob_debug_path", false);

    // When true, draws the mob-navigability grid in an 8m radius around the
    // player via DebugDraw — green square = standable dry cell (at its surface
    // Y), orange-tinted square = standable but wall-proximate (charged a
    // wall-avoidance cost so A* prefers roomier cells), cyan = standable water
    // cell, magenta = standable but inside a hazard danger zone (fire trap /
    // campfire / spike trap — wander routes around it, an attacking mob walks
    // in), red cross = column the pathfinder rejects (too little headroom, no
    // surface in range, or the body can't clear the surrounding walls). The
    // grid is sampled with the nearest
    // loaded mob's traversal profile (its actual maxStepHeight / clearance), so
    // walk the dog up to a spot and toggle this to see exactly what its
    // pathfinder sees — the canonical tool for diagnosing "the mob won't path
    // there but the player can walk there." Falls back to a default ground-
    // walker profile when no mob is loaded. Off by default; toggle from the
    // in-game console (`nav_grid 1`).
    public static CVarBool navGridDebug = new CVarBool("nav_grid", false);

    // When true, MobHUD shows a two-line text overlay over each visible mob
    // breaking down PLAYER-perceives-MOB. Top line: V/H/S sense deltas
    // (smell is always 0 — player doesn't smell). Bottom line: L (light at
    // mob), D (distance closeness), F (facing — always 1 for the player),
    // S (mob's speed-based visibility), C (1 - mob camouflage).
    public static CVarBool debugPlayerPerception = new CVarBool("debug_player_perception", false);

    // When true, MobHUD shows the same two-line breakdown for MOB-perceives-
    // PLAYER. Top: V/H/S sense deltas. Bottom: L (player light), D (distance
    // closeness vs mob VisionRange), F (mob's facing dot-power), S (player
    // speed-based visibility), C (1 - player camouflage).
    public static CVarBool debugMobPerception = new CVarBool("debug_mob_perception", false);

    // When true, MobHUD adds a "Pos x,y,z" line to the debug overlay showing
    // each mob's world-space GlobalPosition. Composes with the perception
    // debug cvars: the label appears whenever ANY of the three debug cvars is
    // on. Useful for diagnosing mobs that look mis-aligned vs. terrain (e.g.
    // a mob embedded in the floor whose LOS raycast originates from inside
    // geometry).
    public static CVarBool debugMobPosition = new CVarBool("debug_mob_position", false);

    // When true, every mob is forced into the Discovered perception state for
    // rendering purposes — the sprite shows through walls via the existing
    // X-ray silhouette pass. Perception sim is unchanged (mobs still aggro on
    // their normal rules); this only bypasses the player-side visibility
    // gate so you can see where mobs actually are during debug. Compose with
    // debug_mob_perception to verify LOS state vs. actual visibility.
    public static CVarBool revealMobs = new CVarBool("reveal_mobs", false);

    // When true, Mob._PhysicsProcess prints a diagnostic line each time the
    // torch-conditions block runs — ambientLight, useTorch, discovery state,
    // playerRemembers, and whether _torch / MobData.torch are populated. Use
    // when goblins fail to light their torches to see which gating step is
    // blocking the spawn.
    public static CVarBool mobDebugTorch = new CVarBool("mob_debug_torch", false);

    // Generic CPU section profiler. While `profile` is true, code sections
    // wrapped in Profiler.Section.Begin/End record per-section call count,
    // total time, max single call, and approximate per-frame cost. Run
    // `profile_dump` from the in-game console to print a table and reset
    // the accumulators. The mob hot path (`Mob.*` sections) is the first
    // thing wired up — useful for finding which part of mob update is
    // dominating the frame at high mob counts.
    // Both edges reset accumulators. Turning ON starts a fresh window;
    // turning OFF clears stale numbers so they don't leak into the next
    // session. Use `profile_dump` if you want to print before clearing.
    public static CVarBool profile = new CVarBool("profile", false, (cvar) =>
    {
        Profiler.Reset();
    });

    // Console action: prints the current per-section totals and resets the
    // accumulators. Run `profile_dump` to take a snapshot, then again later
    // to see the delta over a window.
    public static CVar profileDump = new CVar("profile_dump", (cvar) =>
    {
        Profiler.DumpAndReset();
    });

    // Console action: prints the active Fx instance count broken down by
    // source scene. Pair with the `fx_active` engine monitor to identify
    // which scenes account for the headline number — climbing per-scene
    // counts across repeated invocations indicate a leak.
    public static CVar fxDump = new CVar("fx_dump", (cvar) =>
    {
        Fx.DumpActiveByScene();
    });

    // Rolling window length (seconds) for the on-screen overlay and the
    // Godot custom monitors. Every `profile_window` seconds the live
    // accumulators latch into a "previous window" snapshot that the overlay
    // reads, then reset. Smaller = more responsive table, more churn.
    // Larger = more stable averages, slower reaction to scene changes.
    public static CVarFloat profileWindow = new CVarFloat("profile_window", 1f);

    // Cutoff: sections that contribute less than this many ms per frame are
    // hidden from the F3 overlay's profiler table. They still tick and update
    // their custom monitors — they're just suppressed from the on-screen
    // table so it stays short enough to scan. Only applies to the latched
    // overlay path; `profile_dump` and the hitch logger always show every
    // section. Set to 0 to disable the filter.
    public static CVarFloat profileMinPerFrameMs = new CVarFloat("profile_min_per_frame_ms", 0.05f);

    // Hitch logger. While `hitch_log` is true, DiagnosticsOverlay watches
    // per-frame delta and, whenever a frame exceeds `hitch_threshold_ms`,
    // prints the frame time + a Profiler section snapshot to GD.Print and
    // resets the accumulators so the next hitch starts from a clean slate.
    // Forces `profile` on while enabled so the section table has live data.
    // Always-on (does NOT require the F3 overlay to be visible) so hitches
    // can be caught in the wild without the overlay covering the screen.
    public static CVarBool hitchLog = new CVarBool("hitch_log", false);
    public static CVarFloat hitchThresholdMs = new CVarFloat("hitch_threshold_ms", 50f);

    // Bisection toggles for mob render / physics cost. Mob C# work is cheap;
    // when fps tanks at high mob density the cost is in render submission
    // (sprite + shadow draws) or physics (RigidBody3D vs trimesh contacts),
    // both of which happen outside any C# section. Toggle these to find out
    // which side is dominating.
    //
    // mob_shadows 0 → every Mob's LitSprite stops casting shadows. If fps
    //                 recovers, shadow-map draws are the cost (each sprite
    //                 doubles as a shadow-pass draw call).
    public static CVarBool mobShadows = new CVarBool("mob_shadows", true);

    // mob_physics 0 → every Mob freezes and its CollisionLayer/Mask go to 0,
    //                 so the broadphase and contact resolver see nothing. If
    //                 _PhysicsProcess time collapses, Jolt is the cost.
    public static CVarBool mobPhysics = new CVarBool("mob_physics", true);

    // mob_visible 0 → every Mob's mesh subtree is hidden (visible = false).
    //                 The sprite, its shadow proxy, water reflection child,
    //                 and AO decal stop submitting to the renderer. The
    //                 HudAnchor is a sibling and is NOT affected — use the
    //                 separate `mob_hud` toggle for that. If fps recovers
    //                 when this is off, mob render submission for the body
    //                 sprite chain is the dominant cost.
    public static CVarBool mobVisible = new CVarBool("mob_visible", true);

    // mob_hud 0 → every Mob's HudAnchor (which holds the perception meter,
    //              health bar etc.) is hidden. The HUD has its own
    //              visibility contract — it appears while the player is
    //              perceiving but hasn't yet fully discovered the mob — so
    //              this is a separate bisection toggle from mob_visible.
    //              Run with this off to measure how much frame time the
    //              floating HUDs cost vs. the mob bodies themselves.
    public static CVarBool mobHud = new CVarBool("mob_hud", true);

    // mob_footstep_fx 0 → suppress per-stride footstep one-shots from every
    //                     Mob (the dust-puff + footstep audio). Water-enter
    //                     splash, water/tall-grass loops, and the per-mob
    //                     idle/run/swim audio loops are all unaffected so
    //                     this isolates the cost of the per-step burst
    //                     specifically. If fps recovers when this is off,
    //                     Fx.Create + the spawned particle/audio churn is
    //                     where the budget is going.
    public static CVarBool mobFootstepFx = new CVarBool("mob_footstep_fx", true);

    // mob_anim_loop_fx 0 → no Mob spawns the idle/run/swim anim-loop Fx
    //                      (the persistent breathing / footfall audio +
    //                      particle loop tied to the current animation
    //                      state). Existing loops are left to wind down on
    //                      their next state transition. Useful for measuring
    //                      how much of the steady-state cost comes from the
    //                      always-on per-mob audio bed.
    public static CVarBool mobAnimLoopFx = new CVarBool("mob_anim_loop_fx", true);

    // mob_anim_cull 1 (default) → mobs the player can't currently see (no active
    //              line of sight — drawn as memory silhouettes) freeze their
    //              skeletal pose, so Godot skips their per-frame GPU re-skin (the
    //              dominant visible-mob cost). Skinning then scales with VISIBLE
    //              mob count, not total. 0 = every mob animates. The
    //              mob_anim_frozen / mob_anim_active gauges show the split.
    public static CVarBool mobAnimCull = new CVarBool("mob_anim_cull", true);

    // mob_pose_distance > 0 → optional extra animation LOD on top of the LOS cull:
    //              in-sight mobs farther than this many metres also freeze (a
    //              distant moving mob holds its pose — slight moonwalk). 0 = off.
    public static CVarFloat mobPoseDistance = new CVarFloat("mob_pose_distance", 0f);

    // fx_audio 0     → no Fx instance starts its AudioStreamPlayer3D
    //                  children. Particles still play. Distinguishes the
    //                  cost of audio mixing / 3D positional attenuation
    //                  from the cost of particle simulation + draw.
    public static CVarBool fxAudio = new CVarBool("fx_audio", true);

    // fx_particles 0 → no Fx instance enables emission on its
    //                  GpuParticles3D children. Audio still plays. Use
    //                  with fx_audio to bisect the cost of every Fx into
    //                  audio vs particles.
    public static CVarBool fxParticles = new CVarBool("fx_particles", true);

    // motes 0 → the camera-parented dust-mote GpuParticles3D (MoteEffect,
    //           scenes/effects/motes.tscn, 4000 particles) hides itself, so
    //           the renderer skips its per-particle simulation + draw-pass
    //           shader (which samples light_map/cloud several times per speck).
    //           NOT covered by fx_particles — motes are a standalone scene
    //           node, not an Fx. Bisection toggle for the mote cost.
    public static CVarBool motes = new CVarBool("motes", true);

    // sprite_reflections 0 → every LitSprite hides its water-reflection
    //                        child and skips UpdateReflection's water lookup.
    //                        Bisection toggle for the LitSprite.UpdateReflection
    //                        section — at high mob density the reflection
    //                        update can dominate _Process even when the
    //                        sprite is "stationary" (sub-voxel jitter on
    //                        RigidBody3D-anchored sprites used to bust the
    //                        cache; that's now fixed via voxel-keyed caching,
    //                        but this toggle stays so you can still
    //                        attribute frame time to the reflection path
    //                        without recompiling).
    public static CVarBool spriteReflections = new CVarBool("sprite_reflections", true);

    // props_visible 0 → every PropInstance in the world hides itself.
    //                   Static decorations (trees, barrels, grass, etc.)
    //                   are usually the largest contributor to draw counts;
    //                   toggling this off lets you attribute the
    //                   `render_draw_calls` / `process_ms` numbers between
    //                   mobs vs props vs everything else (terrain, HUD,
    //                   decals). Compose with mob_visible to isolate each
    //                   bucket. The toggle drives PropInstance.Visible
    //                   directly so the renderer skips submission entirely.
    public static CVarBool propsVisible = new CVarBool("props_visible", true);

    // Action CVar — dumps WorldPropScatter bucket stats to the console for
    // verifying chunk-eviction is working. Shows per-bucket member count
    // (sprites currently registered) vs the live MultiMesh InstanceCount;
    // mismatches mean a rebuild is pending. Type `props_stats` in the
    // in-game console after walking around to see counts rise/fall.
    public static CVar propsStats = new CVar("props_stats", (cvar) =>
    {
        if (World.Current == null || World.Current.PropScatter == null)
        {
            Godot.GD.Print("props_stats: no active world.");
            return;
        }
        Godot.GD.Print(World.Current.PropScatter.FormatStats());
    });

    // details_visible 0 → every per-chunk detail-sprite scatter
    //                     (MultiMeshInstance3D from ChunkDetailScatter, used
    //                     for grass blades / flowers / pebbles painted on
    //                     terrain) hides itself. These are separate from
    //                     props_visible, which only covers PropInstance and
    //                     TallGrass. Run with details_visible=0 to see how
    //                     many draw calls the painted scatter contributes.
    public static CVarBool detailsVisible = new CVarBool("details_visible", true);

    // Log per-chunk active cell / quad counts from the DC mesher.
    public static CVarBool dcDebug = new CVarBool("dc_debug", false, (cvar) =>
    {
        ChunkMesherDC.DebugLog = ((CVarBool)cvar).Value;
    });

    // Per-axis emission gates for the DC mesher. Used to isolate which axis's
    // quads are wound incorrectly when debugging winding artifacts.
    public static CVarBool dcEmitX = new CVarBool("dc_emit_x", true, (cvar) =>
    {
        ChunkMesherDC.EmitX = ((CVarBool)cvar).Value;
    });
    public static CVarBool dcEmitY = new CVarBool("dc_emit_y", true, (cvar) =>
    {
        ChunkMesherDC.EmitY = ((CVarBool)cvar).Value;
    });
    public static CVarBool dcEmitZ = new CVarBool("dc_emit_z", true, (cvar) =>
    {
        ChunkMesherDC.EmitZ = ((CVarBool)cvar).Value;
    });

    // Baked ambient-occlusion darkening strength on terrain. AO is baked
    // per-vertex into COLOR.a by the DC mesher (sheltered/contact areas);
    // this scales how hard it darkens the diffuse. 0 = off (reproduces the
    // pre-AO look), 1 = authored, >1 exaggerates so you can confirm the bake
    // is landing where you expect. No regen needed — it's a live shader push.
    public static CVarFloat aoStrength = new CVarFloat("ao_strength", 1f, (cvar) =>
    {
        ChunkMesh.SetAoStrength(((CVarFloat)cvar).Value);
    });

    // Contact/directional ambient-occlusion strength on 3D model props (tree
    // trunks, rocks, stumps, chests, statues — everything using model_lit).
    // These imported/baked meshes have no per-vertex AO bake like the terrain,
    // so model_lit reconstructs it in-shader from the model's base height and
    // downward-facing normals. 0 = off (pre-AO look), 1 = authored, >1
    // exaggerates so you can confirm where it lands. Live shader push, no regen.
    public static CVarFloat modelAo = new CVarFloat("model_ao", 1f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("model_ao_strength", ((CVarFloat)cvar).Value);
    });

    // Concavity-driven wetness pooling on terrain. Concavity is baked per-vertex
    // into CUSTOM2.w by the DC mesher (+ = dip, - = bump); dips on near-flat,
    // sky-exposed ground collect MORE of the current surface wetness
    // (WorldState.WetnessLevel) — wetting first and drying last — while bumps and
    // steep faces shed. It scales the real wetness, so dips are dry when the
    // surface is dry. Live shader pushes — no regen needed.
    //   concavity_wetness  = pooling strength. 0 = off (weather-only wetness,
    //                        pre-feature look), 1 = authored, >1 exaggerates.
    //   concavity_threshold = dip depth (voxels) for full pooling. Lower = even
    //                         shallow dips pool; higher = only deep bowls.
    public static CVarFloat concavityWetness = new CVarFloat("concavity_wetness", 1f, (cvar) =>
    {
        ChunkMesh.SetConcavityWetness(((CVarFloat)cvar).Value);
    });
    public static CVarFloat concavityThreshold = new CVarFloat("concavity_threshold", 0.15f, (cvar) =>
    {
        ChunkMesh.SetConcavityThreshold(((CVarFloat)cvar).Value);
    });

    // Debug: paint baked concavity directly on terrain — red = dip, blue =
    // bump, black ≈ flat. Use to confirm the concavity bake lands in bowls /
    // valley floors before judging the (subtler) wetness pooling.
    public static CVarBool debugConcavity = new CVarBool("debug_concavity", false, (cvar) =>
    {
        ChunkMesh.SetDebugConcavity(((CVarBool)cvar).Value);
    });

    // Terrain texture tuning — all live shader pushes, no regen needed.
    //   terrain_tile_scale     = world-to-UV scale (tiling frequency). One PNG
    //                            spans 1/scale world units; higher = finer/more
    //                            repeats. Default = VoxelTypeInfo.TILE_UV_SCALE.
    //   tile_normal_strength   = how hard the per-tile normal map perturbs
    //                            shading (0 = flat lighting, 1 = full).
    //   Blend sharpness in [0,1]: HIGHER = sharper/tighter, LOWER = softer ramp.
    //   wall_blend_sharpness   = cliff↔ground (SHARPEST; the slope seam).
    //   cliff_blend_sharpness  = cliff↔cliff (soft; two rock walls meeting).
    //   ground_blend_sharpness = ground↔ground & overlay-on-ground (soft).
    public static CVarFloat terrainTileScale = new CVarFloat("terrain_tile_scale", VoxelTypeInfo.TILE_UV_SCALE, (cvar) =>
    {
        ChunkMesh.SetTileScale(((CVarFloat)cvar).Value);
    });
    public static CVarFloat tileNormalStrength = new CVarFloat("tile_normal_strength", 0.6f, (cvar) =>
    {
        ChunkMesh.SetTileNormalStrength(((CVarFloat)cvar).Value);
    });
    public static CVarFloat wallBlendSharpness = new CVarFloat("wall_blend_sharpness", 0.65f, (cvar) =>
    {
        ChunkMesh.SetWallBlendSharpness(((CVarFloat)cvar).Value);
    });
    public static CVarFloat cliffBlendSharpness = new CVarFloat("cliff_blend_sharpness", 0.4f, (cvar) =>
    {
        ChunkMesh.SetCliffBlendSharpness(((CVarFloat)cvar).Value);
    });
    public static CVarFloat groundBlendSharpness = new CVarFloat("ground_blend_sharpness", 0.4f, (cvar) =>
    {
        ChunkMesh.SetGroundBlendSharpness(((CVarFloat)cvar).Value);
    });

    // Wetness model tuning (all live shader pushes; see voxel_clip.gdshader).
    //   wet_displacement   = how much the per-tile height map adds micro-pit
    //                        pooling (0 = ignore height, weather-only).
    //   wet_pool_strength  = strength of the standing-water (pooled) state where
    //                        baked concavity + flat + wet say water collects.
    //   wet_roughness_min  = synthesized wet roughness floor (lower = glossier
    //                        glint + sharper sky reflection when wet).
    //   wet_chroma         = extra saturation lift on wet, porous (soil) ground.
    // Per-material porosity (rock reflects vs soil darkens) is authored on
    // BlockData.Porosity, not a CVar.
    public static CVarFloat wetDisplacement = new CVarFloat("wet_displacement", 0.5f, (cvar) =>
    {
        ChunkMesh.SetWetDisplacement(((CVarFloat)cvar).Value);
    });
    public static CVarFloat wetPoolStrength = new CVarFloat("wet_pool_strength", 1.0f, (cvar) =>
    {
        ChunkMesh.SetWetPoolStrength(((CVarFloat)cvar).Value);
    });
    public static CVarFloat wetRoughnessMin = new CVarFloat("wet_roughness_min", 0.25f, (cvar) =>
    {
        ChunkMesh.SetWetRoughnessMin(((CVarFloat)cvar).Value);
    });
    public static CVarFloat wetChroma = new CVarFloat("wet_chroma", 0.2f, (cvar) =>
    {
        ChunkMesh.SetWetChroma(((CVarFloat)cvar).Value);
    });

    // Forces the terrain shader to ignore texture+lighting and output a solid
    // color. Set to "1 0 1" (magenta) to test whether triangles exist at all.
    // Empty string or zero values = disabled.
    public static CVarString debugSolid = new CVarString("debug_solid", "", (cvar) =>
    {
        string s = ((CVarString)cvar).Value.Trim();
        Godot.Vector3 v = Godot.Vector3.Zero;
        if (!string.IsNullOrEmpty(s))
        {
            string[] parts = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3
                && float.TryParse(parts[0], out float r)
                && float.TryParse(parts[1], out float g)
                && float.TryParse(parts[2], out float b))
            {
                v = new Godot.Vector3(r, g, b);
            }
        }
        Godot.RenderingServer.GlobalShaderParameterSet("debug_solid", v);
    });

    // Override terrain + detail-sprite output with per-pixel world-space
    // normals mapped to RGB (R = +X, G = +Y / up, B = +Z), so you can
    // visually verify that sprites and terrain share the same normal at
    // a contact point. Toggle with `debug_normals 1` in the in-game
    // console.
    public static CVarBool debugNormals = new CVarBool("debug_normals", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_normals", ((CVarBool)cvar).Value);
    });

    // Emit raw texture (albedo + sprite ground tint) with NO lighting at
    // all — no sun_lit, block_lit, fill_a_tint, fill_b_tint, cloud_dim, or
    // ATTENUATION. Use this to compare the raw source textures of terrain
    // vs sprites with nothing in between.
    public static CVarBool debugUnlit = new CVarBool("debug_unlit", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_unlit", ((CVarBool)cvar).Value);
    });

    // Disable the fill_a / fill_b "reverse directional" term in both
    // terrain and sprite shaders. Lighting still applies (sun_lit,
    // block_lit, shadows, cloud), but the per-pixel NdotL darkening goes
    // away. If the sprite suddenly matches terrain brightness when this
    // is on, the tint path is the culprit.
    public static CVarBool debugNoTint = new CVarBool("debug_no_tint", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_no_tint", ((CVarBool)cvar).Value);
    });

    // Force cloud_dim to 1.0 everywhere — rules out cloud shadow as a
    // source of brightness mismatch between sprites and terrain.
    public static CVarBool debugNoCloud = new CVarBool("debug_no_cloud", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_no_cloud", ((CVarBool)cvar).Value);
    });

    // Skip the terrain's detail_normal perturbation so its shaded_normal
    // is pure geometry. This is the leading suspect for the "sprite blue
    // on slopes, terrain not" mismatch: detail_normal biases the terrain
    // normal back toward up on ground-facing faces, effectively hiding
    // slopes from the tint path.
    public static CVarBool debugNoDetailNormal = new CVarBool("debug_no_detail_normal", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_no_detail_normal", ((CVarBool)cvar).Value);
    });

    // Override every textured source color (terrain tiles, sprite textures,
    // grass atlas) with pure white before lighting runs — so the rendered
    // pixel equals just the lighting path output (sun_lit * shadow * cloud
    // * tints + block_lit). Use to compare sprites vs terrain vs grass with
    // nothing from the albedo texture in the way: if a sprite reads brighter
    // / dimmer than the terrain at its base with this on, the discrepancy
    // is in the lighting math, not the source texture. Pair with
    // debug_no_tint to also strip the fill_a/fill_b directional tints.
    public static CVarBool debugWhiteAlbedo = new CVarBool("debug_white_albedo", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_white_albedo", ((CVarBool)cvar).Value);
    });

    // Render only the wetness specular contribution on terrain + detail
    // sprites. Output is grayscale: black where the spec term is zero
    // (cave/wall, dry weather, wrong camera angle) and bright where the
    // half-vector highlight lands. Use this to verify wetness_level is
    // reaching the shader and that the camera-angle glint sweep behaves
    // as expected. Toggle with `debug_wet_spec 1` in the in-game console.
    public static CVarBool debugWetSpec = new CVarBool("debug_wet_spec", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_wet_spec", ((CVarBool)cvar).Value);
    });

    // Water reflection debug visualizer. Modes:
    //   0 = normal composite (refraction + water tint + reflection blend)
    //   1 = FORCE full reflection — ignore fresnel, water alpha, refraction;
    //       emit the raw reflection color (sky+SSR mix) so you can see
    //       exactly what the reflection path is sampling. If this comes
    //       up black, the refl direction is aiming at nothing. If it's
    //       sky-gradient, SSR is missing (the rest is the sky fallback).
    //   2 = sky-sample only (no SSR contribution). Shows what
    //       sample_sky_from returns at the refl direction.
    //   3 = SSR only. Black means SSR found no screen-space hit; non-black
    //       means SSR is working but being masked out by fresnel.
    //   4 = fresnel value as grayscale. Bright = high fresnel (reflection
    //       weight near 1). Dark = low fresnel (reflection barely blends).
    //   5 = reflection direction as abs(refl.xyz) → RGB. Tells you where
    //       the reflection ray is actually pointing.
    //   6 = per-fragment normal after ripple perturbation, abs(n) as RGB.
    //   7 = intended sun-disk reflection position. GREEN = refl direction
    //       inside the sun disk threshold (this pixel WILL show the sun in
    //       normal mode). RED heat ramp = refl direction is near the sun
    //       but outside the disk threshold. BLACK = refl pointing away.
    //       If no camera facing produces green, the sun is geometrically
    //       unreachable at current settings — adjust SunMaxElevationDegrees,
    //       time_of_day, or camera pitch until green appears.
    //   8 = caustic pattern isolation (red channel) using the LIVE sample
    //       point (sun-tangent projection of world_vertex). World-anchored.
    //   9 = depth-reconstructed seabed XZ as a tiled gradient (red = X,
    //       green = Z). Visible grid = depth reconstruction working.
    //  10 = caustic_color uniform upload check (flat fill).
    //  11 = caustic pattern using the OLD camera-coupled sample point
    //       (sun-tangent projection of depth-reconstructed seabed_world).
    //       A/B compare against mode 8: if 11 pops/scrolls with camera but
    //       8 stays anchored, the depth-buffer path was the camera coupling.
    //  12 = sun_uv visualization for the LIVE path — fract gradient on the
    //       actual coords the noise is sampled at. Should stay world-anchored
    //       as the camera moves.
    //  13 = TIME rollover detector. Red ramps 0→1 every 10 seconds. If it
    //       snaps back to 0 mid-ramp, Godot TIME rolled over (default at
    //       3600s, configurable via rendering/limits/time/time_rollover_secs).
    //  14 = caustic pattern at a FIXED sun_uv. Strips out spatial variation
    //       so any visible pop here is purely time-source.
    public static CVarInt waterDebug = new CVarInt("water_debug", 0, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("water_debug_mode", ((CVarInt)cvar).Value);
    });

    // Ceiling-cap pipeline debug visualizer. Each mode replaces a single
    // shader's output with a flat bright color so you can see exactly
    // which shader is drawing what at any pixel. Use to track down "X is
    // showing through the cutaway" artifacts.
    //   0 = off (normal render)
    //   1 = water cap (water_clip_cap.gdshader) → bright MAGENTA wherever it
    //       draws. Anything that's still wrong-looking after enabling this
    //       and seeing magenta over it is being drawn BY the water cap.
    //   2 = ceiling cap (clip_cap.gdshader) → bright RED.
    //   3 = water backface stencil (voxel_water_backface.gdshader) → adds
    //       CYAN wherever stencil=2 is being written. Shows the screen
    //       zone the water cap is allowed to draw in.
    //   5 = water cap disabled entirely. If the artifact disappears, the
    //       water cap was drawing it. If it persists, look elsewhere.
    //   6 = ceiling cap disabled entirely.
    //   7 = voxel_water front face (voxel_water.gdshader) → bright YELLOW.
    //       Shows where actual water voxel surfaces (not the cap) draw.
    //   8 = voxel_water clip-line predicate viz. RED = fragment evaluates
    //       `world_vertex.y > camera_clip` (would have been discarded).
    //       GREEN = below clip (legitimate).
    //   9 = voxel_water `world_vertex.y` as grayscale (mod 16).
    //  10 = voxel_water `camera_clip` global as grayscale (mod 16).
    //  11 = voxel_water face-type visualizer. CYAN = top, MAGENTA =
    //       bottom, YELLOW = side. For diagnosing water poke-through.
    // For inspecting the ceiling cap's mask coverage directly (the
    // SubViewport-rendered black/white silhouette), use `cap_mask_debug 1`
    // instead — that draws the mask texture as a fullscreen overlay.
    public static CVarInt clipDebug = new CVarInt("clip_debug", 0, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("clip_debug_mode", ((CVarInt)cvar).Value);
    });

    // Enable/disable the block-light shadow projector (a top-down
    // SubViewport that renders sprite silhouettes; lit shaders sample it
    // and dim their block_lit term accordingly). When false, the
    // projector's render target update mode goes Disabled (no render
    // pass) and the `block_light_shadow_enabled` shader global goes false
    // so lit shaders skip the texture sample entirely and write block_lit
    // to EMISSION at full strength. Rendering is byte-identical to
    // pre-feature when off — the low-spec graphics-settings toggle.
    public static CVarBool blockLightShadow = new CVarBool("block_light_shadow", true, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("block_light_shadow_enabled", ((CVarBool)cvar).Value);
    });

    // ground_stain 0 -> the GroundStainProjector stops rendering and the lit
    // ground shaders branch around the stain sample, so scorch/footprint/blood
    // marks vanish and terrain renders byte-identical to pre-feature. Perf
    // bisection toggle + a quick way to confirm a visual issue is the stain
    // layer vs the underlying terrain.
    public static CVarBool groundStain = new CVarBool("ground_stain", true, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("ground_stain_enabled", ((CVarBool)cvar).Value);
    });

    // Discards every voxel_water fragment when set. Lets you check the
    // terrain stencil + cap pipeline without water front faces in the
    // way — particularly useful with `clip_debug 13` to see which screen
    // pixels get stencil writes vs which are bare scene.
    public static CVarBool waterHide = new CVarBool("water_hide", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("water_hide", ((CVarBool)cvar).Value);
    });

    // DIAGNOSTIC (water-vanish investigation): when true, ChunkManager prints a
    // line every time a water-bearing chunk streams in or out, with the game
    // time-of-day and the loaded chunk count. If an outdoor-water "vanish"
    // coincides with an "UNLOAD water chunk" line (and recovery with a matching
    // "LOAD"), the cause is the chunk streaming out — not the water shader/mesh.
    // If the water vanishes with NO unload line, the chunk stayed resident and
    // the cause is water-specific (shader/material/mesh), not streaming.
    public static CVarBool chunkWaterLog = new CVarBool("chunk_water_log", false);

    // Debug visualizer for sprite_prop_reflection_multimesh.gdshader. Replaces
    // the reflection sprite's ALBEDO with diagnostic values to track down
    // why ripple shimmer / tint / etc. is or isn't producing visible output.
    //   0 = off (normal reflection rendering)
    //   1 = ripple normal tilt (red = +X tilt, green = +Z tilt). Should
    //       wave/scroll over time. Solid black = ripple_strength is 0
    //       OR the ripple_tex_a/b sample is returning flat normals.
    //   2 = computed water-surface XZ visualization (fract of XZ * 0.1).
    //       Should show a tiled gradient that drifts as camera moves.
    //       Solid color = surface_xz reconstruction broken.
    //   3 = source_above_water (height of represented source pixel above
    //       water, in meters). Red ramps 0..world_h+aboveWaterOffset over
    //       the reflection. Black = source is at/below water.
    //   4 = path_len (surface→source ray length given camera pitch).
    //       Red ramps 0..big. Bigger means more ripple shift expected.
    //   5 = raw shift_world_xz magnitude before sprite-basis projection.
    //   6 = final jitter values applied to tex_coord (R = jitter_u, G =
    //       jitter_v, in source pixels — 0..many). If this is non-zero
    //       but the rendered reflection still looks unrippled, the
    //       integer floor(sxy + jitter) clamp is eating the displacement.
    //   7 = ripple_strength as a flat shade.
    public static CVarInt reflectionDebug = new CVarInt("reflection_debug", 0, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("reflection_debug_mode", ((CVarInt)cvar).Value);
    });

    // Force the water surface to use a flat +Y normal — disables the
    // ripple texture's contribution to the shading normal. Reflections
    // become a perfect mirror of the sky/world in that view direction.
    // Use with water_debug 1 to check if ripples are scattering the
    // reflection away from the sun.
    public static CVarBool waterDisableRipples = new CVarBool("water_disable_ripples", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("water_disable_ripples", ((CVarBool)cvar).Value);
    });

    // Master toggle for per-cell water currents. When false, the water
    // shader skips the current sample entirely and ripple_normal falls
    // back to the wind-only single-sample path.
    public static CVarBool waterCurrentsEnabled = new CVarBool("water_currents_enabled", true, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("water_currents_enabled", ((CVarBool)cvar).Value);
    });

    // World m/s of surface drift at the maximum stored current value
    // (signed 1.0). Storage is normalized to [-1, 1] so this CVar tunes
    // the global magnitude without re-baking chunks.
    public static CVarFloat waterCurrentSpeed = new CVarFloat("water_current_speed", 1.0f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("water_current_speed", ((CVarFloat)cvar).Value);
    });

    // Seconds-per-cycle for the ripple texture's two-phase scroll. Longer
    // = less obvious "lerp wobble" between phases, but more visible UV
    // stretching mid-phase. Sub-second values pump too fast; ~2s reads
    // as continuous drift.
    public static CVarFloat waterCurrentPhasePeriod = new CVarFloat("water_current_phase_period", 2.0f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("water_current_phase_period", ((CVarFloat)cvar).Value);
    });

    // Multiplier on the wind-map vector field driving the global
    // GpuParticlesAttractorVectorField3D. The attractor's force scales by
    // wind_velocity_scale × this, so each particle's existing `damping`
    // (drag coefficient) determines its steady-state response — low-damping
    // particles (embers, dust) drift far in wind and high-damping particles
    // (blood, debris) barely budge — physically intuitive without any
    // per-effect authoring. Polled each frame by ChunkManager so live
    // tweaks via the in-game console take effect immediately. 0 = wind has
    // no effect on particles.
    public static CVarFloat particleWindStrength = new CVarFloat("particle_wind_strength", 0.15f);

    // Disable all sprite-based water reflections (the flipped child sprites
    // LitSprite spawns under water surfaces). Doesn't tear down the
    // reflection nodes — just zeroes the global reflection_tint that
    // sprite_reflection.gdshader multiplies the output by, so reflections
    // collapse to black/invisible. Set back to false to restore. Useful
    // for measuring perf cost of reflections, isolating render bugs, or as
    // a low-end graphics setting.
    public static CVarBool spriteReflectionsDisabled = new CVarBool("sprite_reflections_disabled", false, (cvar) =>
    {
        // The actual reflection_tint value is pushed every frame by
        // SkyController.Apply(); checking this flag there gates the push.
    });

    // Render the sun disk as a pure magenta circle in the sky (and thus in
    // the water reflection), bypassing cloud occlusion, sun tint, intensity
    // scaling, and horizon gating. A smoke test: if both the sky dome and
    // water reflection show a magenta disk, the geometry + thresholds are
    // correct and any invisibility in normal mode is a tint/cloud/intensity
    // issue. If no magenta appears anywhere, the refl direction never meets
    // sun_disk_outer at the current settings.
    public static CVarBool skyDebugSunDisk = new CVarBool("sky_debug_sun_disk", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("sky_debug_sun_disk", ((CVarBool)cvar).Value);
    });

    // Power applied to the lightmap value in voxel/sprite/water shaders.
    // 1.0 = linear (raw BFS value), >1 darkens the mid-range so dim sunlight
    // bleed reads as proper darkness while bright areas stay bright.
    public static CVarFloat lightFalloffExp = new CVarFloat("light_falloff_exp", 2f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("light_falloff_exp", ((CVarFloat)cvar).Value);
    });

    // Three independent gates for the tree_lit shader (the maple/canopy tree
    // material). Each is a 0..1 strength; 0 disables that pass entirely, 1 is
    // the authored intent, partial values blend in for A/B comparison. The
    // shader gates each pass on its own strength so the three combine without
    // any compile-time switches — toggling any of them at runtime is a single
    // RenderingServer push and a uniform branch in the shader.
    //
    // tree_wind 0 → canopy verts stop swaying in the vertex shader (still
    // displaced by zero, which is free; the work that's gone is the sin/cos
    // pair and the mask multiply).
    public static CVarFloat treeWind = new CVarFloat("tree_wind", 1f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("tree_wind_strength", ((CVarFloat)cvar).Value);
    });
    // tree_sphere_normal 0 → canopy shading uses the real (faceted) mesh
    // normal instead of a radial-from-canopy-center fake. The crown reads as
    // a low-poly polyhedron rather than a soft blob.
    public static CVarFloat treeSphereNormal = new CVarFloat("tree_sphere_normal", 1f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("tree_sphere_normal_strength", ((CVarFloat)cvar).Value);
    });
    // tree_detail_noise 0 → canopy albedo isn't modulated by the per-pixel
    // 3D value-noise; surfaces revert to the flat atlas color.
    public static CVarFloat treeDetailNoise = new CVarFloat("tree_detail_noise", 1f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("tree_detail_noise_strength", ((CVarFloat)cvar).Value);
    });
    // tree_silhouette_breakup 0 → no canopy pixels are discarded; the
    // polygonal silhouette is solid. 1 → the authored amount
    // (silhouette_breakup_amount in tree_lit.gdshader, default 0.12) is
    // applied, punching ~12% of canopy pixels out along the noise's dark
    // spots. Slides up past 1 (engine doesn't clamp) for progressive
    // seasonal leaf loss — at high values the canopy thins to almost
    // nothing.
    public static CVarFloat treeSilhouetteBreakup = new CVarFloat("tree_silhouette_breakup", 1f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("tree_silhouette_breakup_strength", ((CVarFloat)cvar).Value);
    });

    // RGB tint applied to the sun visibility mask. Parsed from "r g b" floats;
    // the shader sees a vec3.
    private static Godot.Vector3 _sunColorValue = new Godot.Vector3(1f, 0.96f, 0.88f);
    public static Godot.Vector3 SunColor => _sunColorValue;
    public static CVarString sunColor = new CVarString("sun_color", "1 0.96 0.88", (cvar) =>
    {
        string s = ((CVarString)cvar).Value.Trim();
        string[] parts = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3
            && float.TryParse(parts[0], out float r)
            && float.TryParse(parts[1], out float g)
            && float.TryParse(parts[2], out float b))
        {
            _sunColorValue = new Godot.Vector3(r, g, b);
            Godot.RenderingServer.GlobalShaderParameterSet("sun_color", _sunColorValue);
        }
    });

    // When set to "x y z", the ChunkMesh only builds that single chunk (others
    // produce no geometry). Useful for isolating a chunk when debugging DC.
    // Empty string = all chunks build normally.
    public static CVarString debugOnlyChunk = new CVarString("debug_only_chunk", "", (cvar) =>
    {
        string s = ((CVarString)cvar).Value.Trim();
        if (string.IsNullOrEmpty(s))
        {
            ChunkMesh.OnlyChunkFilter = null;
            return;
        }
        string[] parts = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3
            && int.TryParse(parts[0], out int x)
            && int.TryParse(parts[1], out int y)
            && int.TryParse(parts[2], out int z))
        {
            ChunkMesh.OnlyChunkFilter = new Godot.Vector3I(x, y, z);
        }
    });

    // Prints the voxel + light state at the player's current voxel and the
    // 5 voxels above it. Use to verify whether a "dark" cave actually has 0
    // sunlight (or is being lit by lateral BFS through some opening).
    public static CVar lightProbe = new CVar("light_probe", (cvar) =>
    {
        if (World.Current == null || World.Current.player == null)
        {
            Godot.GD.Print("light_probe: no active world / player.");
            return;
        }
        Godot.Vector3 p = World.Current.player.GlobalPosition;
        int px = Godot.Mathf.FloorToInt(p.X);
        int py = Godot.Mathf.FloorToInt(p.Y);
        int pz = Godot.Mathf.FloorToInt(p.Z);
        WorldState ws = World.Current.WorldState;
        Godot.GD.Print($"light_probe at ({px},{py},{pz}):");
        for (int dy = 0; dy <= 5; dy++)
        {
            int wy = py + dy;
            VoxelType v = ws.GetVoxelWorld(px, wy, pz);
            int sun = ws.GetSunlightWorld(px, wy, pz);
            ws.GetBlockLightWorld(px, wy, pz, out int br, out int bg, out int bb);
            Godot.GD.Print($"  y={wy}: voxel={v} sun={sun} block=({br},{bg},{bb})");
        }
    });

    // Scans a 30x30 area around the player at the player's voxel-Y and prints
    // any column where the air voxel directly above the player's Y is sunlit
    // (sun > 0). Use to locate the opening that's leaking sunlight into a
    // supposedly-sealed cave.
    public static CVar lightLeak = new CVar("light_leak", (cvar) =>
    {
        if (World.Current == null || World.Current.player == null)
        {
            Godot.GD.Print("light_leak: no active world / player.");
            return;
        }
        Godot.Vector3 p = World.Current.player.GlobalPosition;
        int px = Godot.Mathf.FloorToInt(p.X);
        int py = Godot.Mathf.FloorToInt(p.Y);
        int pz = Godot.Mathf.FloorToInt(p.Z);
        WorldState ws = World.Current.WorldState;
        const int RADIUS = 15;
        int playerSun = ws.GetSunlightWorld(px, py, pz);
        Godot.GD.Print($"light_leak around ({px},{py},{pz}) playerSun={playerSun}: scanning air at y={py} with sun > player.sun, sorted by distance");
        var hits = new System.Collections.Generic.List<(int dist, int wx, int wz, int sun)>();
        for (int dx = -RADIUS; dx <= RADIUS; dx++)
        {
            for (int dz = -RADIUS; dz <= RADIUS; dz++)
            {
                int wx = px + dx, wz = pz + dz;
                VoxelType v = ws.GetVoxelWorld(wx, py, wz);
                if (v != VoxelType.Air) { continue; }
                int sun = ws.GetSunlightWorld(wx, py, wz);
                if (sun > playerSun)
                {
                    hits.Add((System.Math.Abs(dx) + System.Math.Abs(dz), wx, wz, sun));
                }
            }
        }
        hits.Sort((a, b) => a.dist.CompareTo(b.dist));
        int show = System.Math.Min(20, hits.Count);
        for (int i = 0; i < show; i++)
        {
            var h = hits[i];
            Godot.GD.Print($"  dist={h.dist} ({h.wx},{py},{h.wz}) sun={h.sun}");
        }
        if (hits.Count == 0) { Godot.GD.Print("  no brighter air found in radius."); }
    });

    // Prints the sampled air temperature (°F) at the player's current
    // position, broken into base + sun contribution. Mirrors what
    // GameClient.SampleAirTemperature returns each frame so you can verify
    // weather / sun-shading / fog interactions match the gameplay sample.
    public static CVar tempProbe = new CVar("temp", (cvar) =>
    {
        if (World.Current == null || World.Current.player == null)
        {
            Godot.GD.Print("temp: no active world / player.");
            return;
        }
        GameClient client = GameClient.Current;
        if (client == null)
        {
            Godot.GD.Print("temp: no active GameClient.");
            return;
        }
        Godot.Vector3 p = World.Current.player.GlobalPosition;
        World.AirTemperatureSample s = World.Current.SampleAirTemperatureBreakdown(p);
        Godot.GD.Print(
            $"temp at ({p.X:F1}, {p.Y:F1}, {p.Z:F1}): {s.Total:F1}°F\n" +
            $"  air        = {s.air:F1}°F\n" +
            $"  sun        = +{s.SunContribution:F1}°F  (sunT {s.sunTemperature:F1} × sunFactor {s.sunFactor:F2} × skyTransmission {s.skyTransmission:F2} × sunMask {s.sunMask:F2})\n" +
            $"  cloudCover = {s.cloudCover:F2}   fog = {s.fog:F2}");
    });

    // Prints the player's current world position and chunk coord.
    public static CVar whereAmI = new CVar("where", (cvar) =>
    {
        if (World.Current == null || World.Current.player == null)
        {
            Godot.GD.Print("where: no active world / player.");
            return;
        }
        Godot.Vector3 p = World.Current.player.GlobalPosition;
        Godot.Vector3I c = World.WorldToChunkCoord(p);
        Godot.GD.Print($"player pos=({p.X:F1}, {p.Y:F1}, {p.Z:F1})  chunk=({c.X}, {c.Y}, {c.Z})");
    });

// When non-empty, Main runs WorldGen on the default WorldGenData at
    // startup (bypassing the main menu), dumps plateau/height/ramp PPMs +
    // stats.txt to this directory, and quits. Use with `--headless` for a
    // fast-feedback debugging loop over the height-field algorithm.
    public static CVarString worldgenDebugDump = new CVarString("worldgen_debug_dump", "");

    // Console command: dumps the most recently generated world's plateau/
    // height fields to user://worldgen_debug (outside the project tree).
    // Useful when a game is already running and you want a snapshot without
    // restarting.
    public static CVar worldgenDebug = new CVar("worldgen_debug", (cvar) =>
    {
        WorldGen.DumpDebug(Godot.ProjectSettings.GlobalizePath("user://worldgen_debug"));
    });

// Path to a packed world file (`.hike`). When non-empty at game start,
    // Main loads the world from this path instead of running WorldGen.
    public static CVarString worldFile = new CVarString("world_file", ""); // user://world.hike

    // Action: writes the currently-loaded WorldState to disk at the given
    // path. Useful for converting a WorldGen-generated world into a packed
    // file for testing the disk loader before the custom editor exists.
    // Usage: `world_export user://world.hike`
    public static CVarString worldExport = new CVarString("world_export", "", (cvar) =>
    {
        string path = ((CVarString)cvar).Value;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        if (World.Current == null)
        {
            Godot.GD.PrintErr("world_export: no active world (start a game first).");
            return;
        }
        try
        {
            WorldFile.Write(path, World.Current.WorldState);
            Godot.GD.Print($"world_export: wrote {path}");
        }
        catch (System.Exception e)
        {
            Godot.GD.PrintErr($"world_export failed: {e.Message}");
        }
    });

    // Subscene authoring commands. All require an active WorldEditor — they
    // no-op (with an error log) outside editor mode. The editor maintains
    // the corner selection; these CVars are just the console surface.
    //
    // Workflow:
    //   1. Move the editor cursor (WASD + EditorUp/Down) to corner A; run
    //      `subscene_corner` to capture it.
    //   2. Move to corner B; run `subscene_corner` again.
    //   3. Run `subscene_save user://cottage.hikescene` (or the _env
    //      variant for castles/dungeons that need to override Wind/EnvTag).
    //   4. To stamp into the same world: move the cursor, run
    //      `subscene_stamp <path>`. The cursor position is the placement
    //      anchor — the subscene's bbox-min lands there (anchor defaults
    //      to (0,0,0) at save time).
    public static CVar subsceneCorner = new CVar("subscene_corner", (cvar) =>
    {
        if (WorldEditor.Current == null)
        {
            Godot.GD.PrintErr("subscene_corner: no active editor.");
            return;
        }
        WorldEditor.Current.MarkSubsceneCorner();
    });

    public static CVar subsceneCornerClear = new CVar("subscene_corner_clear", (cvar) =>
    {
        if (WorldEditor.Current == null)
        {
            Godot.GD.PrintErr("subscene_corner_clear: no active editor.");
            return;
        }
        WorldEditor.Current.ClearSubsceneCorners();
    });

    public static CVarString subsceneSave = new CVarString("subscene_save", "", (cvar) =>
    {
        string path = ((CVarString)cvar).Value;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        if (WorldEditor.Current == null)
        {
            Godot.GD.PrintErr("subscene_save: no active editor.");
            return;
        }
        WorldEditor.Current.SaveSubscene(path, includeEnv: false);
    });

    public static CVarString subsceneSaveEnv = new CVarString("subscene_save_env", "", (cvar) =>
    {
        string path = ((CVarString)cvar).Value;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        if (WorldEditor.Current == null)
        {
            Godot.GD.PrintErr("subscene_save_env: no active editor.");
            return;
        }
        WorldEditor.Current.SaveSubscene(path, includeEnv: true);
    });

    public static CVarString subsceneStamp = new CVarString("subscene_stamp", "", (cvar) =>
    {
        string path = ((CVarString)cvar).Value;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        if (WorldEditor.Current == null)
        {
            Godot.GD.PrintErr("subscene_stamp: no active editor.");
            return;
        }
        WorldEditor.Current.StampSubscene(path);
    });
}