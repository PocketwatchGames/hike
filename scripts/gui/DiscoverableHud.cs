using Godot;

// Worldspace HUD for any Discoverable that opted into a callout (trap,
// secret passage). Visible only while perception is in Detected — once the
// host hits Discovered the host's own visuals take over and the HUD hides
// itself. Mirrors MobHUD's screen-projection shape so behind-camera /
// off-screen handling is identical.
public partial class DiscoverableHud : Node2D
{
    [Export] private TextureProgressBar _perceptionBar;

    private Camera3D _camera;
    private Discoverable _discoverable;

    public static void Create(PackedScene scene, Camera3D camera, Discoverable discoverable, Node parent)
    {
        var hud = scene.Instantiate<DiscoverableHud>();
        hud.Init(camera, discoverable, parent);
    }

    private void Init(Camera3D camera, Discoverable discoverable, Node parent)
    {
        _camera = camera;
        _discoverable = discoverable;
        if (parent != null)
        {
            parent.AddChild(this);
        }
        Scale = new Vector2(discoverable.hudScale, discoverable.hudScale);
        _discoverable.TreeExiting += QueueFree;
        Update();
    }

    private void Update()
    {
        if (_discoverable == null || _discoverable.State != EPlayerPerceptionState.Detected)
        {
            Visible = false;
            return;
        }
        Vector3 worldPos = _discoverable.HudPosition;
        if (_camera.IsPositionBehind(worldPos))
        {
            Visible = false;
            return;
        }

        Visible = true;
        Position = GameClient.Current.ProjectToScreen(worldPos);
        if (_perceptionBar != null)
        {
            _perceptionBar.Value = _discoverable.PerceptionProgress;
        }
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("DiscoverableHud.Process");

        Update();
    }
}
