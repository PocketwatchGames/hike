using Godot;

// Top-down ground-stain projector. Renders flat ground marks (scorch,
// footprints, blood, worn paths) into a small SubViewport via an orthographic
// camera looking straight down, then publishes the result as
// `ground_stain_tex` for the lit ground shaders. Each stain source contributes
// a proxy MeshInstance3D (a flat quad carrying the stain texture) on the stain
// projector layer; lit ground shaders sample the projector texture at each
// fragment's world position and composite it into the surface BASE color
// before the lighting split (see shaders/ground_stain.gdshaderinc).
//
// This is the sibling of BlockLightShadowProjector and shares its design: a
// world-space top-down render sampled per-fragment by world position. The
// difference is what it carries (stain color + coverage vs. shadow coverage)
// and that it projects straight down (stains lie flat on the ground; there is
// no drop-shadow tilt).
//
// World->UV is decomposed into origin (vec3) + right (vec3) + up (vec3) + size
// (float) globals — see _Process. mat4 globals don't round-trip through
// project.godot's [shader_globals] section reliably in Godot 4.6.
//
// CVar `ground_stain` is the on/off toggle. When off the SubViewport stops
// rendering and the lit shaders branch around the sample (see
// `ground_stain_enabled` global) so rendering is byte-identical to pre-feature.
//
// Scene wiring mirrors BlockLightShadowProjector: parented inside
// SceneViewport, with an exported SubViewport child (own_world_3d=false so it
// shares SceneViewport's World3D) and an orthographic Camera3D child of that
// SubViewport.
[GlobalClass]
public partial class GroundStainProjector : Node3D
{
    // Orthographic frustum half-extent (world units). Should at least cover the
    // area the player can see — anything outside reads zero stain. Matches the
    // block-light projector's pad around the iso camera's visible footprint.
    [Export(PropertyHint.Range, "8,128,1")] public float radiusWorld = 40f;

    // RT size. 512 ~ 16 cm/texel at radiusWorld=40 — keeps stain edges crisp at
    // the pixel-art scale without burning meaningful VRAM (~1 MB at RGBA8).
    [Export] public Vector2I textureSize = new Vector2I(512, 512);

    // Master composite weight pushed to `ground_stain_strength`. 1.0 = stains
    // fully replace the surface color at full coverage; lower = subtler. The
    // per-stain texture alpha still feathers each mark's edges.
    [Export(PropertyHint.Range, "0,1,0.01")] public float strength = 0.85f;

    // Distance the camera sits above the focal plane. Far enough that all flat
    // stain quads (which sit ~at ground level) are well inside the frustum.
    private const float CAMERA_HEIGHT = 100f;

    // Visual layer the stain proxies live on (layer 5 = bit 4). Distinct from
    // the default layers (terrain/sprites) and from the block-light shadow
    // proxy layer (4). The projector camera's CullMask matches this; the main
    // camera's CullMask excludes it so stain quads never render to the screen
    // directly — only into this projector.
    public const uint STAIN_PROXY_LAYER_MASK = 1u << 4; // layer 5

    [Export] public SubViewport viewport;
    [Export] public Camera3D camera;

    public override void _Ready()
    {
        if (Engine.IsEditorHint())
        {
            return;
        }

        viewport.Size = textureSize;
        // own_world_3d=false: this nested SubViewport renders the parent
        // SceneViewport's World3D — the same chunks/entities the main camera
        // sees, so stain proxies parented to world entities are visible here.
        viewport.OwnWorld3D = false;
        // Transparent so the RT clears to (0,0,0,0); lit shaders read coverage
        // from .a, which must be 0 outside any stain.
        viewport.TransparentBg = true;

        camera.Projection = Camera3D.ProjectionType.Orthogonal;
        camera.Size = radiusWorld * 2f;
        camera.Near = 0.1f;
        camera.Far = CAMERA_HEIGHT * 2f;
        camera.CullMask = STAIN_PROXY_LAYER_MASK;
        camera.Current = true;

        // Only the texture global needs runtime seeding: project.godot points it
        // at a 1x1 placeholder so shader compile works, and we swap in the live
        // SubViewport texture here. The other globals take their project.godot
        // defaults until _Process pushes real values. (RegisterRuntime would
        // race standalone-launch shader compile — see CLAUDE.md.)
        ShaderGlobals.Register("ground_stain_tex", RenderingServer.GlobalShaderParameterType.Sampler2D, viewport.GetTexture());

        CVars.groundStain.OnChanged += OnGroundStainChanged;
        ApplyEnabled(CVars.groundStain.Value);
    }

    public override void _ExitTree()
    {
        CVars.groundStain.OnChanged -= OnGroundStainChanged;
        // Unbind the SubViewport texture before it dies with this node.
        ShaderGlobals.ResetToProjectDefault("ground_stain_tex");
    }

    private void OnGroundStainChanged(CVar cvar)
    {
        ApplyEnabled(((CVarBool)cvar).Value);
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("GroundStainProjector.Process");
        if (Engine.IsEditorHint() || !CVars.groundStain.Value)
        {
            return;
        }

        Vector3 focal = Sim.Current?.player?.GlobalPosition ?? Vector3.Zero;

        // Snap the focal point to this projector's own texel grid in world XZ.
        // The RT is low-res (~16 cm/texel), so a continuously-moving orthographic
        // origin rasterizes stain edges onto different texels each frame — the
        // marks crawl/shimmer against the pixel-snapped terrain as the player
        // moves (no shimmer standing still). Advancing the origin in whole-texel
        // steps pins every world point to a stable sub-texel position (the
        // standard shadow-map texel-snap); render and sample share this origin,
        // so the per-texel jump itself is invisible. Y doesn't affect the
        // top-down XZ→UV mapping. Note this is the projector's grid, NOT the
        // player's PixelSnap grid — that snaps to the main isometric screen
        // pixels (a different texel size and orientation), so it wouldn't align
        // with this RT.
        float metersPerTexel = camera.Size / textureSize.Y;
        focal.X = Mathf.Round(focal.X / metersPerTexel) * metersPerTexel;
        focal.Z = Mathf.Round(focal.Z / metersPerTexel) * metersPerTexel;

        // Straight down over the player. Up hint = Forward so LookAt's basis is
        // stable when the direction is vertical (its cross product with world-Up
        // would otherwise collapse).
        camera.GlobalPosition = focal + Vector3.Up * CAMERA_HEIGHT;
        camera.LookAt(focal, Vector3.Forward);

        // origin is the camera's world position (Godot's view matrix computes
        // (P_world - cam_world) projected onto the basis); using it here gives
        // the lit shaders bit-identical math. up is NEGATED before push because
        // SubViewport render-to-texture stores rows in opposite Y order from how
        // texture(uv) samples them (same flip BlockLightShadowProjector needs).
        Basis basis = camera.GlobalBasis;
        RenderingServer.GlobalShaderParameterSet("ground_stain_origin", camera.GlobalPosition);
        RenderingServer.GlobalShaderParameterSet("ground_stain_right", basis.X);
        RenderingServer.GlobalShaderParameterSet("ground_stain_up", -basis.Y);
        RenderingServer.GlobalShaderParameterSet("ground_stain_size", camera.Size);
        RenderingServer.GlobalShaderParameterSet("ground_stain_strength", strength);
    }

    private void ApplyEnabled(bool enabled)
    {
        viewport.RenderTargetUpdateMode = enabled
            ? SubViewport.UpdateMode.Always
            : SubViewport.UpdateMode.Disabled;
        RenderingServer.GlobalShaderParameterSet("ground_stain_enabled", enabled);
    }
}
