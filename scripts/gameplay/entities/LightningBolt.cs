using System.Collections.Generic;
using Godot;

// A procedurally-generated jagged lightning bolt drawn as a soft, camera-facing
// ribbon between two points, with random offshoot branches. Built to be reused:
// a weather LightningStrike generates one vertical bolt (ground → sky), but the
// same component arcs between two arbitrary world points for chain-lightning
// weapons — just place the node at the source and Generate(Zero, ToLocal(target)).
//
// Geometry vs. billboarding: rather than a billboard_mode quad (which can only
// pivot a single flat card and would flatten a multi-segment shape), the path is
// jittered once into a fixed jagged polyline, then the RIBBON is rebuilt each
// frame so every segment's flat side turns to face the camera. The path shape is
// stable; only its orientation tracks the view ("camera-yaw facing quads" for an
// isometric camera, but it works at any bolt orientation). Rebuild is gated on
// camera movement and bolts are short-lived, so the per-frame cost is trivial.
//
// Each cross-section is three verts (transparent edge → bright center → transparent
// edge) so the additive material reads as a glowing core with soft falloff, no
// gradient texture required. Branches taper their width and alpha to zero at the tip.
[GlobalClass]
public partial class LightningBolt : MeshInstance3D
{
    // Material for the bolt ribbon (unshaded, vertex-color, alpha-blended). Baked
    // into the generated mesh's surface each build — NOT a surface_material_override,
    // because that only binds when the mesh already has a surface at load time, and
    // this mesh is built at runtime. Wired in the scene; a chain-lightning caller
    // can swap it for a differently-tinted arc.
    [Export] public Material material;

    // --- Core bolt shape ---

    // Width (m) of the bolt's bright core at its root. The visible glow is this
    // wide; the soft additive falloff makes the lit line read thinner.
    [Export(PropertyHint.Range, "0.05,3,0.05")] public float coreWidth = 0.45f;

    // Peak perpendicular jitter (m) of each zigzag point off the straight line.
    // Pinned to zero at both endpoints; full amplitude across the middle.
    [Export(PropertyHint.Range, "0,5,0.05")] public float jagAmplitude = 1.1f;

    // Target spacing (m) between zigzag points along the bolt. Smaller = more,
    // tighter kinks. Drives the main bolt's segment count from its length.
    [Export(PropertyHint.Range, "0.25,10,0.25")] public float segmentLength = 2.0f;

    // Alpha of the bolt at its root vs. its far tip. The strike bolt fades its
    // sky end out (endAlpha < 1); a chain arc between two solid targets would set
    // both to 1.
    [Export(PropertyHint.Range, "0,1,0.01")] public float mainStartAlpha = 1.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float mainEndAlpha = 0.18f;

    // Core color, written into vertex color (the material uses it as albedo). The
    // additive emission tints from the material; this lets a caller recolor per
    // bolt (e.g. a differently-tinted chain arc) without a new material.
    [Export] public Color boltColor = new Color(0.75f, 0.85f, 1f, 1f);

    // --- Branches ---

    // Number of offshoot branches spawned off the main bolt.
    [Export(PropertyHint.Range, "0,16,1")] public int branchCount = 5;

    // How many generations of branches (1 = only off the main bolt; 2 = branches
    // also sprout sub-branches). Each generation spawns roughly half as many.
    [Export(PropertyHint.Range, "1,4,1")] public int branchDepth = 1;

    // Branch length as a fraction of the main bolt length, sampled per branch.
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float branchLengthFractionMin = 0.15f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float branchLengthFractionMax = 0.4f;

    // Max angular deviation (degrees) of a branch from its parent's direction.
    [Export(PropertyHint.Range, "0,90,1")] public float branchAngleDegrees = 42f;

    // Branch core width as a fraction of the parent's, and its jitter as a
    // fraction of the main jagAmplitude. Branches always taper to nothing.
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float branchWidthScale = 0.5f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float branchJagScale = 0.7f;

    // Alpha at a branch root (tapers to 0 at the tip).
    [Export(PropertyHint.Range, "0,1,0.01")] public float branchRootAlpha = 0.85f;

    // --- Self-managed flash lifetime (standalone arcs, e.g. chain lightning) ---
    // Used only when the bolt is spawned on its own via Flash()/CreateArc rather
    // than driven by an owner like LightningStrike. The bolt holds at full opacity
    // for holdSeconds, fades out over fadeSeconds, then frees itself. Cosmetic, so
    // this rides wall-clock _Process delta (slow-mo shouldn't drag the flicker).
    [Export(PropertyHint.Range, "0,1,0.01")] public float holdSeconds = 0.06f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float fadeSeconds = 0.22f;

    // Camera must move at least this far (m) before the ribbon is rebuilt to
    // re-face it. Keeps a near-static isometric camera from rebuilding every frame.
    private const float CAM_REBUILD_DISTANCE = 0.25f;

    // One stroke = one polyline (the main bolt, or a branch) plus its width/alpha
    // taper. Path points are local-space and fixed at Generate time; the ribbon
    // is rebuilt from them each frame toward the camera.
    private struct Stroke
    {
        public List<Vector3> points;
        public float startWidth;
        public float endWidth;
        public float startAlpha;
        public float endAlpha;
    }

    private readonly List<Stroke> _strokes = new();
    private readonly RandomNumberGenerator _rng = new();
    private Vector3 _lastCamWorld;
    private bool _built;
    private bool _selfManaged;
    private float _flashAge;

    // Spawn a one-shot arc bolt between two WORLD points under `host`, from a scene
    // whose root is a LightningBolt. Generates, holds, fades, and frees itself.
    // The seam for chain-lightning weapons: an arc per link. Returns null on bad input.
    public static LightningBolt CreateArc(Node host, PackedScene scene, Vector3 fromWorld, Vector3 toWorld)
    {
        if (host == null || scene == null)
        {
            return null;
        }
        LightningBolt bolt = scene.InstantiateOrNull<LightningBolt>();
        if (bolt == null)
        {
            return null;
        }
        host.AddChild(bolt);
        bolt.GlobalPosition = fromWorld;
        // ToLocal accounts for the bolt's inherited global transform, so the end
        // point is correct even if the host is rotated/scaled.
        bolt.Flash(Vector3.Zero, bolt.ToLocal(toWorld));
        return bolt;
    }

    // Generate the bolt between two LOCAL points, then hold/fade/free on the wall
    // clock (see holdSeconds/fadeSeconds). For spawn-and-forget standalone bolts.
    public void Flash(Vector3 localStart, Vector3 localEnd)
    {
        Generate(localStart, localEnd);
        Visible = true;
        Transparency = 0f;
        _selfManaged = true;
        _flashAge = 0f;
    }

    // Build a fresh random bolt between two LOCAL-space points and draw it once.
    // Call again to re-roll the shape (e.g. a flickering re-strike).
    public void Generate(Vector3 localStart, Vector3 localEnd)
    {
        _strokes.Clear();
        _rng.Randomize();

        Vector3 delta = localEnd - localStart;
        float len = delta.Length();
        Vector3 dir = len > 1e-4f ? delta / len : Vector3.Up;

        List<Vector3> main = GenerateStrokePoints(localStart, localEnd, jagAmplitude, segmentLength);
        _strokes.Add(new Stroke
        {
            points = main,
            startWidth = coreWidth,
            endWidth = coreWidth * 0.35f,
            startAlpha = mainStartAlpha,
            endAlpha = mainEndAlpha,
        });

        // Branches fork toward the ground. The trunk is modelled bottom→top
        // (dir points up), but the strike reads as coming DOWN, so offshoots grow
        // along -dir (downward and outward). Sub-branches inherit their parent
        // branch's already-downward heading, so the negation is only at this level.
        GenerateBranches(main, -dir, len, branchDepth, branchCount);

        Vector3 cam = CameraWorld();
        _lastCamWorld = cam;
        BuildMesh(ToLocal(cam));
        _built = true;
    }

    public override void _Process(double delta)
    {
        if (!Visible || _strokes.Count == 0)
        {
            return;
        }
        // Re-face the camera — the shape is fixed; only its orientation tracks the
        // view. Skip the rebuild when the camera has barely moved.
        Vector3 cam = CameraWorld();
        if (!_built || cam.DistanceTo(_lastCamWorld) >= CAM_REBUILD_DISTANCE)
        {
            _lastCamWorld = cam;
            BuildMesh(ToLocal(cam));
            _built = true;
        }

        // Standalone arcs manage their own hold → fade → free.
        if (_selfManaged)
        {
            _flashAge += (float)delta;
            if (_flashAge > holdSeconds)
            {
                float t = fadeSeconds > 0f ? Mathf.Clamp((_flashAge - holdSeconds) / fadeSeconds, 0f, 1f) : 1f;
                Transparency = t;
                if (t >= 1f)
                {
                    QueueFree();
                }
            }
        }
    }

    private Vector3 CameraWorld()
    {
        // Fall back to a point "behind" the bolt when there's no camera (headless)
        // so geometry still builds with a sane facing.
        return GameCamera.Current?.GlobalPosition ?? GlobalPosition + Vector3.Back * 10f;
    }

    // --- Path generation ---

    // A jagged polyline from a to b: points are spaced ~spacing apart and pushed
    // off the straight line by up to amp along two perpendicular axes, with the
    // offset enveloped to zero at both ends so the endpoints stay exact.
    private List<Vector3> GenerateStrokePoints(Vector3 a, Vector3 b, float amp, float spacing)
    {
        Vector3 delta = b - a;
        float len = delta.Length();
        Vector3 dir = len > 1e-4f ? delta / len : Vector3.Up;
        BuildPerp(dir, out Vector3 p1, out Vector3 p2);

        int segs = Mathf.Max(1, Mathf.RoundToInt(len / Mathf.Max(0.01f, spacing)));
        var pts = new List<Vector3>(segs + 1);
        for (int i = 0; i <= segs; i++)
        {
            float t = (float)i / segs;
            Vector3 basePt = a.Lerp(b, t);
            // Ramp amplitude in from each end over the first/last ~12% of length,
            // full across the middle — pins endpoints without killing mid jitter.
            float env = Mathf.Clamp(Mathf.Min(t, 1f - t) * 4f, 0f, 1f);
            float off1 = (_rng.Randf() * 2f - 1f) * amp * env;
            float off2 = (_rng.Randf() * 2f - 1f) * amp * env;
            pts.Add(basePt + p1 * off1 + p2 * off2);
        }
        pts[0] = a;
        pts[segs] = b;
        return pts;
    }

    // growthDir is the general heading offshoots grow toward (downward/outward for
    // a strike), NOT the parent stroke's own direction — see the caller in Generate.
    private void GenerateBranches(List<Vector3> parent, Vector3 growthDir, float parentLen, int depth, int count)
    {
        if (depth <= 0 || count <= 0 || parent.Count < 4)
        {
            return;
        }
        for (int b = 0; b < count; b++)
        {
            // Root the branch somewhere along the parent, away from its endpoints.
            int idx = _rng.RandiRange(1, parent.Count - 2);
            Vector3 root = parent[idx];
            Vector3 dir = RandomDeviate(growthDir, branchAngleDegrees);
            float frac = Mathf.Lerp(branchLengthFractionMin, branchLengthFractionMax, _rng.Randf());
            float len = parentLen * frac;
            Vector3 tip = root + dir * len;

            List<Vector3> pts = GenerateStrokePoints(root, tip, jagAmplitude * branchJagScale, segmentLength * 0.7f);
            float width = coreWidth * Mathf.Pow(branchWidthScale, branchDepth - depth + 1);
            _strokes.Add(new Stroke
            {
                points = pts,
                startWidth = width,
                endWidth = 0f,
                startAlpha = branchRootAlpha,
                endAlpha = 0f,
            });

            // Sub-branches: thinner, fewer, off this branch.
            GenerateBranches(pts, dir, len, depth - 1, count / 2);
        }
    }

    // A unit vector deviated from dir by up to maxDeg on each of two perpendicular
    // axes — keeps branches in the parent's forward hemisphere.
    private Vector3 RandomDeviate(Vector3 dir, float maxDeg)
    {
        BuildPerp(dir, out Vector3 p1, out Vector3 p2);
        float ang = Mathf.DegToRad(maxDeg);
        float a1 = (_rng.Randf() * 2f - 1f) * ang;
        float a2 = (_rng.Randf() * 2f - 1f) * ang;
        Vector3 d = dir + p1 * Mathf.Tan(a1) + p2 * Mathf.Tan(a2);
        return d.LengthSquared() > 1e-6f ? d.Normalized() : dir;
    }

    // Two unit vectors perpendicular to dir (and to each other).
    private static void BuildPerp(Vector3 dir, out Vector3 p1, out Vector3 p2)
    {
        Vector3 up = Mathf.Abs(dir.Y) < 0.99f ? Vector3.Up : Vector3.Right;
        p1 = dir.Cross(up).Normalized();
        p2 = dir.Cross(p1).Normalized();
    }

    // --- Ribbon construction ---

    private void BuildMesh(Vector3 camLocal)
    {
        if (_strokes.Count == 0)
        {
            Mesh = null;
            return;
        }
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        if (material != null)
        {
            st.SetMaterial(material);
        }
        for (int i = 0; i < _strokes.Count; i++)
        {
            AddStroke(st, _strokes[i], camLocal);
        }
        Mesh = st.Commit();
    }

    private void AddStroke(SurfaceTool st, Stroke s, Vector3 camLocal)
    {
        List<Vector3> pts = s.points;
        int n = pts.Count;
        if (n < 2)
        {
            return;
        }
        // Per-point ribbon frame: left edge, center, right edge, and core alpha.
        // The width axis is perpendicular to both the local tangent and the
        // direction to the camera, so the flat ribbon turns to face the viewer.
        var left = new Vector3[n];
        var right = new Vector3[n];
        var alpha = new float[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 tangent = Tangent(pts, i, n);
            Vector3 toCam = camLocal - pts[i];
            toCam = toCam.LengthSquared() > 1e-6f ? toCam.Normalized() : Vector3.Back;
            Vector3 side = tangent.Cross(toCam);
            if (side.LengthSquared() < 1e-6f)
            {
                // Camera looking straight down the bolt — any perpendicular works.
                side = tangent.Cross(Vector3.Up);
                if (side.LengthSquared() < 1e-6f)
                {
                    side = Vector3.Right;
                }
            }
            side = side.Normalized();

            float f = (float)i / (n - 1);
            float halfWidth = Mathf.Lerp(s.startWidth, s.endWidth, f) * 0.5f;
            left[i] = pts[i] - side * halfWidth;
            right[i] = pts[i] + side * halfWidth;
            alpha[i] = Mathf.Lerp(s.startAlpha, s.endAlpha, f);
        }

        for (int i = 0; i < n - 1; i++)
        {
            float v0 = (float)i / (n - 1);
            float v1 = (float)(i + 1) / (n - 1);
            Color edge0 = ColorAt(0f);
            Color edge1 = ColorAt(0f);
            Color mid0 = ColorAt(alpha[i]);
            Color mid1 = ColorAt(alpha[i + 1]);
            // Left half: transparent edge → bright center.
            Quad(st,
                left[i], edge0, new Vector2(0f, v0),
                pts[i], mid0, new Vector2(0.5f, v0),
                pts[i + 1], mid1, new Vector2(0.5f, v1),
                left[i + 1], edge1, new Vector2(0f, v1));
            // Right half: bright center → transparent edge.
            Quad(st,
                pts[i], mid0, new Vector2(0.5f, v0),
                right[i], edge0, new Vector2(1f, v0),
                right[i + 1], edge1, new Vector2(1f, v1),
                pts[i + 1], mid1, new Vector2(0.5f, v1));
        }
    }

    private Color ColorAt(float a)
    {
        return new Color(boltColor.R, boltColor.G, boltColor.B, a);
    }

    private static Vector3 Tangent(List<Vector3> pts, int i, int n)
    {
        Vector3 t;
        if (i == 0)
        {
            t = pts[1] - pts[0];
        }
        else if (i == n - 1)
        {
            t = pts[n - 1] - pts[n - 2];
        }
        else
        {
            t = pts[i + 1] - pts[i - 1];
        }
        return t.LengthSquared() > 1e-6f ? t.Normalized() : Vector3.Up;
    }

    // Emits a quad (p0→p1→p2→p3) as two triangles. The material is double-sided,
    // so winding doesn't matter.
    private static void Quad(SurfaceTool st,
        Vector3 p0, Color c0, Vector2 uv0,
        Vector3 p1, Color c1, Vector2 uv1,
        Vector3 p2, Color c2, Vector2 uv2,
        Vector3 p3, Color c3, Vector2 uv3)
    {
        Vert(st, p0, c0, uv0);
        Vert(st, p1, c1, uv1);
        Vert(st, p2, c2, uv2);
        Vert(st, p0, c0, uv0);
        Vert(st, p2, c2, uv2);
        Vert(st, p3, c3, uv3);
    }

    private static void Vert(SurfaceTool st, Vector3 pos, Color col, Vector2 uv)
    {
        st.SetColor(col);
        st.SetUV(uv);
        st.AddVertex(pos);
    }
}
