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

    public override void _Process(double delta)
    {
        Vector3 sunDir = World.Current?.WorldState?.ShadowLightDirection ?? new Vector3(-0.215f, -0.819f, -0.532f);
        RenderingServer.GlobalShaderParameterSet("sun_world_dir", sunDir);
        RenderingServer.GlobalShaderParameterSet("sun_color", ColorToVec3(sunColor));
        RenderingServer.GlobalShaderParameterSet("horizon_color", ColorToVec3(horizonColor));
        RenderingServer.GlobalShaderParameterSet("zenith_color", ColorToVec3(zenithColor));
        RenderingServer.GlobalShaderParameterSet("cloud_color", ColorToVec3(cloudColor));
        RenderingServer.GlobalShaderParameterSet("cloud_scroll", cloudScroll);
        RenderingServer.GlobalShaderParameterSet("cloud_scale", cloudScale);
        RenderingServer.GlobalShaderParameterSet("cloud_coverage", cloudCoverage);
    }

    private static Vector3 ColorToVec3(Color c)
    {
        return new Vector3(c.R, c.G, c.B);
    }
}
