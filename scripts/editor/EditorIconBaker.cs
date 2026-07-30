using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

// One brush that wants a rendered icon: the palette slot to deliver it to, and
// the scene to render for it.
public readonly struct IconBakeRequest
{
    public readonly int PaletteIndex;
    public readonly PackedScene Scene;

    public IconBakeRequest(int paletteIndex, PackedScene scene)
    {
        PaletteIndex = paletteIndex;
        Scene = scene;
    }
}

// Renders palette-button icons for brushes that have no authored art, by
// instancing each brush's real scene into an off-screen viewport and capturing
// a single frame. Beats hand-drawing ~60 thumbnails: a newly authored prop gets
// a button image with no extra authoring step, and the image can't go stale.
//
// The bake viewport owns its own World3D, so the editor's terrain, cutaway and
// projectors aren't in the shot — but prop shaders are `ambient_light_disabled`
// and take their light from GLOBAL uniforms (sun_color / sun_world_dir /
// light_map), so the instance still has to sit at a world position the light
// map covers or it bakes black. That's what the anchor is for: the caller
// passes a lit spot in the edited world (the cursor), and anchorOffset lifts
// the instance clear of the ground into open air.
[GlobalClass]
public partial class EditorIconBaker : Node
{
    [Export] public SubViewport viewport;
    [Export] public Camera3D camera;
    // Bake instances are parented here, one at a time.
    [Export] public Node3D mount;

    [ExportGroup("Framing")]
    // Iso-ish view direction, matching how props read in the game camera.
    [Export] public Vector3 viewDirection = new Vector3(-1f, -0.8f, -1f);
    // How far back the ortho camera sits. Only has to clear the tallest prop —
    // an orthographic projection doesn't change scale with distance.
    [Export(PropertyHint.Range, "1,500,1")] public float cameraDistance = 100f;
    // Slack around the subject's bounds, so nothing touches the button edge.
    [Export(PropertyHint.Range, "1,2,0.01")] public float framePadding = 1.15f;
    // Floor on the ortho frame, so a tiny or mesh-less prop doesn't bake at an
    // absurd zoom (or divide the frame down to nothing).
    [Export(PropertyHint.Range, "0.05,10,0.05")] public float minimumFrameSize = 0.5f;

    [ExportGroup("Lighting")]
    // Lift off the caller's anchor. The anchor is the editor cursor, which can
    // sit at ground level or inside geometry; a few metres up is reliably open
    // air, and open air is where the light map reads as full daylight.
    [Export] public Vector3 anchorOffset = new Vector3(0f, 3f, 0f);

    // Set when the editor tears down mid-bake — the loop awaits frames, so it
    // can outlive the scene it's rendering into.
    private bool _cancelled;

    public override void _ExitTree()
    {
        _cancelled = true;
    }

    // Bakes each request in turn, one per frame, handing each finished icon to
    // onBaked as it completes so buttons fill in progressively rather than the
    // palette stalling until every prop has rendered. `worldAnchor` is a lit
    // position in the edited world — see the class comment.
    public async void Bake(IReadOnlyList<IconBakeRequest> requests, Vector3 worldAnchor, Action<int, Texture2D> onBaked)
    {
        if (viewport == null || camera == null || mount == null || requests == null || onBaked == null)
        {
            GD.PushWarning("EditorIconBaker: rig is not wired; entity brushes will show name labels instead of icons.");
            return;
        }

        mount.GlobalPosition = worldAnchor + anchorOffset;

        foreach (IconBakeRequest request in requests)
        {
            if (_cancelled || !IsInstanceValid(this))
            {
                return;
            }
            Texture2D icon = await BakeOne(request.Scene);
            if (_cancelled || !IsInstanceValid(this))
            {
                return;
            }
            if (icon != null)
            {
                onBaked(request.PaletteIndex, icon);
            }
        }
    }

    private async Task<Texture2D> BakeOne(PackedScene scene)
    {
        if (scene?.Instantiate() is not Node3D node)
        {
            return null;
        }
        mount.AddChild(node);
        node.Position = Vector3.Zero;

        // Procedural props build their geometry in _Ready (TreeTrunk meshes its
        // trunk, FoliageMultiMesh fills its multimesh), so bounds aren't final
        // — and the multimesh isn't even populated — until a frame has passed.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_cancelled || !IsInstanceValid(node))
        {
            return null;
        }

        FrameSubject(node);
        viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        // The render target only holds this subject's image once the frame it
        // was scheduled in has actually drawn.
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        Texture2D icon = null;
        if (!_cancelled && IsInstanceValid(viewport))
        {
            Image image = viewport.GetTexture()?.GetImage();
            if (image != null)
            {
                icon = ImageTexture.CreateFromImage(image);
            }
        }
        if (IsInstanceValid(node))
        {
            node.QueueFree();
        }
        return icon;
    }

    // Points the ortho camera at the subject and zooms it to fit. The frame is
    // measured by projecting the subject's corners onto the camera's own axes
    // rather than using a bounding sphere, so a sequoia and a tuft of grass
    // both fill their button instead of both being framed for the sequoia.
    private void FrameSubject(Node3D node)
    {
        Aabb bounds = VisualBounds.Of(node) ?? new Aabb(node.GlobalPosition, Vector3.Zero);
        Vector3 center = bounds.GetCenter();
        Vector3 direction = viewDirection.Normalized();

        camera.LookAtFromPosition(center - direction * cameraDistance, center, Vector3.Up);

        Basis basis = camera.GlobalBasis;
        float halfWidth = 0f;
        float halfHeight = 0f;
        Vector3 size = bounds.Size;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 offset = bounds.Position + new Vector3(
                (corner & 1) != 0 ? size.X : 0f,
                (corner & 2) != 0 ? size.Y : 0f,
                (corner & 4) != 0 ? size.Z : 0f) - center;
            halfWidth = Mathf.Max(halfWidth, Mathf.Abs(offset.Dot(basis.X)));
            halfHeight = Mathf.Max(halfHeight, Mathf.Abs(offset.Dot(basis.Y)));
        }

        // The viewport is square, so one Size covers both axes — take the wider.
        camera.Size = Mathf.Max(Mathf.Max(halfWidth, halfHeight) * 2f * framePadding, minimumFrameSize);
        camera.Far = cameraDistance * 2f;
    }
}
