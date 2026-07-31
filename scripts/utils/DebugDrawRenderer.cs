using System.Collections.Generic;
using Godot;

// Single batched renderer for transient debug line geometry. Owns one
// ImmediateMesh — cleared and refilled every frame — drawn through two
// MeshInstance3Ds so occluded geometry reads as occluded (see the two
// materials below). Callers don't talk to this class directly — they go
// through the static DebugDraw API which forwards into _instance.
//
// Lifetime model: each queued segment has a `remaining` time in seconds.
// Segments with remaining=0 (the default) live for exactly one frame —
// added pre-_Process, drawn this frame, decremented to negative, removed.
// Segments with remaining>0 persist across frames until their counter
// expires. The list is walked in place; expired entries are swap-removed
// from the back so order doesn't matter and we avoid shifting.
//
// All debug shape geometry in the project goes through this single
// renderer — wireframe primitives for live overlays (paths, encircle
// rings) and timed shapes for one-shot events (weapon impacts, hit
// rings). One MeshInstance for the whole game, refilled each frame.
[GlobalClass]
public partial class DebugDrawRenderer : Node3D
{
    private struct Segment
    {
        public Vector3 a;
        public Vector3 b;
        public Color color;
        // Seconds remaining. <=0 means "this frame only" — drawn once
        // and removed before the next frame's tick. Positive values
        // decrement by frame delta.
        public float remaining;
    }

    // Above every other transparent material in the project (the fullscreen fog
    // quad, at 64, is the highest), so debug geometry draws last. The occluded
    // pass takes the lower of the two so it is laid down first and the visible
    // pass blends over it — transparent surfaces sort by priority ascending.
    private const int OVERLAY_RENDER_PRIORITY = 100;
    private const int VISIBLE_RENDER_PRIORITY = OVERLAY_RENDER_PRIORITY + 1;

    // Alpha multiplier for the parts of a shape that are behind geometry. They
    // still draw — that's the point of a debug overlay — but faintly enough to
    // read as behind rather than in front, which is otherwise impossible to
    // tell for a box that could be either inside a wall or hovering before it.
    private const float OCCLUDED_ALPHA = 0.22f;

    private static DebugDrawRenderer _instance;

    // Exposed so debug tooling can confirm its draw calls actually reached the
    // renderer, separating "nothing was queued" from "queued but not rendering".
    public static DebugDrawRenderer Instance => _instance != null && IsInstanceValid(_instance) ? _instance : null;

    private readonly List<Segment> _segments = new();
    // One mesh, drawn twice: once depth-tested at full strength, once faint
    // with the depth test off. Every segment therefore appears exactly once —
    // bright where nothing is in front of it, dim where something is.
    private MeshInstance3D _meshInstance;
    private MeshInstance3D _occludedInstance;
    private ImmediateMesh _mesh;
    private StandardMaterial3D _material;
    private StandardMaterial3D _occludedMaterial;

    // Lazy-creates the singleton renderer as a child of Sim.Current the
    // first time anything tries to draw. If Sim.Current isn't ready
    // (e.g. main menu / pre-game), the call is a silent no-op so calling
    // DebugDraw outside a running game doesn't throw.
    public static DebugDrawRenderer EnsureInstance()
    {
        if (_instance != null && IsInstanceValid(_instance))
        {
            return _instance;
        }
        Sim w = Sim.Current;
        if (w == null)
        {
            return null;
        }
        _instance = new DebugDrawRenderer();
        _instance.Name = "DebugDrawRenderer";
        w.AddChild(_instance);
        return _instance;
    }

    public override void _Ready()
    {
        // Material/mesh constructed at runtime is normally discouraged
        // (see CLAUDE.md), but this is a debug-only helper that's gated
        // off in shipping — keeping the .tres for it would just add noise.
        _material = CreateMaterial(depthTested: true, alpha: 1f, VISIBLE_RENDER_PRIORITY);
        _occludedMaterial = CreateMaterial(depthTested: false, OCCLUDED_ALPHA, OVERLAY_RENDER_PRIORITY);

        _mesh = new ImmediateMesh();
        // Occluded pass first: the visible pass blends over it, so a line that
        // is in the clear ends up at full strength rather than washed out.
        _occludedInstance = AddPass(_occludedMaterial);
        _meshInstance = AddPass(_material);
    }

    // Alpha rides on albedo_color, which StandardMaterial3D multiplies into the
    // vertex color — so one shared mesh serves both passes and callers keep
    // passing a single opaque color.
    //
    // Both passes must survive the fullscreen transparent quads that composite
    // on top of the scene, hence the render priority: without it the fog quad
    // (render_priority 64) blends over them at ALPHA≈1 wherever the ray reaches
    // the far plane, which is why they vanished against the sky. Without
    // DisableFog the inner environment's black depth fog (88..105m) fades them
    // to black past the camera's focus distance.
    private static StandardMaterial3D CreateMaterial(bool depthTested, float alpha, int renderPriority)
    {
        var material = new StandardMaterial3D();
        material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        material.VertexColorUseAsAlbedo = true;
        material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        material.AlbedoColor = new Color(1f, 1f, 1f, alpha);
        material.NoDepthTest = !depthTested;
        material.DisableFog = true;
        material.RenderPriority = renderPriority;
        // Disable the back-face culling: line primitives don't have
        // sides, but with culling on the rasterizer is still fussy
        // about wide lines on certain GPUs.
        material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        return material;
    }

    private MeshInstance3D AddPass(Material material)
    {
        var instance = new MeshInstance3D();
        instance.Mesh = _mesh;
        instance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        instance.MaterialOverride = material;
        AddChild(instance);
        return instance;
    }

    public override void _ExitTree()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _Process(double delta)
    {
        // Tick lifetimes BEFORE rendering so a fresh single-frame segment
        // (added with remaining=0 between frames) renders this frame and
        // is then cleaned up at the top of next frame. The cull check is
        // strict < 0 so a just-added remaining=0 segment survives one
        // render, gets decremented to negative here, then is removed on
        // the next pass.
        float dt = (float)delta;
        for (int i = _segments.Count - 1; i >= 0; i--)
        {
            Segment s = _segments[i];
            if (s.remaining < 0f)
            {
                _segments[i] = _segments[_segments.Count - 1];
                _segments.RemoveAt(_segments.Count - 1);
                continue;
            }
            s.remaining -= dt;
            _segments[i] = s;
        }

        Render();
    }

    private void Render()
    {
        _mesh.ClearSurfaces();
        if (_segments.Count == 0)
        {
            return;
        }

        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (int i = 0; i < _segments.Count; i++)
        {
            Segment s = _segments[i];
            _mesh.SurfaceSetColor(s.color);
            _mesh.SurfaceAddVertex(s.a);
            _mesh.SurfaceSetColor(s.color);
            _mesh.SurfaceAddVertex(s.b);
        }
        _mesh.SurfaceEnd();
    }

    // Internal API used by DebugDraw. Exposed so the static class doesn't
    // need to reach into private state directly.
    internal void Enqueue(Vector3 a, Vector3 b, Color color, float lifetime)
    {
        _segments.Add(new Segment
        {
            a = a,
            b = b,
            color = color,
            remaining = lifetime,
        });
    }

    public int SegmentCount => _segments.Count;
}
