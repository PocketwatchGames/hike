public static class CVars
{
    public static CVarString savePath = new CVarString("savepath", "./savegame.dat");
    public static CVarString language = new CVarString("language", "");
    public static CVar version = new CVar("version", (cvar) => Godot.GD.Print(Version.Full));
    public static CVarBool ceilingCap = new CVarBool("ceiling_cap", true);

    // Detaches the game camera from the player and lets WASD + right-mouse-look
    // fly it freely. Disables pixel snapping while active so mouse-look is smooth.
    public static CVarBool debugFlyCam = new CVarBool("debug_flycam", false);

    // Post-process. Vignette cvars feed the post_process canvas_item shader;
    // GameClient pushes them each frame. Pixel-art scale controls the chunky
    // pixel size (linear); 1 disables chunking. DOF removed — reintroduce
    // inside the scene environment later if wanted.
    public static CVarFloat vignetteRadius = new CVarFloat("vignette_radius", 0.55f);
    public static CVarFloat vignetteSoftness = new CVarFloat("vignette_softness", 0.45f);
    public static CVarFloat vignetteStrength = new CVarFloat("vignette_strength", 0.5f);
    public static CVarInt pixelScale = new CVarInt("pixel_scale", 4);

    // Strength of the projected-shadow darkening (0 = no shadows, 1 = full
    // darkening of affected fragments). Consumed by voxel_clip and sprite_lit
    // via the shadow_strength global shader uniform.
    // Multiplier applied on top of WorldState.ShadowStrength (the sim-driven
    // value). 1.0 keeps the simulation's strength unchanged; 0 disables
    // shadows entirely. Useful for visual tuning without touching sim state.
    public static CVarFloat shadowStrengthMultiplier = new CVarFloat("shadow_strength_mul", 1f);

    // When true, Mob._PhysicsProcess prints yaw/angular-velocity diagnostics
    // each frame for alive mobs. Used to diagnose yaw oscillation.
    public static CVarBool debugMobYaw = new CVarBool("debug_mob_yaw", false);

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

    // Power applied to the lightmap value in voxel/sprite/water shaders.
    // 1.0 = linear (raw BFS value), >1 darkens the mid-range so dim sunlight
    // bleed reads as proper darkness while bright areas stay bright.
    public static CVarFloat lightFalloffExp = new CVarFloat("light_falloff_exp", 2f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("light_falloff_exp", ((CVarFloat)cvar).Value);
    });

    // Multiplier applied to the sun-visibility lightmap channel. <1 keeps
    // sunlit areas below max brightness so block lights (torches) have
    // headroom to add visibly on top. Will be driven by the day/night
    // simulation once it exists; today it's a static tuning value.
    public static CVarFloat sunIntensity = new CVarFloat("sun_intensity", 0.85f, (cvar) =>
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

    // Cloud shadow parameters. Tunable at runtime via console.
    public static CVarFloat cloudScale = new CVarFloat("cloud_shadow_scale", 0.005f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("cloud_shadow_scale", ((CVarFloat)cvar).Value);
    });
    public static CVarFloat cloudSpeed = new CVarFloat("cloud_speed", 0.3f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("cloud_speed", ((CVarFloat)cvar).Value);
    });
    public static CVarFloat cloudCutoff = new CVarFloat("cloud_cutoff", 0.4f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("cloud_cutoff", ((CVarFloat)cvar).Value);
    });
    public static CVarFloat cloudPower = new CVarFloat("cloud_power", 3.0f, (cvar) =>
    {
        Godot.RenderingServer.GlobalShaderParameterSet("cloud_power", ((CVarFloat)cvar).Value);
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