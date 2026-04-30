public static class CVars
{
    public static CVarString savePath = new CVarString("savepath", "./savegame.dat");
    public static CVarString language = new CVarString("language", "");
    public static CVar version = new CVar("version", (cvar) => Godot.GD.Print(Version.Full));
    public static CVarBool ceilingCap = new CVarBool("ceiling_cap", true);

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

    // Detaches the game camera from the player and lets WASD + right-mouse-look
    // fly it freely. Disables pixel snapping while active so mouse-look is smooth.
    public static CVarBool debugFlyCam = new CVarBool("debug_flycam", false);

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

    // Atmospheric visual state — sky dome, clouds, sun tint, fog haze,
    // inscatter shafts, animated dust — is derived each frame by
    // SkyController from (RegionData, WeatherData, time-of-day) via
    // WeatherDerivation. Regions live on SimData (4-quadrant scaffolding);
    // authoring new looks means editing RegionData.tres + WeatherData.tres
    // (or the derivation tuning group on SimData), not CVars.

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
    // pixel size (linear); 1 disables chunking. DOF removed — reintroduce
    // inside the scene environment later if wanted.
    public static CVarFloat vignetteRadius = new CVarFloat("vignette_radius", 0.55f);
    public static CVarFloat vignetteSoftness = new CVarFloat("vignette_softness", 0.45f);
    public static CVarFloat vignetteStrength = new CVarFloat("vignette_strength", 0.5f);
    public static CVarInt pixelScale = new CVarInt("pixel_scale", 4);

    // When true, Mob._PhysicsProcess prints yaw/angular-velocity diagnostics
    // each frame for alive mobs. Used to diagnose yaw oscillation.
    public static CVarBool debugMobYaw = new CVarBool("debug_mob_yaw", false);

    // When true, draws each alive mob's active path as line segments via
    // DebugDraw — green for upcoming waypoints, yellow for the current
    // segment from the mob to its next waypoint, red dot at the goal.
    // Off by default; toggle from the in-game console.
    public static CVarBool debugMobPath = new CVarBool("debug_mob_path", false);

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

    // Rolling window length (seconds) for the on-screen overlay and the
    // Godot custom monitors. Every `profile_window` seconds the live
    // accumulators latch into a "previous window" snapshot that the overlay
    // reads, then reset. Smaller = more responsive table, more churn.
    // Larger = more stable averages, slower reaction to scene changes.
    public static CVarFloat profileWindow = new CVarFloat("profile_window", 1f);

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
    public static CVarBool debugDC = new CVarBool("debug_dc", false, (cvar) =>
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
    //       region the water cap is allowed to draw in.
    //   4 = solid backface stencil (voxel_backface_stencil.gdshader) → adds
    //       GREEN wherever stencil=1 is being written. Shows the screen
    //       region the ceiling cap is allowed to draw in.
    //   5 = water cap disabled entirely. If the artifact disappears, the
    //       water cap was drawing it. If it persists, look elsewhere.
    //   6 = ceiling cap disabled entirely.
    //   7 = voxel_water front face (voxel_water.gdshader) → bright YELLOW.
    //       Shows where actual water voxel surfaces (not the cap) draw.
    //   8 = voxel_water clip-line predicate viz. RED = fragment evaluates
    //       `world_vertex.y > camera_clip` (would have been discarded).
    //       GREEN = below clip (legitimate). If you see RED water
    //       surfaces, the discard isn't firing — either camera_clip is
    //       +inf / wrong, or the global isn't seeded on this material.
    //   9 = voxel_water `world_vertex.y` as grayscale (mod 16). Should be
    //       horizontal bands; flat = the varying isn't reaching fragment.
    //  10 = voxel_water `camera_clip` global as grayscale (mod 16). Flat
    //       uniform color across all water fragments. Pure white = global
    //       reading as huge / +inf; pure black = global never set on this
    //       material.
    //  11 = voxel_water face-type visualizer. CYAN = top face, MAGENTA =
    //       bottom face, YELLOW = side face. Use this when water bleeds
    //       through the cap to see which mesh face the leak is on; the
    //       fix is in WaterMesher (cull that face under sealed-pocket
    //       conditions). Top faces against solid are already culled.
    //  13 = full cap+stencil visualizer. Color legend:
    //         BLUE          = visible terrain (voxel_clip emission)
    //         RED           = clip_cap drawing (stencil=1 read region)
    //         MAGENTA       = water_clip_cap drawing (stencil=2 read region)
    //         GREEN (added) = voxel_backface_stencil writing stencil=1
    //         CYAN  (added) = voxel_water_backface writing stencil=2
    //         Anything else = sky / sprites / water / fog (other shaders)
    //       Cap regions and matching stencil-write regions should overlap.
    //       Pair with `water_hide 1` to drop water front faces.
    //  14 = same as 13 but voxel_backface_stencil also runs ABOVE the clip
    //       line and paints YELLOW there. Diagnostic for "stencil coverage
    //       we'd get if we removed the above-clip discard". If yellow
    //       appears over a water poke-through region that mode 13 left
    //       bare, the existing discard is killing useful coverage.
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

    // Discards every voxel_water fragment when set. Lets you check the
    // terrain stencil + cap pipeline without water front faces in the
    // way — particularly useful with `clip_debug 13` to see which screen
    // pixels get stencil writes vs which are bare scene.
    public static CVarBool waterHide = new CVarBool("water_hide", false, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("water_hide", ((CVarBool)cvar).Value);
    });

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

    // Multiplier applied to the sun-visibility lightmap channel. Peak values
    // >1.0 push sunlit terrain above the glow HDR threshold so bloom has
    // something to feed on (Environment_bloom sets glow_hdr_threshold = 1.0
    // and tonemap_mode = Filmic, so values above 1 bloom and roll off
    // instead of clamping flat). Block lights (torches) can still add on
    // top — the lightmap format is what caps their headroom, not this.
    // Will be driven by the day/night simulation once it exists; today
    // it's a static tuning value.
    public static CVarFloat sunIntensity = new CVarFloat("sun_intensity", 2f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("sun_intensity", ((CVarFloat)cvar).Value);
    });

    // RGB tint applied to the sun visibility mask. Day/night will drive this:
    // warm at dawn/dusk, cool at noon. Parsed from "r g b" floats. The shader
    // sees a vec3 — the day/night sim will eventually call SetSunColor instead
    // of going through this CVar each frame, so the parsing cost only matters
    // for console tweaks.
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
    // height fields to ./worldgen_debug. Useful when a game is already
    // running and you want a snapshot without restarting.
    public static CVar worldgenDebug = new CVar("worldgen_debug", (cvar) =>
    {
        WorldGen.DumpDebug(Godot.ProjectSettings.GlobalizePath("res://worldgen_debug"));
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
}