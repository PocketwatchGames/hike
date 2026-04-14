public static class CVars
{
    public static CVarString savePath = new CVarString("savepath", "./savegame.dat");
    public static CVarString language = new CVarString("language", "");
    public static CVar version = new CVar("version", (cvar) => Godot.GD.Print(Version.Full));
    public static CVarBool ceilingCap = new CVarBool("ceiling_cap", true);

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