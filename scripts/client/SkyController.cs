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
    [Export] public Color sunColor = new Color(1.0f, 0.92f, 0.72f);
    // Sky cloud visuals (sky_common.gdshaderinc). cloud_scale is shared with
    // the terrain cloud shadow system — both use the same global.
    [Export] public Color cloudColor = new Color(1.0f, 0.98f, 0.95f);
    [Export] public Vector2 cloudScroll = new Vector2(0.004f, 0.002f);
    [Export] public float cloudCoverage = 0.45f;
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
        ShaderGlobals.Register("sun_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(-0.215f, -0.819f, -0.532f));
        ShaderGlobals.Register("fill_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Down);
        ShaderGlobals.Register("sun_tint_color", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        ShaderGlobals.Register("fill_tint_color", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        ShaderGlobals.Register("sun_color", RenderingServer.GlobalShaderParameterType.Vec3, ColorToVec3(sunColor));
        ShaderGlobals.Register("horizon_color", RenderingServer.GlobalShaderParameterType.Vec3, ColorToVec3(horizonColor));
        ShaderGlobals.Register("zenith_color", RenderingServer.GlobalShaderParameterType.Vec3, ColorToVec3(zenithColor));
        ShaderGlobals.Register("cloud_color", RenderingServer.GlobalShaderParameterType.Vec3, ColorToVec3(cloudColor));
        ShaderGlobals.Register("cloud_scroll", RenderingServer.GlobalShaderParameterType.Vec2, cloudScroll);
        ShaderGlobals.Register("cloud_coverage", RenderingServer.GlobalShaderParameterType.Float, cloudCoverage);
    }

    public override void _Process(double delta)
    {
        Vector3 sunDir = World.Current?.WorldState?.ShadowLightDirection ?? new Vector3(-0.215f, -0.819f, -0.532f);
        Vector3 fillDir = ComputeFillDirection(sunDir, fillPitchDegrees, fillYawOffsetDegrees);
        RenderingServer.GlobalShaderParameterSet("sun_world_dir", sunDir);
        RenderingServer.GlobalShaderParameterSet("fill_world_dir", fillDir);
        RenderingServer.GlobalShaderParameterSet("sun_tint_color", ColorToVec3(sunTintColor));
        RenderingServer.GlobalShaderParameterSet("fill_tint_color", ColorToVec3(fillTintColor));
        RenderingServer.GlobalShaderParameterSet("sun_color", ColorToVec3(sunColor));
        RenderingServer.GlobalShaderParameterSet("horizon_color", ColorToVec3(horizonColor));
        RenderingServer.GlobalShaderParameterSet("cloud_color", ColorToVec3(cloudColor));
        RenderingServer.GlobalShaderParameterSet("cloud_scroll", cloudScroll);
        RenderingServer.GlobalShaderParameterSet("cloud_coverage", cloudCoverage);
        RenderingServer.GlobalShaderParameterSet("zenith_color", ColorToVec3(zenithColor));
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
