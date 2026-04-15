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
    [Export] public Color cloudColor = new Color(1.0f, 0.98f, 0.95f);
    [Export] public Vector2 cloudScroll = new Vector2(0.004f, 0.002f);
    [Export] public float cloudScale = 2.5f;
    [Export] public float cloudCoverage = 0.45f;

    // Reverse directional shading: each tint is the color a face is multiplied
    // by when it faces fully away from the corresponding light. White = no
    // effect; darker/saturated colors darken and tint backfacing surfaces.
    [Export] public Color sunTintColor = new Color(0.15f, 0.15f, 0.35f);
    [Export] public Color fillTintColor = new Color(0.5f, 0.5f, 0.5f);
    // Fill light pitch below horizon (degrees) and yaw offset from the sun.
    [Export] public float fillPitchDegrees = 35f;
    [Export] public float fillYawOffsetDegrees = 135f;

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
        RenderingServer.GlobalShaderParameterSet("zenith_color", ColorToVec3(zenithColor));
        RenderingServer.GlobalShaderParameterSet("cloud_color", ColorToVec3(cloudColor));
        RenderingServer.GlobalShaderParameterSet("cloud_scroll", cloudScroll);
        RenderingServer.GlobalShaderParameterSet("cloud_scale", cloudScale);
        RenderingServer.GlobalShaderParameterSet("cloud_coverage", cloudCoverage);
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
