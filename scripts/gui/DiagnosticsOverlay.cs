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

    // Process last in the frame (higher priority = later in Godot) so that when
    // the hitch detector reads the live profiler accumulators they already hold
    // every other node's _Process work for the just-elapsed frame. Without this
    // the dump could miss work from nodes that process after the overlay.
    private const int HitchProcessPriority = 1000;

    // Refresh cadence for the on-screen text. The profiler latches its own
    // rolling window separately (see CVars.profileWindow); this is just how
    // often we re-render the label string.
    private const double UpdateIntervalSeconds = 0.25;

    // Width of the scroll column. Matches the RichTextLabel's CustomMinimumSize
    // plus margin (8+8) plus space for the vertical scrollbar.
    private const float ScrollColumnWidth = 660f;

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

    // Per-frame GC tracking. Sentinel -1 means "no baseline yet" — the next
    // frame seeds it and reports a delta of zero. Re-seeded on every enable
    // of hitch_log so a long pause before enabling doesn't surface as a fake
    // collection on the first hitched frame.
    private int _prevGc0 = -1;
    private int _prevGc1 = -1;
    private int _prevGc2 = -1;

    public override void _Ready()
    {
        Layer = OverlayLayer;
        // Default off; press F3 to toggle. Defaulting visible would clutter
        // every screenshot, default off is friendlier.
        Visible = false;

        // Outer ScrollContainer caps the panel height to the viewport so a
        // long profiler table doesn't run off the bottom of the screen. It
        // anchors top-stretches-bottom with a fixed width column on the left;
        // when the content fits, the PanelContainer inside hugs its size and
        // the empty area below stays invisible. MouseFilter=Pass so the wheel
        // scrolls when hovered but events still propagate to gameplay.
        var scroll = new ScrollContainer();
        scroll.AnchorLeft = 0f;
        scroll.AnchorTop = 0f;
        scroll.AnchorRight = 0f;
        scroll.AnchorBottom = 1f;
        scroll.OffsetLeft = 8f;
        scroll.OffsetTop = 8f;
        scroll.OffsetRight = 8f + ScrollColumnWidth;
        scroll.OffsetBottom = -8f;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        scroll.MouseFilter = Control.MouseFilterEnum.Pass;
        AddChild(scroll);

        var panel = new PanelContainer();
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        panel.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        scroll.AddChild(panel);

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
        ProcessPriority = HitchProcessPriority;
    }

    public override void _Process(double delta)
    {
        // Always-on hitch detector. Runs even when the overlay is hidden so
        // hitches can be caught in the wild.
        UpdateHitchDetector(delta);

        // debug_slopes pins the overlay visible so the slope readout shows
        // without F3. Hidden again on the next frame after the CVar flips
        // off, unless F3 had it on independently.
        if (CVars.debugSlopes.Value && !Visible)
        {
            Visible = true;
            UpdateProfilingState();
        }

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
            _prevGc0 = -1;
            _prevGc1 = -1;
            _prevGc2 = -1;
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

        // Sample GC counts every frame so the hitch log can report exactly
        // how many collections fell on the hitched frame. CollectionCount is
        // cumulative since process start; the per-frame delta is the
        // interesting number. Has to run even on the skip-first-frame path so
        // the baseline gets seeded.
        int gc0 = System.GC.CollectionCount(0);
        int gc1 = System.GC.CollectionCount(1);
        int gc2 = System.GC.CollectionCount(2);
        int dGc0 = _prevGc0 < 0 ? 0 : gc0 - _prevGc0;
        int dGc1 = _prevGc1 < 0 ? 0 : gc1 - _prevGc1;
        int dGc2 = _prevGc2 < 0 ? 0 : gc2 - _prevGc2;
        _prevGc0 = gc0;
        _prevGc1 = gc1;
        _prevGc2 = gc2;

        // Skip the first frame after enabling — delta is the wall-clock gap
        // since the last _Process call, which may be huge if the game just
        // unpaused or hitch_log just flipped on.
        if (_hitchSkipFirstFrame)
        {
            _hitchSkipFirstFrame = false;
            IsolateNextFrame();
            return;
        }

        double frameMs = delta * 1000.0;
        if (frameMs < CVars.hitchThresholdMs.Value)
        {
            // Non-hitch frame: clear the accumulators so the NEXT frame starts
            // clean. With the overlay processing last (HitchProcessPriority),
            // this makes every [HITCH] dump's table cover exactly the one
            // hitched frame instead of every frame since the previous hitch —
            // so total_ms/calls, not just max_ms, attribute the spike.
            IsolateNextFrame();
            return;
        }

        // Frame envelope. Sample Godot's process/physics monitors at hitch
        // time (not via the trailing engine-monitors block, which can lag a
        // frame). gap_ms is what's left after accounting for _Process and
        // _PhysicsProcess on the main thread — render submission, GPU sync,
        // vsync wait, shader compiles, and first-touch resource loads all
        // land here. Large gap_ms with small process_ms is the smoking gun
        // for render-side hitches that the C# profiler can't see.
        double processMs = Godot.Performance.GetMonitor(Godot.Performance.Monitor.TimeProcess) * 1000.0;
        double physicsMs = Godot.Performance.GetMonitor(Godot.Performance.Monitor.TimePhysicsProcess) * 1000.0;
        double gapMs = frameMs - processMs - physicsMs;

        var sb = new System.Text.StringBuilder();
        sb.Append("[HITCH] frame=").Append(frameMs.ToString("F1")).Append("ms");
        sb.Append(" process=").Append(processMs.ToString("F1"));
        sb.Append(" physics=").Append(physicsMs.ToString("F1"));
        sb.Append(" gap=").Append(gapMs.ToString("F1"));
        sb.Append(" gc_this_frame=").Append(dGc0).Append('/').Append(dGc1).Append('/').Append(dGc2);
        sb.Append('\n');
        Profiler.AppendTable(sb, Profiler.View.Live);
        Godot.GD.Print(sb.ToString());
        Profiler.Reset();
    }

    // Clears the live profiler accumulators so the next frame is measured in
    // isolation. Only when the overlay is hidden — when it's visible, Profiler
    // .Tick() owns the rolling window for the on-screen table and a per-frame
    // reset would flatten it to single-frame noise. With the overlay hidden
    // (the primary hitch-hunting mode) this gives clean single-frame dumps.
    private void IsolateNextFrame()
    {
        if (!Visible)
        {
            Profiler.Reset();
        }
    }

    private string BuildText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[code]");
        sb.Append("FPS ").Append(Engine.GetFramesPerSecond().ToString("F0")).Append('\n');
        if (CVars.debugSlopes.Value)
        {
            AppendSlopeSection(sb);
        }
        sb.Append('\n');
        Profiler.AppendTable(sb, Profiler.View.Latched);
        sb.Append("[/code]");
        return sb.ToString();
    }

    // Slope debug readout. "floor" tracks the current standing surface;
    // "lastWall" sticks until cleared so a quick run-into-and-back shows the
    // hit angle even after the contact ends. Age is wall-clock seconds since
    // the last hit, sourced from World.GameTimeMs so the value is in sync
    // with the [slope] log lines.
    private void AppendSlopeSection(System.Text.StringBuilder sb)
    {
        sb.Append('\n');
        if (float.IsNaN(Player.DebugFloorAngleDeg))
        {
            sb.Append("floor    : airborne\n");
        }
        else
        {
            sb.Append("floor    : ").Append(Player.DebugFloorAngleDeg.ToString("F1")).Append("°\n");
        }
        if (!Player.DebugHasWallHit)
        {
            sb.Append("lastWall : none\n");
            return;
        }
        ulong nowMs = World.Current?.GameTimeMs ?? 0;
        double ageSec = nowMs > Player.DebugLastWallHitMs
            ? (nowMs - Player.DebugLastWallHitMs) / 1000.0
            : 0.0;
        Vector3 n = Player.DebugLastWallNormal;
        Vector3 p = Player.DebugLastWallPosition;
        sb.Append("lastWall : ").Append(Player.DebugLastWallAngleDeg.ToString("F1")).Append("° ")
          .Append(ageSec.ToString("F1")).Append("s ago\n");
        sb.Append("  normal : (").Append(n.X.ToString("F2")).Append(", ")
          .Append(n.Y.ToString("F2")).Append(", ").Append(n.Z.ToString("F2")).Append(")\n");
        sb.Append("  at     : (").Append(p.X.ToString("F2")).Append(", ")
          .Append(p.Y.ToString("F2")).Append(", ").Append(p.Z.ToString("F2")).Append(")\n");
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
