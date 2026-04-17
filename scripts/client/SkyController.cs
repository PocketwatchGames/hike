using Godot;

// Owns the global shader parameters consumed by shaders/sky_common.gdshaderinc
// (sky.gdshader + voxel_water.gdshader both read from them). Registers the
// globals up-front and pushes per-frame values — sun direction from
// WorldState, cloud scroll driven by elapsed time, CVar-driven reflection
// strength for water.
[GlobalClass]
public partial class SkyController : Node3D
{
    [Export] public Color horizonColor = new Color(0.72f, 0.82f, 0.92f);
    [Export] public Color zenithColor = new Color(0.25f, 0.48f, 0.82f);
    [Export] public Color sunColor = new Color(1.0f, 0.96f, 0.88f);
    // Sky cloud visuals (sky_common.gdshaderinc). cloud_scale is shared with
    // the terrain cloud shadow system — both use the same global.
    [Export] public Color cloudColor = new Color(1.0f, 0.98f, 0.95f);
    [Export] public Vector2 cloudScroll = new Vector2(0.004f, 0.002f);
    // Cloud density remap: the baked noise texture value is remapped via
    // smoothstep(threshold, threshold + (1 - sharpness), noise). Lower
    // threshold = more coverage. Higher sharpness = harder cloud edges
    // (0 = soft gradient, 1 = hard step). Both shared between sky dome
    // and ground-shadow projection so sky clouds match their shadows.
    [Export] public float cloudThreshold = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudSharpness = 0.7f;
    [Export] public float cloudScale = 0.15f;
    // World Y of the flat cloud plane used for projective sun-shadow casting
    // (see shaders/cloud_shadow.gdshaderinc). The sky dome renders clouds at
    // "infinity" via sky_common, so this altitude isn't directly visible —
    // it controls how far along the sun direction ground points project to
    // sample the cloud pattern. Too low: shadows track the ground too
    // closely (feels like stamps). Too high: shadow XZ offset grows large,
    // so the shadows feel decoupled from anything overhead. 40–80 usually
    // reads well for the current world scale.
    [Export] public float cloudAltitude = 60f;
    // How aggressively a fully-shadowed cloud occludes direct sun.
    // 1.0 = fully shadowed (cloud kills all sun, leaving only block light);
    // 0.6 ≈ previous "ATTENUATION-only" behaviour (direct sun killed, sky-bounce
    // ambient survives); 0.0 = clouds produce no terrain shadow at all.
    [Export] public float cloudShadowStrength = 1.0f;
    // Reverse directional shading: each tint is the color a face is multiplied
    // by when it faces fully away from the corresponding light. White = no
    // effect; darker/saturated colors darken and tint backfacing surfaces.
    [Export] public Color sunTintColor = new Color(0.15f, 0.15f, 0.35f);
    [Export] public Color fillTintColor = new Color(0.5f, 0.5f, 0.5f);
    // Fill light pitch below horizon (degrees) and yaw offset from the sun.
    [Export] public float fillPitchDegrees = 35f;
    [Export] public float fillYawOffsetDegrees = 135f;

    public override void _Ready()
    {
        // All static (non-dynamic) globals are seeded here ONCE. The old code
        // pushed them every frame from _Process, which broke CVar overrides:
        // console commands like `cloud_strength 0.5` were overwritten on the
        // very next frame by the export default. _Process now only pushes
        // genuinely dynamic values (sun direction).
        ShaderGlobals.Register("sun_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(-0.215f, -0.819f, -0.532f));
        ShaderGlobals.Register("fill_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Down);
        ShaderGlobals.Register("sun_tint_color", RenderingServer.GlobalShaderParameterType.Vec3, ColorToVec3(sunTintColor));
        ShaderGlobals.Register("fill_tint_color", RenderingServer.GlobalShaderParameterType.Vec3, ColorToVec3(fillTintColor));
        ShaderGlobals.Register("sun_color", RenderingServer.GlobalShaderParameterType.Vec3, ColorToVec3(sunColor));
        ShaderGlobals.Register("horizon_color", RenderingServer.GlobalShaderParameterType.Vec3, ColorToVec3(horizonColor));
        ShaderGlobals.Register("zenith_color", RenderingServer.GlobalShaderParameterType.Vec3, ColorToVec3(zenithColor));
        ShaderGlobals.Register("cloud_color", RenderingServer.GlobalShaderParameterType.Vec3, ColorToVec3(cloudColor));
        ShaderGlobals.Register("cloud_scroll", RenderingServer.GlobalShaderParameterType.Vec2, cloudScroll);
        ShaderGlobals.Register("cloud_threshold", RenderingServer.GlobalShaderParameterType.Float, cloudThreshold);
        ShaderGlobals.Register("cloud_sharpness", RenderingServer.GlobalShaderParameterType.Float, cloudSharpness);
        ShaderGlobals.Register("cloud_scale", RenderingServer.GlobalShaderParameterType.Float, cloudScale);
        ShaderGlobals.Register("cloud_altitude", RenderingServer.GlobalShaderParameterType.Float, cloudAltitude);
        ShaderGlobals.Register("cloud_shadow_strength", RenderingServer.GlobalShaderParameterType.Float, cloudShadowStrength);
    }

    public override void _Process(double delta)
    {
        // Only genuinely dynamic state — sun direction (driven by the day/night
        // sim) and its derived fill direction. Other values have CVar callbacks
        // or are tuned via exports at game start; pushing them every frame
        // would clobber runtime overrides.
        Vector3 sunDir = World.Current?.WorldState?.ShadowLightDirection ?? new Vector3(-0.215f, -0.819f, -0.532f);
        Vector3 fillDir = ComputeFillDirection(sunDir, fillPitchDegrees, fillYawOffsetDegrees);
        RenderingServer.GlobalShaderParameterSet("sun_world_dir", sunDir);
        RenderingServer.GlobalShaderParameterSet("fill_world_dir", fillDir);
    }

    private static Vector3 ComputeFillDirection(Vector3 sunDir, float pitchDeg, float yawOffsetDeg)
    {
        // Sun yaw from its horizontal travel direction.
        float sunYaw = Mathf.Atan2(sunDir.X, sunDir.Z);
        float fillYaw = sunYaw + Mathf.DegToRad(yawOffsetDeg);
        float pitch = Mathf.DegToRad(pitchDeg);
        float horiz = Mathf.Cos(pitch);
        Vector3 dir = new Vector3(horiz * Mathf.Sin(fillYaw), -Mathf.Sin(pitch), horiz * Mathf.Cos(fillYaw));
        return dir.Normalized();
    }

    private static Vector3 ColorToVec3(Color c)
    {
        return new Vector3(c.R, c.G, c.B);
    }
}
