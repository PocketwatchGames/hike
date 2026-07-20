using Godot;

// Top-down silhouette projector. Renders the alpha-cut shadow proxies of
// every sprite into a small SubViewport via an orthographic camera, then
// publishes the result as `block_light_shadow_tex` for the lit shaders.
// Each sprite contributes a SECOND proxy node (BlockLightShadowProxy on
// LitSprite, the BlockLightShadow multimesh bucket on prop scatter) on
// the projector layer; the original sun/moon ShadowsOnly proxy is
// untouched on the default layer. Lit shaders sample the projector
// texture and dim their `block_lit` term where coverage > 0.
//
// World→UV is decomposed into origin (vec3) + right (vec3) + up (vec3) +
// size (float) globals — see _Process. mat4 globals don't round-trip
// through project.godot's [shader_globals] section reliably in Godot
// 4.6.
//
// CVar `block_light_shadow` is the on/off toggle. When off the SubViewport
// stops rendering and the lit shaders branch around the sample (see
// `block_light_shadow_enabled` global) so rendering is byte-identical to
// pre-feature.
//
// Scene wiring: this node is parented inside SceneViewport (sibling of
// MainCamera). The exported `viewport` is a SubViewport child of this
// node with own_world_3d=false, so it inherits SceneViewport's World3D
// and renders the same chunks/sprites the main camera sees. The exported
// `camera` is a Camera3D child of that SubViewport, current=true within
// it.
[GlobalClass]
public partial class BlockLightShadowProjector : Node3D
{
    // Orthographic frustum half-extent (world units). Should at least cover
    // the area the player can see — anything outside the projector's
    // frustum reads coverage = 0 and gets full block_lit. ~40 voxels is a
    // comfortable pad around the iso camera's visible footprint.
    [Export(PropertyHint.Range, "8,128,1")] public float radiusWorld = 40f;

    // Tilt of the projector direction away from straight-down. 0 = pure
    // top-down (silhouettes degenerate to edge-on for billboarded
    // sprites). Higher = longer drop-shadow on the ground; lower = shorter.
    // 20–35° reads well; below ~15° the billboard math gets pinched and
    // silhouettes look anemic.
    [Export(PropertyHint.Range, "5,60,0.5")] public float tiltDegrees = 25f;

    // Coverage texture size. 512 is a sensible default — ~16 cm/texel at
    // radiusWorld=40, which keeps silhouette edges sharp at the chunky
    // pixel-art scale without burning meaningful VRAM (~1 MB at RGBA8).
    [Export] public Vector2I textureSize = new Vector2I(512, 512);

    // How aggressively the silhouette dims block_lit. 1.0 = full dim
    // (fragment under a silhouette gets block_lit=0). 0.0 = no effect.
    // 0.4–0.7 reads as a subtle grounding shadow without crushing
    // torch/lamp light to nothing.
    [Export(PropertyHint.Range, "0,1,0.01")] public float strength = 0.6f;

    // Soft-edge blur radius, measured in projector texels. 0 = sharp
    // alpha-cut silhouettes (raw 1-texel edges from the proxy render).
    // 1.5–3.0 gives a soft drop-shadow look without obvious pixel grid
    // artifacts. Cost: a 5-tap cross blur in the lit shaders, applied
    // only when this is > 0.
    [Export(PropertyHint.Range, "0,8,0.1")] public float blurTexels = 2.0f;

    // Distance the projector camera sits from the focal plane along
    // -direction. Far enough that ALL casters between the camera and the
    // focal plane are inside the frustum — sprite proxies extend several
    // voxels upward from their anchors, and tall props (trees) more so.
    // Camera's far plane = CAMERA_HEIGHT * 2.
    private const float CAMERA_HEIGHT = 100f;

    // Visual layer the shadow proxies live on. Picked so it's distinct
    // from the default layers used by terrain / sprites (layer 1 = bit 0).
    // The projector camera's CullMask matches this; the main camera's
    // CullMask excludes it. SunLight and MoonLight include it in their
    // cull mask so proxies still cast sun/moon shadows.
    public const uint SHADOW_PROXY_LAYER_MASK = 1u << 3; // layer 4

    [Export] public SubViewport viewport;
    [Export] public Camera3D camera;

    // Cached projector forward direction. Constant after _Ready (we no
    // longer track the iso camera) — used in _Process to position the
    // camera. The basis (right/up) is read fresh from camera.GlobalBasis
    // each frame so the basis pushed to the shader is bit-identical to
    // the one Godot's LookAt computed for the projection matrix.
    private Vector3 _projectorDir;

    public override void _Ready()
    {
        if (Engine.IsEditorHint())
        {
            return;
        }

        viewport.Size = textureSize;
        // own_world_3d=false so this nested SubViewport renders the parent
        // SceneViewport's World3D — same chunks, players, sprites the main
        // camera sees. Without this we'd need to spawn the world twice.
        viewport.OwnWorld3D = false;
        // Transparent so the viewport clears to (0,0,0,0) regardless of
        // the parent world's environment background. Lit shaders read .r
        // as coverage; the default needs to be 0 outside any silhouette.
        viewport.TransparentBg = true;

        camera.Projection = Camera3D.ProjectionType.Orthogonal;
        camera.Size = radiusWorld * 2f;
        camera.Near = 0.1f;
        camera.Far = CAMERA_HEIGHT * 2f;
        camera.CullMask = SHADOW_PROXY_LAYER_MASK;
        camera.Current = true;

        // Only the texture global needs runtime seeding: project.godot
        // points it at a 1×1 placeholder so shader compile works, and we
        // swap in the live SubViewport texture here. The other globals
        // (origin/right/up/size/strength/blur_uv/enabled) take their
        // project.godot defaults until _Process and the CVar callback
        // push real values. RegisterRuntime would race standalone-launch
        // shader compile and produce 'Global uniform does not exist'
        // errors — see CLAUDE.md → Shader Global Uniforms.
        ShaderGlobals.Register("block_light_shadow_tex", RenderingServer.GlobalShaderParameterType.Sampler2D, viewport.GetTexture());

        _projectorDir = ComputeDirection();

        // Stop rendering the projector pass when the CVar toggles off.
        // The CVar's own callback already pushes block_light_shadow_enabled
        // for the lit shaders' uniform branch — this listener piggybacks
        // so the viewport's render-target update mode also tracks state.
        CVars.blockLightShadow.OnChanged += OnBlockLightShadowChanged;
        ApplyEnabled(CVars.blockLightShadow.Value);
    }

    public override void _ExitTree()
    {
        CVars.blockLightShadow.OnChanged -= OnBlockLightShadowChanged;
    }

    private void OnBlockLightShadowChanged(CVar cvar)
    {
        ApplyEnabled(((CVarBool)cvar).Value);
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("BlockLightShadowProjector.Process");
        if (Engine.IsEditorHint() || !CVars.blockLightShadow.Value)
        {
            return;
        }

        Vector3 playerPos = Sim.Current?.player?.GlobalPosition ?? Vector3.Zero;
        Vector3 focal = playerPos;

        camera.GlobalPosition = focal - _projectorDir * CAMERA_HEIGHT;
        camera.LookAt(focal, ComputeUp(_projectorDir));

        // origin is the camera's world position (Godot's view matrix
        // computes (P_world - cam_world) projected onto the basis); using
        // it here gives the lit shaders bit-identical math.
        //
        // The up axis is NEGATED before push: Godot's render-to-texture
        // for SubViewports stores rows in opposite Y order from how
        // texture(uv) samples them, so without the flip world-fixed
        // silhouettes drift on the up axis as the player moves while the
        // player's own (texture-centered) silhouette stays anchored.
        Basis basis = camera.GlobalBasis;
        RenderingServer.GlobalShaderParameterSet("block_light_shadow_origin", camera.GlobalPosition);
        RenderingServer.GlobalShaderParameterSet("block_light_shadow_right", basis.X);
        RenderingServer.GlobalShaderParameterSet("block_light_shadow_up", -basis.Y);
        RenderingServer.GlobalShaderParameterSet("block_light_shadow_size", camera.Size);
        RenderingServer.GlobalShaderParameterSet("block_light_shadow_strength", strength);
        // Pre-divide blur radius by texture size so the shader can do a
        // single multiply instead of fetching textureSize per fragment.
        RenderingServer.GlobalShaderParameterSet("block_light_shadow_blur_uv", blurTexels / Mathf.Max(textureSize.X, 1));
    }

    private void ApplyEnabled(bool enabled)
    {
        viewport.RenderTargetUpdateMode = enabled
            ? SubViewport.UpdateMode.Always
            : SubViewport.UpdateMode.Disabled;
        RenderingServer.GlobalShaderParameterSet("block_light_shadow_enabled", enabled);
    }

    // Mostly-down direction with a small horizontal tilt along a FIXED
    // world axis. Tilting toward the active iso camera (the original
    // intent) sounds nice but rotates the projector basis every time
    // the camera yaws — and (world_pos - origin) projected onto a
    // rotating basis makes off-center silhouettes drift across the world
    // each frame while only the focal-point silhouette stays anchored.
    // Locking the horizontal tilt to a constant world direction keeps
    // the basis stable, so all silhouettes render at their world-space
    // ground positions regardless of camera yaw.
    private Vector3 ComputeDirection()
    {
        float tiltRad = Mathf.DegToRad(Mathf.Max(tiltDegrees, 5f));
        float sinT = Mathf.Sin(tiltRad);
        float cosT = Mathf.Cos(tiltRad);
        // Fixed +Z horizontal — chosen so the silhouette projects in the
        // -Z half (toward camera at the standard iso yaw of ~+45° around
        // Y) for a natural drop-shadow read at the canonical camera angle.
        return new Vector3(0f, -cosT, sinT).Normalized();
    }

    // Stable up vector for LookAt — degenerates to world-Up except when
    // direction is so close to vertical that LookAt's cross product would
    // collapse, in which case we fall back to world-Forward.
    private static Vector3 ComputeUp(Vector3 dir)
    {
        return Mathf.Abs(dir.Y) > 0.999f ? Vector3.Forward : Vector3.Up;
    }
}
