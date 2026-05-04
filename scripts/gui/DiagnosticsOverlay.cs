using Godot;

// Always-on-top diagnostics screen. Toggled with F3. Lives on its own
// CanvasLayer above the HUD but below the console so the console can still
// cover it when open. Available in shipping builds — customers can hit F3
// to see fps + Godot engine monitors. In Debug / ExportDebug builds the
// PROFILE symbol is also defined, so the same overlay also renders the C#
// Profiler section table on a rolling window.
public partial class DiagnosticsOverlay : CanvasLayer
{
    // ConsoleUI sits at layer 100; keep this just below so the console
    // visually wins when both are open.
    private const int OverlayLayer = 99;

    // Refresh cadence for the on-screen text. The profiler latches its own
    // rolling window separately (see CVars.profileWindow); this is just how
    // often we re-render the label string.
    private const double UpdateIntervalSeconds = 0.25;

    private RichTextLabel _label;
    private double _accum;

    // Tracks whether we forced `profile` on while the overlay is visible so
    // we can restore the user's prior setting when it gets hidden again.
    private bool _forcedProfileOn;
    private bool _profilePriorState;

    // Hitch detector. When CVars.hitchLog is true, we watch every frame's
    // delta and dump the live profile table whenever delta exceeds the
    // threshold. Independent of overlay visibility — set hitch_log=1 from
    // the console and let it run with the overlay hidden.
    private bool _hitchForcedProfileOn;
    private bool _hitchProfilePriorState;
    private bool _hitchSkipFirstFrame = true;

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

        _label = new RichTextLabel();
        _label.BbcodeEnabled = true;
        _label.FitContent = true;
        _label.AutowrapMode = TextServer.AutowrapMode.Off;
        _label.ScrollActive = false;
        _label.MouseFilter = Control.MouseFilterEnum.Ignore;
        // [code]...[/code] forces Godot's bundled monospace font without any
        // asset dependency, so the table columns line up in shipping builds.
        _label.Text = "[code]FPS --[/code]";
        // Width has to be non-zero or RichTextLabel doesn't lay out content.
        // Picked wide enough for the table; FitContent shrinks unused rows.
        _label.CustomMinimumSize = new Vector2(620, 0);
        margin.AddChild(_label);

        // Run while the rest of the game is paused so we can still read the
        // counter while inspecting a paused frame.
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Process(double delta)
    {
        // Always-on hitch detector. Runs even when the overlay is hidden so
        // hitches can be caught in the wild.
        UpdateHitchDetector(delta);

        if (!Visible)
        {
            return;
        }
        // Drive the profiler's rolling-window latch every frame while we're
        // visible. Cheap when profile is off; otherwise pegs LatchedTotal /
        // LatchedCalls / LatchedWindowSec at CVars.profileWindow cadence so
        // the table below shows stable per-window averages.
        Profiler.Tick();

        _accum += delta;
        if (_accum < UpdateIntervalSeconds)
        {
            return;
        }
        _accum = 0;
        _label.Text = BuildText();
    }

    // Watches per-frame delta and dumps the profiler table whenever a frame
    // exceeds CVars.hitchThresholdMs. Forces `profile` on while hitch_log
    // is enabled so the dumped table has live data; restores the prior
    // setting when hitch_log is turned off. Resets the profiler after each
    // dump so consecutive hitches don't bleed into each other.
    private void UpdateHitchDetector(double delta)
    {
        bool enabled = CVars.hitchLog.Value;

        if (enabled && !_hitchForcedProfileOn)
        {
            _hitchProfilePriorState = CVars.profile.Value;
            CVars.profile.Value = true;
            _hitchForcedProfileOn = true;
            _hitchSkipFirstFrame = true;
        }
        else if (!enabled && _hitchForcedProfileOn)
        {
            // Don't stomp the user's setting if the F3 overlay also forced
            // it on — only restore if hitch_log was the only thing holding
            // it on.
            if (!_forcedProfileOn)
            {
                CVars.profile.Value = _hitchProfilePriorState;
            }
            _hitchForcedProfileOn = false;
        }

        if (!enabled)
        {
            return;
        }

        // Skip the first frame after enabling — delta is the wall-clock gap
        // since the last _Process call, which may be huge if the game just
        // unpaused or hitch_log just flipped on.
        if (_hitchSkipFirstFrame)
        {
            _hitchSkipFirstFrame = false;
            return;
        }

        double frameMs = delta * 1000.0;
        if (frameMs < CVars.hitchThresholdMs.Value)
        {
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("[HITCH] frame=").Append(frameMs.ToString("F1")).Append("ms\n");
        Profiler.AppendTable(sb, useLatched: false);
        Godot.GD.Print(sb.ToString());
        Profiler.Reset();
    }

    private string BuildText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[code]");
        sb.Append("FPS ").Append(Engine.GetFramesPerSecond().ToString("F0")).Append('\n');
        sb.Append('\n');
        Profiler.AppendTable(sb, useLatched: true);
        sb.Append("[/code]");
        return sb.ToString();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.F3)
        {
            Visible = !Visible;
            UpdateProfilingState();
            GetViewport().SetInputAsHandled();
        }
    }

    // While the overlay is visible we force CVars.profile on so the table
    // has live data — customers shouldn't need console access to see the
    // profile. When the overlay is hidden again we restore the prior CVar
    // value so headless / cvars.txt overrides keep working.
    private void UpdateProfilingState()
    {
        if (Visible && !_forcedProfileOn)
        {
            _profilePriorState = CVars.profile.Value;
            CVars.profile.Value = true;
            _forcedProfileOn = true;
        }
        else if (!Visible && _forcedProfileOn)
        {
            CVars.profile.Value = _profilePriorState;
            _forcedProfileOn = false;
        }
    }
}
