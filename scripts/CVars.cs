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