using System.Collections.Generic;
using Godot;

// Single batched renderer for transient debug line geometry. Owns one
// MeshInstance3D + ImmediateMesh + an unshaded vertex-color material that
// gets cleared and refilled every frame. Callers don't talk to this class
// directly — they go through the static DebugDraw API which forwards into
// _instance.
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

    private static DebugDrawRenderer _instance;

    private readonly List<Segment> _segments = new();
    private MeshInstance3D _meshInstance;
    private ImmediateMesh _mesh;
    private StandardMaterial3D _material;

    // Lazy-creates the singleton renderer as a child of World.Current the
    // first time anything tries to draw. If World.Current isn't ready
    // (e.g. main menu / pre-game), the call is a silent no-op so calling
    // DebugDraw outside a running game doesn't throw.
    public static DebugDrawRenderer EnsureInstance()
    {
        if (_instance != null && IsInstanceValid(_instance))
        {
            return _instance;
        }
        World w = World.Current;
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
        _material = new StandardMaterial3D();
        _material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _material.VertexColorUseAsAlbedo = true;
        _material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _material.NoDepthTest = false;
        // Disable the back-face culling: line primitives don't have
        // sides, but with culling on the rasterizer is still fussy
        // about wide lines on certain GPUs.
        _material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;

        _mesh = new ImmediateMesh();
        _meshInstance = new MeshInstance3D();
        _meshInstance.Mesh = _mesh;
        _meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _meshInstance.MaterialOverride = _material;
        AddChild(_meshInstance);
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
        // is then cleaned up at the top of next frame. Segments added
        // during _Process by other nodes will get their first render
        // here; that's fine — they'll have remaining=0 and be cleaned
        // out next frame.
        float dt = (float)delta;
        for (int i = _segments.Count - 1; i >= 0; i--)
        {
            Segment s = _segments[i];
            if (s.remaining <= 0f)
            {
                // Drawn last frame already (or freshly added with no
                // lifetime). Remove via swap-pop.
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
