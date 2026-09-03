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
        Player player = Sim.Current?.player;
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

    // Logs MusicManager piece transitions (which top-level track crossfades in,
    // and why) so dynamic-music issues can be diagnosed from console.
    public static CVarBool musicDebug = new CVarBool("music_debug", false);

    // Logs safety-zone events — player enter/exit of a zone (IsSafe flips) and
    // mob Retreat behavior entries — so pacify / disengage issues can be
    // diagnosed from console.
    public static CVarBool safetyDebug = new CVarBool("safety_debug", false);

    // Logs every stage of the world editor's entity pick — mode, Ctrl detection,
    // how many entities were considered / visible / had usable bounds / were hit
    // by the cursor ray, and how many line segments reached DebugDrawRenderer —
    // so a Ctrl-hover that draws no box can be traced to the stage that dropped
    // it rather than guessed at. Also draws a fixed test box at the cursor, which
    // isolates "DebugDraw doesn't render here" from "the pick found nothing".
    public static CVarBool editorPickDebug = new CVarBool("editor_pick_debug", false);

    // Outlines the world editor's invisible voxel markers around the edit
    // cursor — cyan for Opening (doorway / window void), orange for Barrier
    // (sightless, lightless solid). Neither type draws any geometry of its own,
    // so this is the only way to see what has already been marked. On by
    // default; turn it off (`editor_markers 0`) when the outlines get in the way
    // of reading the geometry underneath.
    public static CVarBool editorMarkerOverlay = new CVarBool("editor_markers", true);

    // Periodically logs each dangerous hostile near the player with the factors
    // the interactive danger gate reads (Sim.IsDangerNear) — distance, behavior,
    // composed EBehaviorFlags, IsEngaging, and clear-line-to-player — so a stuck
    // "Danger Nearby" with nothing on screen can be traced to the exact mob.
    public static CVarBool dangerDebug = new CVarBool("danger_debug", false);

    // Debug: drops a Treasure Map as loot at the player's feet so the pickup →
    // reveal → dig flow can be exercised without hunting zone chests.
    public static CVar spawnTreasureMap = new CVar("spawn_treasure_map", (cvar) =>
    {
        Sim sim = Sim.Current;
        Player player = sim?.player;
        if (sim == null || player == null)
        {
            return;
        }
        ItemData map = Godot.GD.Load<ItemData>("res://resources/data/items/consumables/treasure_map_hub.tres");
        if (map != null)
        {
            sim.SpawnLoot(player.GlobalPosition + Godot.Vector3.Up * 0.5f, Godot.Vector3.Up * 2.5f, map);
        }
    });

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
    // Recompute the whole world's sun field from the live voxels and occluders.
    // Loading does NOT do this — a .hike carries baked sunlight and is trusted
    // (see LightEngine.LIGHT_VERSION) — so this is the tool for a hand-authored
    // world whose light predates a change to the lighting pipeline. Seconds, not
    // milliseconds: it is the same full-world pass worldgen ends on.
    public static CVar worldRelight = new CVar("relight", (cvar) =>
    {
        WorldState world = Sim.Current?.WorldState;
        if (world == null)
        {
            Godot.GD.Print("relight: no world loaded");
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        FoliageStamper.Stamp(world);
        LightEngine.Relight(world);
        Sim.Current?.ChunkManager?.RebuildAllChunkMeshes();
        Godot.GD.Print($"relight: {sw.ElapsedMilliseconds}ms");
    });

    // Bake a world-map document to a .hike with no painter and no window:
    //   worldmap_bake res://.../default_world_map.tres [res://out.hike]
    // Runs the same three steps Ctrl+S does (build, occluder stamp, relight +
    // write), straight through on this thread. The optional second argument
    // overrides the document's authored outputWorldPath IN MEMORY ONLY, so a
    // test bake cannot overwrite the real world. Minutes on a large document.
    public static CVarString worldMapBake = new CVarString("worldmap_bake", "", (cvar) =>
    {
        string arg = ((CVarString)cvar).Value;
        if (string.IsNullOrEmpty(arg) || arg == "?")
        {
            Godot.GD.Print("worldmap_bake <WorldMapData.tres> [output.hike]");
            return;
        }
        string[] parts = arg.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var data = Godot.GD.Load<WorldMapData>(parts[0]);
        if (data == null)
        {
            Godot.GD.PrintErr($"worldmap_bake: could not load '{parts[0]}'");
            return;
        }
        if (parts.Length > 1)
        {
            data.outputWorldPath = parts[1];
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Report what actually happened. The write is the LAST thing a bake does
        // and the most likely thing to fail (a running game or editor holds the
        // .hike open), so printing the elapsed time unconditionally announces a
        // world that was never written — and the blob left on disk is the stale
        // one from whenever the last bake succeeded.
        bool ok = data.BakeToWorldFile();
        if (!ok)
        {
            Godot.GD.PrintErr($"worldmap_bake: FAILED after {sw.ElapsedMilliseconds}ms, "
                + $"'{data.outputWorldPath}' NOT written (see the error above; if it is a file lock, "
                + "check for a running game or editor, or a stray headless Godot process)");
            return;
        }
        Godot.GD.Print($"worldmap_bake: {sw.ElapsedMilliseconds}ms -> {data.outputWorldPath}");
    });

    // Build the initial world fill's ~535 chunk geometries on the thread pool
    // rather than one at a time (ChunkMesh.BuildGeometry / Realize). Off is the
    // A/B, and is also how you PROFILE the fill: the per-section timers are
    // main-thread state, so the sections inside a parallel build record nothing.
    public static CVarBool chunkParallelFill = new CVarBool("chunk_parallel_fill", true);

    // Mesher sampling lattice (see Density.cs / ChunkMesherDC.cs).
    //   false — voxel CORNERS, min-rule. Dilates the solid phase by one voxel,
    //           so 1-voxel-thin AIR features (doorways, arrow slits, narrow
    //           tunnels) produce no sign change and mesh over solid.
    //   true  — voxel CENTRES, one sign per voxel. Thin air and thin solid both
    //           survive, and SharpAxes.All cells place their vertex on a
    //           voxel-grid corner, so stone reads as a true cubic mesh.
    // Toggling requeues every loaded chunk. Flat Y-snapped ground is unchanged
    // between the two; slopes, inside corners, and anything currently welded
    // shut by the dilation will move.
    public static CVarBool voxelCenterSampling = new CVarBool("voxel_center_sampling", true, (cvar) =>
    {
        Sim.Current?.ChunkManager?.RebuildAllChunkMeshes();
    });

    // Global multiplier on every block's authored BlockSurfaceData.edgeRoughness, for
    // dialling the look in live. 0 disables the carve entirely and restores
    // ruler-straight authored surfaces. Requeues every loaded chunk.
    public static CVarFloat voxelEdgeRoughness = new CVarFloat("voxel_edge_roughness", 1f, (cvar) =>
    {
        Sim.Current?.ChunkManager?.RebuildAllChunkMeshes();
    });

    // The DC mesher's two SHADING smoothers, live for A/B. Both average a
    // riser's vertex normals into the treads either side of it, which is what
    // stops a terraced ramp from lighting-banding — and also what decides
    // whether a SHORT wall can reach the wall tile at all (a 2-voxel riser is
    // all lip: measured normal.y 0.24 with these off, 0.61 with them on, against
    // a wallBand that starts at 0.3–0.4). Neither touches the emitted geometry,
    // silhouette or collision; the cliff face is vertical either way.
    // Requeues every loaded chunk. Measure with mesher_probe / mesher_sweep.
    public static CVarInt mesherVertRelax = new CVarInt("mesher_vert_relax", 2, (cvar) =>
    {
        ChunkMesherDC.VERT_RELAX_ITERATIONS = ((CVarInt)cvar).Value;
        Sim.Current?.ChunkManager?.RebuildAllChunkMeshes();
    });

    public static CVarInt mesherNormalSmooth = new CVarInt("mesher_normal_smooth", 1, (cvar) =>
    {
        ChunkMesherDC.NORMAL_SMOOTH_ITERATIONS = ((CVarInt)cvar).Value;
        Sim.Current?.ChunkManager?.RebuildAllChunkMeshes();
    });

    // Minimum alignment for a neighbour to pull on a cell's normal (and on its
    // relaxed position) — the crease gate. Raised from 0.5 to 0.8 so it fires on
    // a short riser, whose lips sit only ~45 degrees away (dot ~0.7); at 0.5 a
    // 2-voxel wall's normal was averaged up to 0.61 and never reached the
    // wallBand, and 0.95 rather than 0.8 so DIAGONAL edges (whose lips align far
    // better with the face) get the same treatment. Ramp banding is unchanged or
    // slightly better. Keep in sync with
    // ChunkMesherDC.NORMAL_SMOOTH_MIN_DOT — this callback only fires on a SET,
    // so the field's own initializer is what a fresh session runs with.
    public static CVarFloat mesherNormalMinDot = new CVarFloat("mesher_normal_min_dot", 0.95f, (cvar) =>
    {
        ChunkMesherDC.NORMAL_SMOOTH_MIN_DOT = ((CVarFloat)cvar).Value;
        Sim.Current?.ChunkManager?.RebuildAllChunkMeshes();
    });

    // How strongly a cell keeps its own normal versus its neighbours'. Higher
    // preserves more local shape.
    public static CVarFloat mesherNormalSelfWeight = new CVarFloat("mesher_normal_self_weight", 2f, (cvar) =>
    {
        ChunkMesherDC.NORMAL_SMOOTH_SELF_WEIGHT = ((CVarFloat)cvar).Value;
        Sim.Current?.ChunkManager?.RebuildAllChunkMeshes();
    });

    public static CVar mesherProbe = new CVar("mesher_probe", (cvar) => MesherProbe.Run());
    public static CVar mesherSweep = new CVar("mesher_sweep", (cvar) => MesherProbe.Sweep());
    public static CVar mesherWallSweep = new CVar("mesher_wall_sweep", (cvar) => MesherProbe.WallSweep());
    public static CVar mesherStepTexture = new CVar("mesher_step_texture", (cvar) => MesherProbe.StepTexture());
    public static CVar mesherProbeMaterial = new CVar("mesher_probe_material", (cvar) => MesherProbe.MaterialRegistration());

    // Dump the shape-channel decision for a patch of world so a stepped slope
    // can be traced to either the stamping pass or the grade rule itself.
    // Usage: grade_debug "<worldX> <worldZ>"
    public static CVarString gradeDebug = new CVarString("grade_debug", "", (cvar) => GradeDebug.Dump(((CVarString)cvar).Value));

    // Dump the water-current field as an arrow grid around the player:
    // `water_current_probe`. Reads through the same trilinear sample the shader
    // does, so it says whether "the river doesn't flow right" is a worldgen
    // problem or a rendering one. An ACTION cvar, not a string one — the
    // console only Executes on a bare name for CVarType.None; for every other
    // type a bare name just prints the value and the callback never fires.
    public static CVar waterCurrentProbe = new CVar("water_current_probe", (cvar) => CurrentDebug.Dump());
    // Console: what water the LOADED world is actually made of, and what the zone
    // under the player authors. The four reasons you see no scum are
    // indistinguishable on screen — see WaterTypeDebug.
    public static CVar waterTypeProbe = new CVar("water_type_probe", (cvar) => WaterTypeDebug.Dump());

    // Debug: cycle control to the next party member. Exercises the party-switch
    // path (GameClient.SwitchControlTo) before the camp Select-Character UI lands.
    public static CVar partyNext = new CVar("party_next", (cvar) => GameClient.Current?.SwitchToNextPartyMember());

    // Detaches the game camera from the player and lets WASD + right-mouse-look
    // fly it freely. Disables pixel snapping while active so mouse-look is smooth.
    public static CVarBool debugFlyCam = new CVarBool("debug_flycam", false);

    // When true, the F3 overlay shows the player's world position and the voxel
    // indices it floors to. Both, because the dumps, the carve grid and the
    // console's voxel commands are all indexed by the latter while the camera
    // and physics work in the former.
    public static CVarBool debugPlayerPosition = new CVarBool("debug_player_position", false);

    // Slope diagnostics. When true, the F3 overlay shows the current floor
    // angle + the last hit on an upward-facing surface too steep to climb
    // (FloorMaxAngle-gated), and prints each unique wall hit to the console.
    // Logs only fire while the player has move input, throttled so a single
    // contact doesn't spam per-tick.
    public static CVarBool debugSlopes = new CVarBool("debug_slopes", false);

    // Names what is stopping horizontal movement, from inside the real per-tick
    // tests: the blocking collider and its layer, whether the block survives
    // with ledge barriers excluded, and how the step-down resolved. An
    // invisible stop looks the same whether it is a barrier, terrain, a prop or
    // the step-down handing the tick back; this separates them in one line.
    public static CVarBool moveBlockDebug = new CVarBool("move_block_debug", false);

    // Draw a translucent wireframe sphere at every ApplyAreaDamage burst
    // (status-effect impact/dash bursts, etc.) for one frame. Off by default —
    // the real hit feedback is the authored Fx; this is a dev visualizer for
    // tuning blast radii. Toggle with `debug_aoe 1` in the in-game console.
    public static CVarBool debugAoe = new CVarBool("debug_aoe", false);

    // Draw both halves of melee reach as wireframes: the swing's damage volume
    // (the swept fan built in ItemEventHandlers.DoMelee, warm orange) for the
    // player and every mob alike, and every nearby HurtBox it has to overlap
    // (HurtBoxDebug, cool green — grey for a corpse's or a burrowed mob's box,
    // which an ordinary weapon can't hit). The swing draws on every swing
    // including whiffs and zero-damage ones, so a mob weapon whose reach
    // doesn't match its animation is visible without landing a hit.
    // Toggle with `debug_melee 1`.
    public static CVarBool debugMelee = new CVarBool("debug_melee", false);

    // Draw the arced-throw preview trajectory (AimingReticle._arcPoints) as
    // debug lines + a marker at each sampled point, bypassing the ribbon shader
    // entirely. Off by default — a dev visualizer for diagnosing the arc ribbon
    // (if these show but the ribbon doesn't, the solve is fine and the bug is in
    // the ribbon's rendering). Toggle with `debug_aim_arc 1` in the console.
    public static CVarBool debugAimArc = new CVarBool("debug_aim_arc", false);

    // Once-per-second console print of the sun + canopy reading at the
    // player's voxel — useful to verify foliage shadowing (a tree's
    // FoliageCluster with CastsSunShadow stamps into CanopyAttenuation;
    // stepping under the canopy should drop sky01 below 0.7 so rain is
    // sheltered). Prints player voxel, sunlight (raw + sky01), and the
    // canopy density byte at that voxel.
    public static CVarBool debugSkyLight = new CVarBool("debug_sky_light", false);

    // Mouse aim sensitivity. Multiplies raw mouseMotion.Relative before
    // accumulating into the aim cursor (clamped to a fixed pixel radius in
    // GameClient). Higher = more cursor travel per pixel of mouse motion =
    // more responsive. 1.0 ≈ raw pixels.
    public static CVarFloat mouseSensitivity = new CVarFloat("mouse_sensitivity", 1.0f);

    // Mouse/keyboard target locking for positional & arced (thrown) weapons. When
    // false, the mouse keeps a plain free aim cursor — no hover ring, no snap-to-mob
    // lock — exactly as before the assist existed. Gamepad locking is unaffected.
    public static CVarBool mouseTargetLock = new CVarBool("mouse_target_lock", true);

    // Sneak input model. False (default) = toggle: a press flips sneak on/off.
    // True = hold-to-sneak: the player sneaks whenever the Sneak button is held
    // (and they otherwise can), and stands the moment it's released.
    public static CVarBool sneakHold = new CVarBool("sneak_hold", true);

    // Hold duration (seconds) shared by the button-hold gestures:
    // holding Interact past this opens a multi-action interactive's options
    // menu (a shorter tap runs the default action), and holding
    // ConsumableCycleRight past it opens the consumable quick-select wheel (a
    // shorter tap cycles to the next consumable).
    public static CVarFloat contextButtonHoldTime = new CVarFloat("context_button_hold_time", 0.3f);

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
        Sim.Current?.SetFogDebugMode(((CVarInt)cvar).Value);
    });

    // Gates AUTHORED voxel fog contribution only (fog_map). Dust + shafts
    // + halos keep working when this is off — they come from dust_density
    // and block-light accumulation which are independent of the fog_map.
    public static CVarBool fogEnabled = new CVarBool("fog_enabled", true, (cvar) =>
    {
        Sim.Current?.SetFogEnabled(((CVarBool)cvar).Value);
    });

    // Master kill-switch for the ENTIRE volumetric fog pass — haze, shafts,
    // halos, dust, everything. When false, the fog shader early-outs to
    // transparent before any raymarching or texture work. Use on low-spec
    // machines as a graphics option, or toggle while profiling to see how
    // much of the frame budget the fog pass accounts for.
    public static CVarBool fogVolumetricEnabled = new CVarBool("fog_volumetric", true, (cvar) =>
    {
        Sim.Current?.SetFogVolumetricEnabled(((CVarBool)cvar).Value);
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

    // Debug: force the precipitation AMOUNT (0..1), overriding the simulated
    // weather's rainAmount. < 0 = off (use real weather). Same rationale as
    // wind_force below: WeatherSimulation.Apply rewrites the channel every
    // frame, so the console cannot otherwise hold a value long enough to look
    // at. Pair with snow_force to get a downpour or a whiteout on demand.
    public static CVarFloat precipForce = new CVarFloat("precip_force", -1f);

    // Debug: force the rain/snow phase, bypassing BOTH gates that normally
    // decide it (the zone's authored ZoneData.snowCover and the air
    // temperature). 0 = all rain, 1 = all snow, in between = sleet.
    // < 0 = off, use the real derivation.
    //
    // Exists because the two gates are AND-ed, so "I see no snow" has four
    // indistinguishable causes: the zone authors no snowCover, the air is too
    // warm, there is no precipitation at all, or the particle path is broken.
    // Forcing the phase separates the last one from the first three without
    // regenerating a world or waiting on the clock.
    public static CVarFloat snowForce = new CVarFloat("snow_force", -1f);

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
    // storm to roll in. Uses Sim.Current.SimData.weatherLightning
    // — wire it in the resource for this to do anything.
    public static CVar strikeLightning = new CVar("strike_lightning", (cvar) =>
    {
        Sim sim = Sim.Current;
        Player player = sim?.player;
        LightningData data = sim?.SimData?.weatherLightning;
        if (sim == null || player == null || data == null)
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
        var result = sim.GetWorld3D().DirectSpaceState.IntersectRay(rayQuery);
        Godot.Vector3 strikePos = result.Count > 0 ? (Godot.Vector3)result["position"] : query2d;
        LightningStrike.Create(sim, strikePos, data);
    });

    // Debug: dump the current weather state plus the variance prev/cur/next
    // triples and the lightning-gate breakdown. Use to diagnose why a
    // thunderstorm isn't firing — the print shows whether the bottleneck
    // is low simCloud, low simRain, or a fair lightningVariance roll.
    public static CVar weatherProbe = new CVar("weather", (cvar) =>
    {
        WorldState ws = Sim.Current?.WorldState;
        SkyController sky = SkyController.Current;
        if (ws == null || sky == null)
        {
            Godot.GD.Print("weather: no active world / sky.");
            return;
        }
        WeatherData w = sky.Weather;
        ZoneData zone = sky.Zone;
        SimData simData = ws.SimData;
        if (w == null || simData == null)
        {
            Godot.GD.Print("weather: world/sim not initialized.");
            return;
        }

        float tod = (float)ws.TimeOfDay01;
        // The diurnal curve is authored in orbit phase (noon = 0.5), so remap.
        float orbitPhase = (float)WorldState.OrbitPhase01(tod);
        float diurnal = WeatherSimulation.DiurnalCurve(orbitPhase, simData);
        float diurnalSlope = WeatherSimulation.DiurnalCurveSlope(orbitPhase, simData);
        float coolingRate = Godot.Mathf.Max(0f, -diurnalSlope);

        // Three storm-mode gates — match WeatherSimulation.Apply.
        float wetGate = Godot.Mathf.SmoothStep(simData.lightningCloudThreshold, 1f, w.cloudCover)
            * Godot.Mathf.SmoothStep(simData.lightningRainThreshold, 1f, w.rainAmount);
        float dryGate = Godot.Mathf.SmoothStep(simData.dryLightningCloudThreshold, 1f, w.cloudCover)
            * (1f - Godot.Mathf.SmoothStep(0f, simData.dryLightningHumidityMax, w.humidity))
            * Godot.Mathf.SmoothStep(simData.dryLightningTempMin, simData.dryLightningTempMax, w.airTemperature);
        // Elevation: use blended ZoneState if available.
        float elev = SkyController.Current?.ZoneState.Elevation ?? 0f;
        float orographicGate = Godot.Mathf.SmoothStep(simData.orographicLightningCloudThreshold, 1f, w.cloudCover)
            * Godot.Mathf.SmoothStep(simData.orographicLightningWindMin, simData.orographicLightningWindMax, w.windSpeed)
            * Godot.Mathf.SmoothStep(simData.orographicLightningElevationMin, 1f, elev);
        float gateAny = Godot.Mathf.Max(wetGate, Godot.Mathf.Max(dryGate, orographicGate));
        string winner = wetGate >= dryGate && wetGate >= orographicGate ? "WET"
            : dryGate >= orographicGate ? "DRY" : "OROGRAPHIC";

        Godot.GD.Print("=== weather probe ===");
        Godot.GD.Print($"  time-of-day:    day={ws.DayNumber} tod={tod:F3} (abs={ws.TimeOfDayAbsolute:F3})  diurnal={diurnal:F3}  slope={diurnalSlope:F3}  coolingRate={coolingRate:F3}");
        Godot.GD.Print($"  {(WorldState.IsNight(tod) ? "NIGHT slot active" : "DAY slot active")} (day/night weather re-roll at each sleep-to-sunrise)");
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

        // PRECIPITATION PHASE. Printed as the two gates rather than only the
        // result, because "no snow" has four causes that look identical from
        // the outside: the zone authors no snowCover, the air is too warm,
        // there is no precipitation at all, or the particle path is broken.
        DerivedPalette phasePal = sky.Palette;
        float coldGate = 1f - Godot.Mathf.SmoothStep(simData.snowTempColdF, simData.snowTempWarmF, w.airTemperature);
        Godot.GD.Print($"  PRECIPITATION PHASE:");
        Godot.GD.Print($"    zone snowCover = {zone?.snowCover ?? 0f:F3}  (blended; 0 here means snow can NEVER fall)");
        Godot.GD.Print($"    cold gate      = {coldGate:F3}  ({w.airTemperature:F1}°F across {simData.snowTempColdF:F0}..{simData.snowTempWarmF:F0}°F)");
        Godot.GD.Print($"    snowFraction   = {phasePal.SnowFraction:F3}{(CVars.snowForce.Value >= 0f ? "  [FORCED]" : "")}");
        Godot.GD.Print($"    rainIntensity  = {phasePal.RainIntensity:F3}  (tier {phasePal.RainTier})");
        Godot.GD.Print($"    snowIntensity  = {phasePal.SnowIntensity:F3}{(CVars.precipForce.Value >= 0f ? "  [precip FORCED]" : "")}");

        // WATER OPTICS — the chain from the authored zone colour to what the
        // shader actually scatters. Worth printing in full because every step
        // is a place the authored hue quietly loses authority, and none of them
        // are visible from the screen: the sediment pull can outvote the author
        // outright (it is weighted muddiness * 0.6, so the muddier the zone the
        // less its waterColor means), and the albedo then scales what survives.
        DerivedPalette wpal = sky.Palette;
        if (zone != null)
        {
            Godot.GD.Print($"  WATER:");
            Godot.GD.Print($"    zone authored  waterColor={zone.waterColor} waterOpacity={zone.waterOpacity:F3}");
            float wmuddy = wpal.WaterMuddiness;
            Godot.GD.Print($"    blended        hue={wpal.WaterShallowTint} muddiness={wmuddy:F3}");
            Godot.Color wscatter = wpal.WaterShallowTint;
            float walbedo = Godot.Mathf.Lerp(sky.waterClearScatterAlbedo, sky.waterMuddyScatterAlbedo, wmuddy);
            Godot.GD.Print($"    scatter        {wscatter}  (hue verbatim; muddiness moves intensity only)");
            Godot.GD.Print($"    x albedo {walbedo:F3}  -> scatter_color={wscatter * walbedo}");
            float wabsorb = Godot.Mathf.Lerp(sky.waterClearAbsorption, sky.waterMuddyAbsorption, wmuddy);
            Godot.GD.Print($"    absorption     {wabsorb:F3}/m x (1-scatter) -> "
                + $"({(1f - wscatter.R) * wabsorb:F2}, {(1f - wscatter.G) * wabsorb:F2}, {(1f - wscatter.B) * wabsorb:F2})/m");
        }

        // FOG breakdown — the values that actually drive the volumetric
        // shader, plus the night-dimming diagnostic. fogPhaseScale is fed
        // p.PrimaryIntensity, which is sun-side: it only drops after sunset
        // via the nightfall SkyLight scale, not from the day→night blend.
        // This shows how close night fog still is to full daytime density.
        DerivedPalette pal = sky.Palette;
        float fogIntensityReference = simData.fogIntensityReference;
        float fogIntensityFloor = simData.fogIntensityFloor;
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
        Player probePlayer = Sim.Current?.player;
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
        Godot.GD.Print($"    SkyLight (nightfall, 1=sunset 0=midnight) = {pal.SkyLight:F3}");
        Godot.GD.Print($"    Illumination (0 = no light in open air)    = {pal.Illumination:F3}   (scales fog_color)");
        Godot.GD.Print($"    fogPhaseScale  CURRENT = {curPhaseScale:F3}   (1.0 = no night dimming)");
        Godot.GD.Print($"    fogPhaseScale  IF FIXED= {fixPhaseScale:F3}   (would scale fog by this/{curPhaseScale:F3} = {(curPhaseScale > 0 ? fixPhaseScale / curPhaseScale : 1f):F2}×)");

        Godot.GD.Print($"  VARIANCE  (day slot → night slot   |   currently active)");
        Godot.GD.Print($"    weather    = {ws.DayWeatherVariance:F3} → {ws.NightWeatherVariance:F3}   |  {ws.WeatherVariance:F3}  slope={ws.WeatherVarianceSlope:F3}");
        Godot.GD.Print($"    humidity   = {ws.DayHumidityVariance:F3} → {ws.NightHumidityVariance:F3}   |  {ws.HumidityVariance:F3}");
        Godot.GD.Print($"    cloud      = {ws.DayCloudVariance:F3} → {ws.NightCloudVariance:F3}   |  {ws.CloudVariance:F3}");
        Godot.GD.Print($"    lightning  = {ws.DayLightningVariance:F3} → {ws.NightLightningVariance:F3}   |  {ws.LightningVariance:F3}");
        Godot.GD.Print($"    (cloud variance is INVERSE: low = cloudier; lightning variance reads through directly; day→night crossfades at sunset)");
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
    // 0 = sunrise, 0.25 = noon, 0.5 = sunset, 0.75 = midnight, 1 = the next
    // sunrise (where the clock pauses until a sleep). Clamped to [0, 1];
    // setting via console jumps the sun/moon orbit immediately within the day.
    // NOTE: this only takes effect with a world loaded, so setting it from
    // cvars.txt or the command line is dropped — use the in-game console.
    public static CVarFloat timeOfDay = new CVarFloat("time_of_day", 0.05f, (cvar) =>
    {
        WorldState ws = Sim.Current?.WorldState;
        if (ws == null) { return; }
        double v = System.Math.Clamp((double)((CVarFloat)cvar).Value, 0.0, 1.0);
        ws.TimeOfDay01 = v;
        ws.TimeOfDayAbsolute = ws.DayNumber + v;
    });

    // Swaps the MainCamera between authored framing presets (CameraAngleSettings)
    // for A/B testing angles: 0 = orthographic shipping framing, 1/2 = perspective,
    // 3 = free-look orbit (mouse / right stick drive yaw & pitch). Applied to the
    // live camera only on change, so any inspector live-tuning of the resulting
    // pitch/distance/fov sticks between swaps.
    public static CVarInt cameraPreset = new CVarInt("camera_preset", 0, (cvar) =>
    {
        var client = GameClient.Current;
        if (client != null && client.camera != null)
        {
            client.camera.ApplyAngleSettings(CameraAngleSettings.FromPreset(((CVarInt)cvar).Value));
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

    // Master enable for the cinematic slow-motion "death cam" (SlowMotionController).
    // When false, Trigger() is a no-op so the game never slows / zooms on death.
    public static CVarBool slowMotion = new CVarBool("slow_motion", true);

    // Master enable + scale for controller rumble (ControllerRumble, the haptic
    // sibling of camera shake; owned by GameClient). `rumble` false stops every
    // motor and skips the driver; `rumble_scale` multiplies all impulse
    // magnitudes (0 = silent, 1 = authored, >1 exaggerates for testing).
    public static CVarBool rumble = new CVarBool("rumble", true);
    public static CVarFloat rumbleScale = new CVarFloat("rumble_scale", 1f);

    // Gates the bird's-eye volumetric cloud quad. Default OFF — the moderate
    // overview reads cleaner without it (the cloud band suited the old dramatic
    // high overlook). Enable to bring the cloud layer back during the overlook;
    // the bird's-eye state machine and camera lift are unaffected either way,
    // only the visible cloud layer is toggled.
    public static CVarBool clouds = new CVarBool("clouds", false);

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

    // Player audio volumes (linear 0..1, 1 = unity gain), applied as bus
    // volume_db via AudioVolume. master scales everything; music scales the
    // Music bus; sfx scales both the 3D positional (World3D) and 2D ambience
    // (Ambience2D) buses — everything that isn't music. AudioVolume.ApplyAll()
    // pushes these at startup since cvar callbacks don't fire on construction.
    public static CVarFloat volumeMaster = new CVarFloat("volume_master", 1f, (cvar) =>
    {
        AudioVolume.ApplyMaster(((CVarFloat)cvar).Value);
    });
    public static CVarFloat volumeMusic = new CVarFloat("volume_music", 1f, (cvar) =>
    {
        AudioVolume.ApplyMusic(((CVarFloat)cvar).Value);
    });
    public static CVarFloat volumeSfx = new CVarFloat("volume_sfx", 1f, (cvar) =>
    {
        AudioVolume.ApplySfx(((CVarFloat)cvar).Value);
    });

    // When true, prints a line each time WeatherLightningSpawner picks
    // a strike interval, skips a strike (no ground, no data), or fires
    // one. Use to verify the spawner is awake and observe its cadence
    // against the current lightning intensity. Lines look like:
    //   [lightning] intensity=0.18 interval=8.2s
    //   [lightning] FIRE at (12.3, 4.0, -6.1) (intensity=0.21)
    //   [lightning] skip: no ground at (12.3, 0.0, -6.1)
    public static CVarBool lightningLog = new CVarBool("lightning_log", false);

    // When true, prints a line each NightMobSpawner spawn cycle with the current
    // population target (which ramps toward midnight), the live count, and how
    // many mobs it spawned. Use to verify the density ramp and that mobs are
    // finding unlit ground to spawn on. Lines look like:
    //   [nightspawn] target=6 current=4 spawned=2
    public static CVarBool nightSpawnLog = new CVarBool("night_spawn_log", false);

    // When true, prints a full NightMobSpawner status line once a second — every
    // input to slime spawning (time of day, the darkness dwell + what it's easing
    // toward, player light, the danger scalar, target vs current) PLUS a live
    // ground-probe (how many candidate points found ground, how many were dark
    // enough, and the ground-vs-player height delta) and a one-word reason nothing
    // is spawning. Use to diagnose "why aren't gellies appearing here?" — e.g. a
    // large positive dyAvg means the ground ray is catching terrain above a cave
    // instead of its floor.
    public static CVarBool nightSpawnDebug = new CVarBool("night_spawn_debug", false);

    // When true, draws the NightMobSpawner's search in-world (DebugDraw): a gray
    // slab on every standable spot found around the player, a red→green slab on
    // each VALID candidate (green = darker = higher spawn weight), and a cyan cross
    // at the player. Each slab's height is its spawn Y, so it shows at a glance
    // whether the search is finding the cave floor you're on or the surface above.
    public static CVarBool nightSpawnDraw = new CVarBool("night_spawn_draw", false);

    // When true, prints a line each time the ambient FairySpawner spawns a fairy —
    // the day/period, the running daily count vs the cap, and how many fairies the
    // player has killed today. Use to verify the day-block cadence and per-zone gate.
    public static CVarBool fairySpawnLog = new CVarBool("fairy_spawn_log", false);

    // When true, prints companion-follow / breadcrumb-rescue diagnostics:
    // BehaviorWanderFollow logs (throttled) its phase, distance-to-player,
    // chosen destination and leg speed; Sim.TickCompanionLeash logs each
    // time the pet goes non-resident and whether a rescue crumb was found.
    // Use to diagnose the dog getting left behind / failing to catch up.
    public static CVarBool companionDebug = new CVarBool("companion_debug", false);

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

    // Draw the generated ledge barriers as translucent orange quads. They are
    // invisible collision, so this is the only way to see WHERE one ended up —
    // a barrier in the wrong place is indistinguishable from a missing one.
    public static CVarBool ledgeBarrierDebug = new CVarBool("ledge_barrier_debug", false,
        (cvar) => ChunkMesh.SetLedgeBarrierDebugVisible(((CVarBool)cvar).Value));

    // Console command: how many chunks generated ledge barriers and how many
    // faces they cost. The barriers are invisible, so this is the only way to
    // confirm generation ran and to size it.
    public static CVar ledgeBarrierStats = new CVar("ledge_barrier_stats", (cvar) =>
    {
        Godot.GD.Print($"[ledge_barrier] chunks={ChunkMesh.LedgeBarrierChunks} "
            + $"faces={ChunkMesh.LedgeBarrierFaces}");
        // Generation and COLLISION are separate failures that look identical
        // from the player's side: barriers can exist in the right place and
        // still do nothing if the body is not masking their layer this tick.
        Player player = Sim.Current?.player;
        if (player == null)
        {
            Godot.GD.Print("  no player — cannot report collision state");
            return;
        }
        uint bit = (uint)ECollisionLayer.LedgeBarrier;
        Godot.GD.Print($"  player mask=0x{player.CollisionMask:X} "
            + $"masksBarrier={((player.CollisionMask & bit) != 0)} "
            + $"grounded={player.IsGrounded}");
    });

    // Console command: dump the walkability sampler's view of the 3x3 columns
    // around the player, alongside the raw voxel stack, with a verdict for every
    // air-over-solid candidate. Answers which gate discarded a surface the
    // player is demonstrably standing on — which the nav_grid overlay cannot,
    // since it draws the conclusion rather than the reasoning.
    public static CVar navColumn = new CVar("nav_column", (cvar) => NavColumnDebug.Dump());

    // Log every mantle start and completion, with the resolved landing and rise.
    // Dumps the last ~2.5s of position ownership when the player ends up with no
    // world beneath them — the only way to see what caused a fall-through, since
    // it is always noticed after the fact.
    public static CVarBool fallTrace = new CVarBool("fall_trace", false);
    public static CVarBool mantleDebug = new CVarBool("mantle_debug", false);

    // The mob-side analogue of fall_trace, and the tool for "which behavior
    // walked this mob off a cliff".
    //
    // Prints one line the tick a mob's footing drops away by more than its own
    // maxFallHeight — ground its pathfinder would never have routed it over —
    // naming the active behavior node, the navigator's whole decision state, and
    // which physics channel owned the body. It has to be captured on that edge:
    // by the time a falling mob is noticed the navigator has repathed and the
    // behavior may already have changed, so nothing about the airborne body
    // still says how it got there.
    public static CVarBool mobFallTrace = new CVarBool("mob_fall_trace", false);

    // Console command: `climb_mark <height>` stamps a climbable face up the wall
    // the player is looking at, `climb_mark 0` clears it. Test scaffolding until
    // the editor grows a face-paint tool — it writes the real OverlayFaces
    // channel, but it also flips the wall block climbable for the session
    // (see Blocks.SetClimbableForDebug) because no ivy overlay is authored yet.
    public static CVarInt climbMark = new CVarInt("climb_mark", 0,
        (cvar) => ClimbMarkDebug.Apply(((CVarInt)cvar).Value));

    // Log climb attach and release.
    public static CVarBool climbDebug = new CVarBool("climb_debug", false);


    // Console command: the nearest coiled rope explains, gate by gate, why it
    // does or does not offer to drop. A coil that resolves no drop shows no
    // prompt at all, so this is the only way to tell a mis-aimed one from a
    // broken one.
    public static CVar ropeProbe = new CVar("rope_probe", (cvar) => CoiledRope.Probe());

    // Console command: walk the climb probe's gates for the wall in front of the
    // player and print each verdict. "It won't attach" is always one specific
    // gate; this names it instead of leaving it to bisection.
    public static CVar climbProbe = new CVar("climb_probe", (cvar) => ClimbMarkDebug.Probe());

    // When true, MobHUD shows a two-line text overlay over each visible mob
    // breaking down PLAYER-perceives-MOB. Top line: V/H/S sense deltas
    // (smell is always 0 — player doesn't smell). Bottom line: L (light at
    // mob), D (distance closeness), F (facing — always 1 for the player),
    // S (mob's speed-based visibility), C (1 - mob camouflage).
    public static CVarBool debugPlayerPerception = new CVarBool("debug_player_perception", false);

    // When true, MobHUD shows the same two-line breakdown for MOB-perceives-
    // PLAYER. Top: V/H/S sense deltas. Bottom: L (player light), D (distance
    // closeness vs mob visionRange), F (mob's facing dot-power), S (player
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

    // Prints what the minimap shader's height-derived terms actually see: the
    // heightmap's world-Y range, per-texel neighbor deltas, how much of the map
    // passes the contour is_step gate, and the fwidth the contour anti-aliasing
    // reads at world-map zoom. Both the contour interval and the plateau banding
    // are authored in absolute meters, so a change to the world's vertical extent
    // can silently stop them working — this says by how much, and what
    // contour_interval the current terrain wants.
    public static CVar minimapProbe = new CVar("minimap_probe", (cvar) =>
    {
        Minimap minimap = Sim.Current?.Minimap;
        if (minimap == null)
        {
            Godot.GD.Print("minimap_probe: no active world.");
            return;
        }
        float screenWidth = GameClient.Current?.GetViewport()?.GetVisibleRect().Size.Y ?? 1080f;
        Godot.GD.Print(minimap.FormatHeightStats(screenWidth));
    });

    // Cheat: charts the entire map in one shot — the whole outdoor fog-of-war,
    // every underground/indoor slice the world has content in, every region name,
    // and every map marker currently loaded. Banks the lot into the party pool
    // (as a campfire would) so the world map shows it immediately.
    public static CVar revealMap = new CVar("reveal_map", (cvar) =>
    {
        Minimap minimap = Sim.Current?.Minimap;
        if (minimap == null)
        {
            Godot.GD.PushWarning("reveal_map: no active world.");
            return;
        }
        minimap.RevealEverything();
    });

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

    // Per-entity-type cost of the world-load entity drain plus the chunk-mesh
    // fill, printed once as a table when the loading screen finishes. Must be
    // set before the world loads to catch anything:
    //   -- "spawn_cost_profile 1" "autostart 1"
    public static CVarBool spawnCostProfile = new CVarBool("spawn_cost_profile", false);

    // Console action: walks the whole scene tree and prints what the resident
    // nodes are, bucketed by subtree / source scene / class, with the columns
    // that actually cost frame time (nodes in the process lists, culled
    // VisualInstance3Ds, Jolt colliders). Explains the gap between the F3
    // overlay's node_count and render_objects.
    public static CVar nodeCensus = new CVar("node_census", (cvar) =>
    {
        NodeCensus.Run();
    });

    // node_tree <substring> → prints the full subtree of the first node whose
    // name or source scene matches, with per-node cost flags. node_census says
    // which scene is heavy per instance; this says what's inside it.
    public static CVarString nodeTree = new CVarString("node_tree", "", (cvar) =>
    {
        NodeCensus.DumpSubtree(((CVarString)cvar).Value);
    });

    // Seconds after the game scene starts at which to auto-run `node_census`
    // once. 0 = never. Exists so a headless run (which has no console) can
    // capture a census once the world has settled:
    //   -- "autostart 1" "node_census_delay 20"
    public static CVarFloat nodeCensusDelay = new CVarFloat("node_census_delay", 0f);

    // Console action: re-stitches the voxel terrain atlas from its source art,
    // the headless twin of the editor's "Rebuild Atlas" button. Needs no world
    // and no renderer, so a CI or agent run can bake the atlas — which is also
    // the only path that mints an AtlasBaseIndex for a newly added surface.
    public static CVar atlasRebuild = new CVar("atlas_rebuild", (cvar) =>
    {
        var manifest = Godot.GD.Load<VoxelAtlasManifest>(VoxelAtlasManifest.ManifestResourcePath);
        if (manifest == null)
        {
            Godot.GD.PrintErr($"atlas_rebuild: could not load {VoxelAtlasManifest.ManifestResourcePath}.");
            return;
        }
        manifest.RebuildAtlas();
    });

    // Console action: dumps a block-id census of the loaded world, most common
    // first. The check for "is this material actually being placed?" — reading
    // the catalog and the atlas only proves the material COULD render.
    public static CVar worldHistogram = new CVar("world_histogram", (cvar) =>
    {
        if (Sim.Current?.WorldState == null)
        {
            Godot.GD.PrintErr("world_histogram: no active world (start a game first).");
            return;
        }
        Godot.GD.Print(Sim.Current.WorldState.DescribeBlockHistogram());
    });

    // Seconds after the game scene loads to run world_histogram once, for
    // unattended runs with no console to type into. Same slot as
    // node_census_delay:
    //   -- "autostart 1" "world_histogram_delay 20"
    public static CVarFloat worldHistogramDelay = new CVarFloat("world_histogram_delay", 0f);

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

    // mob_physics 0 → every Mob freezes, its CollisionLayer/Mask go to 0 (so
    //                 the broadphase and contact resolver see nothing), AND its
    //                 C# _PhysicsProcess tick is skipped. This is the "mobs cost
    //                 nothing" floor — it does NOT on its own tell you whether
    //                 the cost was Jolt or C#. Pair it with mob_ai below.
    public static CVarBool mobPhysics = new CVarBool("mob_physics", true);

    // mob_ai 0 → skip only the C# half of Mob._PhysicsProcess (perception,
    //            status/environment ticks, TickAI, the action runner, steering,
    //            animation) while leaving the RigidBody live and unfrozen in
    //            Jolt. This is the finer half of the mob bisection; the two
    //            toggles decompose mob cost:
    //
    //              baseline    - (mob_ai 0)      = C# per-mob tick cost
    //              (mob_ai 0)  - (mob_physics 0) = Jolt body cost
    //
    //            Caveat: with AI off nothing commands the mobs, so Jolt sleeps
    //            most of them within a second or two. The Jolt number this
    //            yields is the resting-body floor, not the cost of a moving
    //            crowd — read it as a lower bound.
    public static CVarBool mobAI = new CVarBool("mob_ai", true);

    // mob_cold_tick 0 → every mob runs its full upkeep every physics tick,
    //                   disabling the distance-based cold band
    //                   (SimData.mobColdTickDistance). Bisection toggle for
    //                   measuring what the LOD is actually worth, and the first
    //                   thing to flip if a distant mob misbehaves.
    public static CVarBool mobColdTick = new CVarBool("mob_cold_tick", true);

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

    // skeleton_internal 0 → SetProcessInternal(false) on every Skeleton3D in the
    //                       tree. Skeleton3D does its per-frame pose/skin work on
    //                       Godot's INTERNAL process channel, which no
    //                       Profiler.Sample can wrap and which IsProcessing()
    //                       doesn't even report — so it lands in process_ms as
    //                       unaccounted time and the only way to size it is to
    //                       switch it off and read the delta. Expect poses to
    //                       freeze while it's off; that IS the tell that the work
    //                       is real. Purely a bisection toggle — the fix, if this
    //                       measures big, is to gate skeletons the way
    //                       mob_anim_cull already gates AnimationPlayers (139
    //                       resident mobs, only ~2 animating, yet 119 skeletons
    //                       were still ticking internally).
    public static CVarBool skeletonInternal = new CVarBool("skeleton_internal", true, (cvar) =>
    {
        SkeletonProbe.SetInternalProcessing(((CVarBool)cvar).Value);
    });

    // motes 0 → the camera-parented dust-mote GpuParticles3D (MoteEffect,
    //           scenes/fx/motes.tscn, 4000 particles) hides itself, so
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
    //                        This is the CPU gate — no node, no lookup. Its
    //                        pair `sprite_reflection_visible` is the RENDER gate
    //                        (nodes and per-frame work stay, the tint goes to
    //                        zero so they draw invisible). Use this one to
    //                        measure CPU cost, that one to kill the look.
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
        if (Sim.Current == null || Sim.Current.PropScatter == null)
        {
            Godot.GD.Print("props_stats: no active world.");
            return;
        }
        Godot.GD.Print(Sim.Current.PropScatter.FormatStats());
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

    // Concavity puddle-bias tuning (concavity_wetness_strength, concavity_threshold)
    // is authored on resources/materials/terrain.tres, not a CVar.

    // Debug: paint baked concavity directly on terrain — red = dip, blue =
    // bump, black ≈ flat. Use to confirm the concavity bake lands in bowls /
    // valley floors before judging the (subtler) wetness pooling.
    public static CVarBool debugConcavity = new CVarBool("debug_concavity", false, (cvar) =>
    {
        ChunkMesh.SetDebugConcavity(((CVarBool)cvar).Value);
    });

    // Console action: re-read every surface .tres and both atlas strips from disk
    // and re-push the per-layer shader tables — porosity, overlay cliff routing,
    // and the overlay edge knobs (erode / feather / relief). The live-tuning loop
    // for surface blends: edit the .tres (or re-run tools/stitch_voxel_atlas.py),
    // run this, see it immediately. No restart, no re-mesh, no rebuild.
    // Debug: draw EVERY water surface as one block id, ignoring what the world
    // actually holds. -1 = off. The point is time-to-condition: judging a scum
    // film otherwise means regenerating a world and walking to water that
    // happened to be stamped with it. `block_check` lists the ids.
    public static CVarInt waterFilmForce = new CVarInt("water_film_force", -1, (cvar) =>
    {
        ChunkMesh.SetWaterFilmForceBlock(((CVarInt)cvar).Value);
    });

    public static CVar surfaceReload = new CVar("surface_reload", (cvar) =>
    {
        ChunkMesh.ReloadSurfaceTables();
    });

    // Debug: paint the overlay pass. RED = coverage after erode, GREEN = coverage
    // before erode, BLUE = the blend weight that survived the height interlock.
    // Black means no overlay reached this fragment at all — which is the one
    // conclusion the final image cannot give you, since bare rock and a fully
    // out-blended overlay look identical.
    public static CVarBool debugOverlayCov = new CVarBool("debug_overlay_cov", false, (cvar) =>
    {
        ChunkMesh.SetDebugOverlayCov(((CVarBool)cvar).Value);
    });

    // How many voxel rows of a mantleable wall wear the climb-growth overlay
    // (ClimbLedgeMarker + ChunkMesherDC). WIDTH is not set here — that is the
    // surface's overlayErode*/overlayFeather, which trim the coverage gradient
    // sub-voxel. This only decides how much region there is to trim.
    //   0 = off. Skips the pass entirely — the A/B for what it costs.
    //   1 = lip row. Coverage is full AT the edge and ramps to nothing a metre
    //       down the wall, so erode can cut it back to any width either side.
    //   2 = full rise. Both rows, for maximum coverage before trimming.
    // Changing this requeues every loaded chunk; the mark is baked, not resolved
    // at draw time.
    public static CVarInt climbLedgeMarks = new CVarInt("climb_ledge_marks", 1, (cvar) =>
    {
        Sim.Current?.ChunkManager?.RebuildAllChunkMeshes();
    });

    // Terrain texture tuning (tile_uv_scale, tile_normal_strength, the three
    // blend sharpnesses) and the wetness model (wet_displacement,
    // wet_roughness_min, wet_chroma) are authored on
    // resources/materials/terrain.tres, not CVars. Per-material porosity (rock
    // reflects vs soil darkens) is on BlockSurfaceData.Porosity; the standing-water
    // (puddle) shape — pool strength, scale, edge, ramp, flatness — and the
    // dynamic ripple feel are on SkyController + that material.

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

    // Term-isolation switches for the terrain composite. The final pixel is a
    // product of several independently-baked terms, so a banding artefact can
    // be bisected by neutralising one at a time rather than reasoning about the
    // finished pixel. Each maps to a `global uniform bool` of the same name in
    // voxel_clip.gdshader; all default off.
    //
    // Pair with debug_white_albedo (white source color) so only lighting is
    // left, then switch these off one at a time — the one that removes the
    // banding is the term carrying it.

    // Distance along the surface normal (in voxels) at which terrain samples the
    // lightmap. Sunlight is baked into AIR voxels ONLY, so a small offset leaves
    // unlit solid texels of the ground itself inside the sample's trilinear
    // footprint — and the share of solid in that footprint cycles with where the
    // surface sits in the voxel grid, which is what banded smooth slopes. 0.5
    // (one half-voxel, just clear of the surface) was visibly banded; 1.0 nearly
    // clears it and 1.5 removes it.
    //
    // Raising it is NOT a usable fix: on a vertical face the sample walks that
    // far horizontally out of the wall into open sunlit air, so the top of every
    // cliff gains a lit band exactly as wide as the offset. Kept as a diagnostic
    // — it is what proved the banding comes from solid texels in the footprint.
    public static CVarFloat lightSampleOffset = new CVarFloat("light_sample_offset", 0.5f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("light_sample_offset", ((CVarFloat)cvar).Value);
    });

    // Baked per-vertex ambient occlusion (COLOR.a -> ao_factor). Off = 1.0.
    public static CVarBool debugNoAo = new CVarBool("debug_no_ao", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_no_ao", ((CVarBool)cvar).Value);
    });

    // The BAKED sun mask (lightmap R channel). Off keeps sun intensity/color
    // but drops the per-voxel visibility term, so banding that survives this
    // does not come from the sunlight volume.
    public static CVarBool debugNoSun = new CVarBool("debug_no_sun", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_no_sun", ((CVarBool)cvar).Value);
    });

    // Block light (lightmap GBA) plus its shadow projector. Off = 0.
    public static CVarBool debugNoBlockLight = new CVarBool("debug_no_block_light", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_no_block_light", ((CVarBool)cvar).Value);
    });

    // Wetness: sky reflection, puddles and footstep ripple rims.
    public static CVarBool debugNoWet = new CVarBool("debug_no_wet", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_no_wet", ((CVarBool)cvar).Value);
    });

    // Water term isolation. debug_water_flat outputs a constant colour with no
    // thickness, depth read, or screen blend — an artefact that survives it is
    // geometry (overlapping / z-fighting faces), not shading.
    // debug_water_thickness shows the reconstructed thickness as greyscale.
    public static CVarBool debugWaterFlat = new CVarBool("debug_water_flat", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_water_flat", ((CVarBool)cvar).Value);
    });

    public static CVarBool debugWaterThickness = new CVarBool("debug_water_thickness", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_water_thickness", ((CVarBool)cvar).Value);
    });

    // Sun source A/B. On = the raw light-volume fetch with no openness term
    // (slopes band where the dilation misses, walls read flat); off = the
    // shipping path, volume sun x static per-vertex openness.
    public static CVarBool debugSunFromVolume = new CVarBool("debug_sun_from_volume", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_sun_from_volume", ((CVarBool)cvar).Value);
    });

    // Forces the pre-dynamic per-vertex sun bake everywhere, including chunks
    // inside the light-map window. The A/B partner for the volume path: terrain
    // stops responding to doors and anything else that moves sunlight at run
    // time, which is exactly what makes the difference legible.
    public static CVarBool debugSunLegacy = new CVarBool("debug_sun_legacy", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_sun_legacy", ((CVarBool)cvar).Value);
    });

    // Eye adaptation gain, which is a per-fragment function of local
    // illuminance and so can turn a gentle light gradient into a visible step.
    public static CVarBool debugNoEyeAdapt = new CVarBool("debug_no_eye_adapt", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("debug_no_eye_adapt", ((CVarBool)cvar).Value);
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
    //  15-20 = PER-LAYER ISOLATION. The water surface stacks five contributors
    //       and they are impossible to tell apart by eye; these show each one's
    //       ACTUAL contribution to the final pixel (already multiplied by the
    //       alpha/reflection weights it is composited with), so 15+16+17+18 sum
    //       to the normal image. Step through them to find which layer is
    //       making the water too bright before changing any tuning:
    //   15 = seabed seen THROUGH the water (the screen-texture sample)
    //   16 = underwater caustics only
    //   17 = the water body itself (its tint × the light on it, foam included)
    //   18 = the sky reflection on the surface
    //   19 = foam mask (greyscale coverage, not colour)
    //   20 = water_alpha (white = opaque water, black = see-through)
    //   21 = surface film mask (white = scum/algae, black = bare water)
    public static CVarInt waterDebug = new CVarInt("water_debug", 0, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("water_debug_mode", ((CVarInt)cvar).Value);
    });

    // A cascade's sheet blends continuously between falling water and the pool
    // surface rolling off its lip (`surfaceness`), so nothing on screen says
    // which one you are looking at — and that is exactly the question when a
    // fall looks wrong against water. Spray is neither: it is Fx particles,
    // isolate that with `fx_particles 0`.
    //   0 = normal
    //   1 = the falling half only. It discards, so the hidden half stops
    //       writing depth and stops occluding the pool as well.
    //   2 = the surface half only (the brink).
    //   3 = surfaceness, flat and opaque: RED falling, GREEN surface — shows
    //       where a given fall hands over from one to the other.
    public static CVarInt waterfallDebug = new CVarInt("waterfall_debug", 0, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("waterfall_debug", ((CVarInt)cvar).Value);
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
    //  20 = the iris disk painted onto the ground in GREEN. The disk is a
    //       circle on SCREEN, so where it lands on the terrain it reads as an
    //       ellipse stretched away from the camera — which is the shape to
    //       check when the reveal covers more or less ground than expected.
    //  21 = the cut VOLUME painted RED on terrain instead of being discarded,
    //       so the cut reads as a solid: red climbs a wall from the clip
    //       plane up and stops laterally at the fan boundary. Mode 20 is the
    //       plan view, this is the elevation. The ceiling cap stands down in
    //       this mode so it can't paint black over the diagnostic. Terrain
    //       only — roofs are model props and still cut away normally, so pair
    //       with `props_visible 0` if one is in the way.
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

    // cap_mask_pass 0 / outline_mask_pass 0 -> stop the matching off-screen
    // SubViewport rendering (UpdateMode.Disabled). Both otherwise run
    // UpdateMode.Always, so each is a FULL scene cull every frame across every
    // VisualInstance3D in the world — the same population the main camera culls,
    // paid again per pass. The outline mask normally has nothing in it (only a
    // highlighted interactive's meshes join OutlineMaskLayer), so it is pure
    // overhead most frames.
    //
    // Expect breakage while off: the ceiling cutaway freezes on a stale mask and
    // the selection outline disappears. These size the pass; they are not
    // ship-off switches. Pair with block_light_shadow / ground_stain to bisect
    // the whole off-screen-pass budget against frame_ms_avg.
    public static CVarBool capMaskPass = new CVarBool("cap_mask_pass", true, (cvar) =>
    {
        GameClient.Current?.camera?.SetCapMaskPassEnabled(((CVarBool)cvar).Value);
    });
    public static CVarBool outlineMaskPass = new CVarBool("outline_mask_pass", true, (cvar) =>
    {
        GameClient.Current?.camera?.SetOutlineMaskPassEnabled(((CVarBool)cvar).Value);
    });

    // clip_iris_debug N -> draw the cutaway's PROBE RING around the player.
    // Draws only; the ring runs whether or not this is on.
    //   0  off
    //   1  the ring: a marker per sample, a stem up to the ceiling it found, and
    //      an ORANGE tick over any sample hidden from the camera. Sample colour is
    //      the space it landed in — BLUE open, dim blue sky, MAGENTA a doorway or
    //      window, AMBER under an eave only, GREY solid at the player's level (a
    //      wall; it answers nothing and votes on nothing), and small DARK for a
    //      sample the player themselves cannot see, which now reports nothing.
    //      Every occluded sample also draws its camera march in RED, from the
    //      LIFTED origin to the voxel that stopped it — read that before theorising
    //      about what is opening the disk, because "which samples are occluded" is
    //      the easy half and "what is occluding them" is the half that misleads.
    //   2  + the GREEN base plane the quantile settled on, the MAGENTA disc seed,
    //      and the disc itself drawn AT its target height — YELLOW while growing,
    //      ORANGE once promoted to full screen. Seeing the two planes at their
    //      real elevations with open distance between them is the read the whole
    //      design is after.
    // Read the stems first. The base is a quantile over them, so a base that
    // twitches while you stand still is a spread problem in the ring, not a
    // tuning problem — and the stems show the spread directly.
    public static CVarInt clipIrisDebug = new CVarInt("clip_iris_debug", 0);

    // clip_iris_dump 1 -> print the ring's decision at the player once a second:
    // resolved base, the seed it picked, and the weighted occlusion share. The
    // companion to the drawing for anything that has to be read as a number.
    public static CVarBool clipIrisDump = new CVarBool("clip_iris_dump", false);


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

    // Bisection toggle: remove all foam coverage from the water surface —
    // shoreline surf, the contiguous rim band at the water/land edge, and
    // whitecaps. Colour and lighting are untouched; only the mask goes to zero,
    // which also releases water_alpha (foam otherwise forces it to 1 at the
    // shore, making the band fully opaque). Use it to answer "is the bright
    // shoreline foam at all?" in one step, rather than reading it off
    // `water_debug 17`/`19`.
    public static CVarBool waterDisableFoam = new CVarBool("water_disable_foam", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("water_disable_foam", ((CVarBool)cvar).Value);
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

    // sprite_reflection_visible 0 → the global reflection_tint that
    //                            sprite_reflection.gdshader multiplies its
    //                            output by goes to zero, so sprite-based water
    //                            reflections draw invisible. The reflection
    //                            nodes and their per-frame update still exist
    //                            and still cost CPU — this is the RENDER gate,
    //                            matching mob_visible / props_visible. Use
    //                            `sprite_reflections` (above) for the CPU gate
    //                            that skips the work entirely. Useful for
    //                            isolating render bugs or as a low-end setting.
    public static CVarBool spriteReflectionVisible = new CVarBool("sprite_reflection_visible", true, (cvar) =>
    {
        // The actual reflection_tint value is pushed every frame by
        // SkyController.Apply(); checking this flag there gates the push.
    });

    // Zero the water surface wave DISPLACEMENT — voxel_water.gdshader's vertex()
    // pushes the top face down by the wave field, so this makes water perfectly
    // flat while leaving its colour, ripple normals and reflections alone. Use it
    // to tell a geometry artefact from a shading one: anything that survives
    // flat water is not the displacement.
    //
    // Wave amplitude is derived from wind and pushed every frame by
    // SkyController.Apply(), so the gate lives there, not in this callback.
    public static CVarBool waterWavesDisabled = new CVarBool("water_waves_disabled", false, (cvar) => { });

    // Zero the ripple NORMAL perturbation (the small-scale surface chop that
    // bends reflections and specular). Geometry is untouched — pair with
    // water_waves_disabled to strip both.
    public static CVarBool waterRipplesDisabled = new CVarBool("water_ripples_disabled", false, (cvar) => { });

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
        if (Sim.Current == null || Sim.Current.player == null)
        {
            Godot.GD.Print("light_probe: no active world / player.");
            return;
        }
        Godot.Vector3 p = Sim.Current.player.GlobalPosition;
        int px = Godot.Mathf.FloorToInt(p.X);
        int py = Godot.Mathf.FloorToInt(p.Y);
        int pz = Godot.Mathf.FloorToInt(p.Z);
        WorldState ws = Sim.Current.WorldState;
        Godot.GD.Print($"light_probe at ({px},{py},{pz}):");
        for (int dy = 0; dy <= 5; dy++)
        {
            int wy = py + dy;
            int v = ws.GetBlockWorld(px, wy, pz);
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
        if (Sim.Current == null || Sim.Current.player == null)
        {
            Godot.GD.Print("light_leak: no active world / player.");
            return;
        }
        Godot.Vector3 p = Sim.Current.player.GlobalPosition;
        int px = Godot.Mathf.FloorToInt(p.X);
        int py = Godot.Mathf.FloorToInt(p.Y);
        int pz = Godot.Mathf.FloorToInt(p.Z);
        WorldState ws = Sim.Current.WorldState;
        const int RADIUS = 15;
        int playerSun = ws.GetSunlightWorld(px, py, pz);
        Godot.GD.Print($"light_leak around ({px},{py},{pz}) playerSun={playerSun}: scanning air at y={py} with sun > player.sun, sorted by distance");
        var hits = new System.Collections.Generic.List<(int dist, int wx, int wz, int sun)>();
        for (int dx = -RADIUS; dx <= RADIUS; dx++)
        {
            for (int dz = -RADIUS; dz <= RADIUS; dz++)
            {
                int wx = px + dx, wz = pz + dz;
                int v = ws.GetBlockWorld(wx, py, wz);
                if (v != Blocks.AirId) { continue; }
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
        if (Sim.Current == null || Sim.Current.player == null)
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
        Godot.Vector3 p = Sim.Current.player.GlobalPosition;
        Sim.AirTemperatureSample s = Sim.Current.SampleAirTemperatureBreakdown(p);
        Godot.GD.Print(
            $"temp at ({p.X:F1}, {p.Y:F1}, {p.Z:F1}): {s.Total:F1}°F\n" +
            $"  air        = {s.air:F1}°F\n" +
            $"  sun        = +{s.SunContribution:F1}°F  (sunT {s.sunTemperature:F1} × sunFactor {s.sunFactor:F2} × skyTransmission {s.skyTransmission:F2} × sunMask {s.sunMask:F2})\n" +
            $"  cloudCover = {s.cloudCover:F2}   fog = {s.fog:F2}");
    });

    // Prints the player's current world position and chunk coord.
    public static CVar whereAmI = new CVar("where", (cvar) =>
    {
        if (Sim.Current == null || Sim.Current.player == null)
        {
            Godot.GD.Print("where: no active world / player.");
            return;
        }
        Godot.Vector3 p = Sim.Current.player.GlobalPosition;
        Godot.Vector3I c = Sim.WorldToChunkCoord(p);
        Godot.GD.Print($"player pos=({p.X:F1}, {p.Y:F1}, {p.Z:F1})  chunk=({c.X}, {c.Y}, {c.Z})");
    });

// When non-empty, Main runs WorldGen on the default WorldGenData at
    // startup (bypassing the main menu), dumps plateau/height/ramp PPMs +
    // stats.txt to this directory, and quits. Use with `--headless` for a
    // fast-feedback debugging loop over the height-field algorithm.
    public static CVarString worldgenDebugDump = new CVarString("worldgen_debug_dump", "");

    // As above, but runs ONLY the terrain approach — no chunks, lighting, fog,
    // props, mobs, subscenes or roads. Same stats.txt and images out (plus the
    // raw int16 fields), in a fraction of the time, because none of those
    // passes change the height field. This is the loop for tuning a
    // TerrainGenData; use worldgen_debug_dump when you need what the later
    // passes did to the world (road regrading, in particular).
    public static CVarString worldgenTerrainDump = new CVarString("worldgen_terrain_dump", "");

    // When true, Main loads every .gdshader so the engine parses it, then quits
    // without starting a game. Pair with `--headless` for a ~4s "do the shaders
    // still compile" check instead of a full autostart run.
    public static CVarBool shaderCheck = new CVarBool("shader_check", false);

    // Does a transparent material with depth_draw_always cull a later
    // transparent draw behind it? WINDOWED only — the dummy renderer
    // rasterizes nothing, so headless always reports the clear colour.
    public static CVarBool depthSortCheck = new CVarBool("depth_sort_check", false);
    public static CVarBool blockCheck = new CVarBool("block_check", false);

    // Dumps every authored SpawnListData as its resolved rows and quits. A
    // spawn entry has no runtime error mode — a dropped density or condition
    // just silently stops placing something — so a diff of this output is how
    // an edit to those files is proved to have changed nothing else.
    public static CVarBool spawnCheck = new CVarBool("spawn_check", false);
    public static CVarBool waterShoreCheck = new CVarBool("water_shore_check", false);

    // Console command: dumps the most recently generated world's plateau/
    // height fields to user://worldgen_debug (outside the project tree).
    // Useful when a game is already running and you want a snapshot without
    // restarting.
    // Whether a finished Generate keeps its height field and terrain generator
    // alive for `worldgen_debug` to dump. Off by default: that is ~2MB of
    // generator scratch pinned for the whole session after the world it made has
    // been handed off. Turn it on before generating when you want the dump.
    public static CVarBool worldgenKeepDebugData = new CVarBool("worldgen_keep_debug_data", false);

    public static CVar worldgenDebug = new CVar("worldgen_debug", (cvar) =>
    {
        if (WorldGen.LastRun == null)
        {
            Godot.GD.Print("worldgen_debug: no run retained. Set worldgen_keep_debug_data 1 before generating.");
            return;
        }
        WorldGen.LastRun.DumpDebug(Godot.ProjectSettings.GlobalizePath("user://worldgen_debug"));
    });

// When true, Main skips the main menu and launches straight into a new game
    // (the standard-new-game path — respects `world_file` if set, else the
    // default WorldGenData). Intended for headless / automated runs; pair with
    // `--headless` and, for an unattended playthrough, `autoplay`.
    public static CVarBool autostart = new CVarBool("autostart", false);

    // When true, Main spawns a HeadlessBot that drives the player with
    // synthesized input (wander + occasional dash/attack), so a headless run
    // exercises movement, chunk streaming, and combat without a human at the
    // controls. No effect until a game is actually running.
    public static CVarBool autoplay = new CVarBool("autoplay", false);

    // When true, Main skips the main menu and opens the world editor instead.
    // Same intent as `autostart`, for iterating on the editor without clicking
    // through the menu. Ignored when `autostart` is also set.
    public static CVarBool autostartEditor = new CVarBool("autostart_editor", false);

    // Which of the menu's world templates `autostart` launches, as an index into
    // GuiMainMenu.worldOptions (the same order the New Game list shows). -1 uses
    // the menu's default. The only way to reach a non-default template headlessly.
    public static CVarInt worldGenIndex = new CVarInt("world_gen_index", -1);

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
        if (Sim.Current == null)
        {
            Godot.GD.PrintErr("world_export: no active world (start a game first).");
            return;
        }
        try
        {
            WorldFile.Write(path, Sim.Current.WorldState);
            Godot.GD.Print($"world_export: wrote {path}");
        }
        catch (System.Exception e)
        {
            Godot.GD.PrintErr($"world_export failed: {e.Message}");
        }
    });

    // Scene-level interior class: the SimData.interiorAmbiences index written
    // into every ENCLOSED cell of a subscene as it is saved. Lets an author say
    // "this whole cottage is a tidy building" without a per-cell brush.
    //
    // -1 (default) PRESERVES whatever the cells already carry, so re-saving an
    // existing scene can't silently overwrite its class — set it once when a
    // scene is first saved, then leave it alone. Per-cell paint, when it
    // exists, writes the same bytes and is overridden by a non-negative value
    // here, so leave this at -1 once you start painting.
    //
    // Outdoor cells are never touched: the class describes interiors, and a
    // scene's open-air margin should keep taking the destination's ambience.
    public static CVarInt subsceneInteriorClass = new CVarInt("subscene_interior_class", -1);

    // Action: RESCALES a painted world-map document and every layer file it
    // points at — the same world, bigger or smaller. Resampling is categorical,
    // so no value is ever averaged into existence: an elevation 6 beside an 8
    // stays a wall and never grows a 7 between them. Heights are not scaled with
    // the footprint. Acts on the document the painter has open — which is saved
    // and reopened around the change, so it is safe to run while painting.
    // Usage: `worldmap_resize <chunksX> <chunksZ> [res://path/to/world_map.tres]`
    public static CVarString worldMapResize = new CVarString("worldmap_resize", "", (cvar) =>
    {
        RunWorldMapExtentCommand((CVarString)cvar, "worldmap_resize", WorldMapResize.Run);
    });

    // Action: changes a painted world-map document's EXTENT without resampling
    // anything — every painted metre stays where it is in world space and the
    // map gains (or loses) ground around it. The one to reach for when a world
    // needs more room; `worldmap_resize` is for making the same world bigger.
    // Usage: `worldmap_canvas <chunksX> <chunksZ> [res://path/to/world_map.tres]`
    public static CVarString worldMapCanvas = new CVarString("worldmap_canvas", "", (cvar) =>
    {
        RunWorldMapExtentCommand((CVarString)cvar, "worldmap_canvas", WorldMapResize.Recanvas);
    });

    // Headless self-check for a painted world-map document: what the bake would
    // make of its water and which cascades it would file, without baking. The
    // painter's shader_check / block_check — a value, not an action, so Main
    // reads it after the command line is processed and quits on it.
    // Usage: `--headless -- "worldmap_check res://path/to/world_map.tres"`
    public static CVarString worldMapCheck = new CVarString("worldmap_check", "");

    // Action: prints the size of the open world-map document.
    //
    // Its own command because the console cannot route a bare `worldmap_resize`
    // here: a value CVar with no argument PRINTS its value rather than running
    // its callback (CVarRegistry.ProcessCommand), so "no size given" never
    // reaches the code below.
    public static CVar worldMapSize = new CVar("worldmap_size", (cvar) =>
    {
        PrintWorldMapSize(WorldMapPainter.LastDocument, "worldmap_size");
    });

    private static void PrintWorldMapSize(WorldMapData doc, string name)
    {
        if (doc == null)
        {
            Godot.GD.PrintErr($"{name}: no world map open — open the painter once, or pass a path.");
            return;
        }
        Godot.GD.Print($"{name}: {doc.sizeChunksX}x{doc.sizeChunksZ} chunks "
            + $"({doc.ImageWidth}x{doc.ImageHeight} m) — {Godot.StringExtensions.GetFile(doc.ResourcePath)}");
    }

    // Shared plumbing for the two extent commands: they take the same arguments
    // and differ only in what they do with them.
    //
    // The document defaults to the one the painter has open, so the usual call
    // is two numbers. A path can still be given as a third argument, for a
    // document that has not been opened this session.
    private static void RunWorldMapExtentCommand(CVarString cvar, string name,
        System.Func<WorldMapData, int, int, bool> action)
    {
        string[] parts = (cvar.Value ?? "").Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        WorldMapData doc = WorldMapPainter.LastDocument;
        if (parts.Length >= 3)
        {
            doc = Godot.ResourceLoader.Load<WorldMapData>(parts[2]);
            if (doc == null)
            {
                Godot.GD.PrintErr($"{name}: could not load '{parts[2]}' as a WorldMapData.");
                return;
            }
        }
        if (doc == null)
        {
            Godot.GD.PrintErr($"{name}: no world map open — open the painter once, or pass a path. "
                + $"Usage `{name} <chunksX> <chunksZ> [res://path/to/world_map.tres]`");
            return;
        }
        // No size, or a size that does not parse: say what the size IS, which is
        // the question anyone is about to ask before choosing a new one.
        if (parts.Length < 2
            || !int.TryParse(parts[0], out int chunksX)
            || !int.TryParse(parts[1], out int chunksZ))
        {
            PrintWorldMapSize(doc, name);
            Godot.GD.Print($"{name}: usage `{name} <chunksX> <chunksZ> [res://path/to/world_map.tres]`");
            return;
        }
        try
        {
            // With the painter open, go through it: it holds unsaved painting
            // and every buffer sized by the map, so it has to save first and
            // reopen the result. Closed, the plain call is enough.
            WorldMapPainter painter = WorldMapPainter.Current;
            if (painter != null && Godot.GodotObject.IsInstanceValid(painter) && painter.Document == doc)
            {
                painter.ApplyExtentChange(action, chunksX, chunksZ);
            }
            else
            {
                action(doc, chunksX, chunksZ);
            }
        }
        catch (System.Exception e)
        {
            Godot.GD.PrintErr($"{name} failed: {e.Message}");
        }
    }

    // Action: converts a packed world file into a subscene file, auto-fitting
    // the bbox to its voxels — the headless equivalent of opening the world in
    // the editor and running `subscene_save`. Needs no editor and no running
    // game. The destination defaults to the standard scene dir, same file stem.
    // Usage: `subscene_from_world user://house01.hike [res://path/out.hikescene]`
    public static CVarString subsceneFromWorld = new CVarString("subscene_from_world", "", (cvar) =>
    {
        string arg = ((CVarString)cvar).Value;
        if (string.IsNullOrEmpty(arg))
        {
            return;
        }
        string[] parts = arg.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        string src = parts[0];
        string dst = parts.Length > 1
            ? parts[1]
            : Godot.StringExtensions.PathJoin(
                SubsceneFile.DEFAULT_SCENE_DIR,
                $"{Godot.StringExtensions.GetBaseName(Godot.StringExtensions.GetFile(src))}.{WorldEditor.SCENE_FILE_EXTENSION}");
        try
        {
            WorldState ws = Main.LoadWorldFromFile(src);
            if (!SubsceneBuilder.TryGetContentBounds(ws, out Godot.Vector3I min, out Godot.Vector3I max))
            {
                Godot.GD.PrintErr($"subscene_from_world: '{src}' has no voxels to save.");
                return;
            }
            SubsceneState sub = SubsceneBuilder.Build(ws, min, max, includeEnv: false, filterEntitiesToBox: false);
            SubsceneFile.Write(dst, sub);
            Godot.GD.Print($"subscene_from_world: wrote {dst} (bbox min={min} max={max} size={sub.Size}, entities={sub.Entities.Count})");
        }
        catch (System.Exception e)
        {
            Godot.GD.PrintErr($"subscene_from_world failed: {e.Message}");
        }
    });

    // Action: prints what a `.hikescene` contains without opening it — bbox,
    // anchor, entity count, and the variant pools it defines with how many
    // positions each offers. Needs no editor and no running game. The pool list
    // comes from the file's baked directory, so it answers "what can a variant
    // pick from here?" without decoding the voxel body.
    // Usage: `subscene_info res://resources/data/world_authoring/subscenes/house01.hikescene`
    public static CVarString subsceneInfo = new CVarString("subscene_info", "", (cvar) =>
    {
        string path = ((CVarString)cvar).Value;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            SubsceneDirectory directory = SubsceneFile.ReadDirectory(path);
            SubsceneState sub = SubsceneFile.Read(path);
            Godot.GD.Print($"subscene_info: {path} size={sub.Size} anchor={sub.Anchor} entities={sub.Entities.Count}");
            // Path hints are read off the entity list rather than the baked
            // directory: the directory lists variant POOLS, and a hint is a road
            // endpoint, not a position a variant may fill.
            var hints = new System.Collections.Generic.List<string>();
            foreach (EntitySimState entity in sub.Entities)
            {
                if (entity is PathHintSimState hint)
                {
                    hints.Add(string.IsNullOrEmpty(hint.Tag) ? "<untagged>" : hint.Tag);
                }
            }
            if (hints.Count > 0)
            {
                Godot.GD.Print($"  path hints: {string.Join(", ", hints)}");
            }
            if (directory.Entries.Length == 0)
            {
                Godot.GD.Print("  pools: none (every entity is unconditional)");
                return;
            }
            foreach (SubsceneDirectory.Entry entry in directory.Entries)
            {
                Godot.GD.Print($"  pool '{entry.Tag}': {entry.Count} position(s)");
            }
        }
        catch (System.Exception e)
        {
            Godot.GD.PrintErr($"subscene_info failed: {e.Message}");
        }
    });

    // Subscene authoring commands. All require an active WorldEditor — they
    // no-op (with an error log) outside editor mode. The editor maintains
    // the corner selection; these CVars are just the console surface.
    //
    // The normal way to author a subscene is to open a Scene document from the
    // menu (New Scene, or an existing `.hikescene`) and press Ctrl+S — these
    // commands are the manual path, for saving a scene out of a World document
    // or stamping one into it:
    //   - `subscene_save <path>` writes the world's voxels as a scene (the
    //     `_env` variant also bakes Wind/EnvTag, for castles/dungeons that must
    //     override the destination's ambience). The bbox auto-fits every voxel
    //     in the world unless corners are pinned.
    //   - `subscene_stamp <path>` pastes one at the cursor, which becomes the
    //     placement anchor — the subscene's bbox-min lands there (the anchor
    //     defaults to (0,0,0) at save time).
    //
    // `subscene_corner` twice pins an explicit bbox instead, for carving one
    // piece out of a larger world; `subscene_corner_clear` returns to
    // auto-fit.
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
    // --- Setup verbs -----------------------------------------------------
    // The console could observe the running game and set global state, but not
    // put the player somewhere specific with specific company — so reaching a
    // test condition meant walking there in real time, on every check. These
    // four collapse that. All are CVarString rather than action CVars because
    // ProcessCommand DISCARDS the argument of a CVarType.None cvar.

    // `tp <poi>` | `tp <x> <y> <z>` — move the living party. Bare `tp` lists the
    // world's points of interest.
    public static CVarString teleport = new CVarString("tp", "", (cvar) =>
    {
        DebugVerbs.Teleport(((CVarString)cvar).Value);
    });

    // `spawn <species> [count] [level]` — ring of transient mobs around the
    // player. Bare `spawn` lists the known species names.
    public static CVarString spawnMob = new CVarString("spawn", "", (cvar) =>
    {
        DebugVerbs.Spawn(((CVarString)cvar).Value);
    });

    // `give <item> [count]` — drop an item at the player's feet (the world
    // pickup path, which is where several item kinds do their real work). Bare
    // `give` lists the known item names.
    public static CVarString giveItem = new CVarString("give", "", (cvar) =>
    {
        DebugVerbs.Give(((CVarString)cvar).Value);
    });

    // `setup <name>` — run an authored scenario's command list (SimData
    // .testScenarios). Bare `setup` lists them with their descriptions.
    public static CVarString setupScenario = new CVarString("setup", "", (cvar) =>
    {
        DebugVerbs.Setup(((CVarString)cvar).Value);
    });

    // Headless data-integrity check: `--headless -- "resource_check 1"` reports
    // [Tool]-closure gaps and any .tres that fails to load, then quits. The
    // data-side twin of shader_check / block_check.
    public static CVarBool resourceCheck = new CVarBool("resource_check", false);

    // Unattended driver for the setup verbs. CLI cvar args run in Main._Ready,
    // before a world exists, so a launch line cannot call tp / spawn / give /
    // setup directly — they need a running game. `exec` holds a semicolon-
    // separated command line and `exec_delay` the seconds to wait after the
    // game scene comes up:
    //   -- "autostart 1" "exec_delay 25" "exec setup night_cascade"
    // Ends the process. Makes an `exec` chain self-terminating — without it an
    // unattended run has no reason to stop and burns its whole timeout, so wall
    // clock says nothing about what the run actually cost.
    public static CVar quit = new CVar("quit", (cvar) =>
    {
        (Godot.Engine.GetMainLoop() as Godot.SceneTree)?.Quit();
    });

    public static CVarString exec = new CVarString("exec", "");
    public static CVarFloat execDelay = new CVarFloat("exec_delay", 0f);
}
