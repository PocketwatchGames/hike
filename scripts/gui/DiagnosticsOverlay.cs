using Godot;

// Always-on-top diagnostics screen. Currently shows FPS; intended as a
// catch-all panel for future runtime diagnostics (chunk counts, mob counts,
// memory, etc.). Toggled with F3. Lives on its own CanvasLayer above the
// HUD but below the console so the console can still cover it when open.
public partial class DiagnosticsOverlay : CanvasLayer
{
    // ConsoleUI sits at layer 100; keep this just below so the console
    // visually wins when both are open.
    private const int OverlayLayer = 99;

    // Update FPS at a fixed cadence so the on-screen number is readable.
    // The underlying Engine.GetFramesPerSecond() is already a smoothed
    // running average, but updating the label every frame produces flicker.
    private const double UpdateIntervalSeconds = 0.25;

    private Label _fpsLabel;
    private double _accum;

    public override void _Ready()
    {
        Layer = OverlayLayer;
        // Default off; press F3 to toggle. Defaulting visible would clutter
        // every screenshot, default off is friendlier.
        Visible = false;

        var panel = new PanelContainer();
        panel.AnchorLeft = 0f;
        panel.AnchorTop = 0f;
        panel.OffsetLeft = 8f;
        panel.OffsetTop = 8f;
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_top", 4);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_bottom", 4);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        panel.AddChild(margin);

        _fpsLabel = new Label();
        _fpsLabel.Text = "FPS --";
        _fpsLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        margin.AddChild(_fpsLabel);

        // Run while the rest of the game is paused so we can still read the
        // counter while inspecting a paused frame.
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Process(double delta)
    {
        if (!Visible) { return; }
        _accum += delta;
        if (_accum < UpdateIntervalSeconds) { return; }
        _accum = 0;
        _fpsLabel.Text = $"FPS {Engine.GetFramesPerSecond():F0}";
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.F3)
        {
            Visible = !Visible;
            GetViewport().SetInputAsHandled();
        }
    }
}
