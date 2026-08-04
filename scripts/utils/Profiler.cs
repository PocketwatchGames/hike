using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

// Generic CPU section profiler.
//
// SHIPPING BUILDS: every public profiling call is tagged
// [Conditional("PROFILE")]. The PROFILE symbol is defined for Debug /
// ExportDebug in hike.csproj and NOT defined for Release / ExportRelease, so
// in shipping builds the C# compiler emits no call site at all.
//
// USAGE — three flavors, pick whichever reads best at the call site:
//
//   1. Inline scope (zero declarations, recommended default):
//
//        using (Profiler.Sample("Mob.TickAI"))
//        {
//            // ... work ...
//        }
//
//   2. Inline begin/end (when a `using` block would force awkward indentation):
//
//        Profiler.Begin("Mob.PerceptionRays");
//        // ... work ...
//        Profiler.End("Mob.PerceptionRays");
//
//   3. Cached section reference (hot loops where you want to skip the lookup):
//
//        private static readonly Profiler.Section ProfTickAI =
//            Profiler.MakeSection("Mob.TickAI");
//        ...
//        ProfTickAI.Begin();
//        // ... work ...
//        ProfTickAI.End();
//
// All three accumulate into the same Section by name.
//
// CONSOLE:
//   profile 1            enable sampling
//   profile 0            disable sampling
//   profile_dump         print the table to the log
//
// IN-GAME OVERLAY:
//   F3 toggles DiagnosticsOverlay, which forces profile=1 while visible and
//   shows the same table (auto-refreshing every `profile_window` seconds).
//   Customers running shipping builds do NOT see the table — PROFILE is not
//   defined there, so all sections read 0 and the overlay just shows fps and
//   the engine monitors.
//
// GODOT EDITOR MONITORS:
//   Every section also registers a Performance.AddCustomMonitor entry under
//   `hike/<section path>`, reporting per_frame_ms. They show up in the editor
//   Debugger → Monitors tab next to the engine's built-ins, so you can graph
//   any section over time.
//
// Reported per dump:
//   calls      - how many times the section ran in the current window
//   total_ms   - wall time spent in the section
//   avg_us     - mean per call
//   max_ms     - worst single call in the window
//   ms_frame   - total_ms / frames actually rendered in the window. Compare
//                directly against frame_ms_avg; no 60 Hz assumption.
//
// Below the table, a "frame coverage" block reports frames / frame_ms_avg /
// profiled_ms_avg / unaccounted_ms_avg for the same window. profiled_ms_avg is
// the UNION of time inside any section (nesting-aware, so it doesn't
// double-count), which makes unaccounted_ms_avg the honest "cost we cannot
// see" figure — engine-side per-node dispatch and culling land there.
//
// Main-thread only: sections write into per-section fields without locking.
public static class Profiler
{
    public static bool Enabled => CVars.profile.Value;

    // Which window a table render reports over. The three readout paths each
    // want a different span:
    //   Live       - the live accumulators since the last latch/reset. Hitch
    //                log uses this so each [HITCH] dump reflects recent frames.
    //   Latched    - the last full rolling window. F3 overlay uses this so
    //                on-screen numbers update once per `profile_window`.
    //   Cumulative - everything since the last manual Reset (`profile 1` /
    //                `profile_dump`). Independent of the overlay's per-window
    //                latch, so a manual dump reports the full time you waited
    //                even while the overlay is also running and latching.
    public enum View
    {
        Live,
        Latched,
        Cumulative,
    }

    public sealed class Section
    {
        internal readonly string Name;
        internal long ActiveStart;
        internal long Total;
        internal long Max;
        internal int Calls;

        // Cumulative tally since the last manual Reset, for the Cumulative
        // view. LatchAndReset folds the live accumulators in here before
        // zeroing them, so this survives the overlay's per-window resets;
        // the manual dump reads CumulativeTotal + Total (the not-yet-folded
        // live remainder). Cleared only by Reset.
        internal long CumulativeTotal;
        internal long CumulativeMax;
        internal int CumulativeCalls;

        // Latched at the end of each rolling window by Profiler.Tick. The
        // overlay and the custom Godot monitor both read these — they update
        // once per window instead of changing every frame, so on-screen
        // numbers don't churn.
        internal long LatchedTotal;
        internal long LatchedMax;
        internal int LatchedCalls;
        internal double LatchedWindowSec;

        internal Section(string name)
        {
            Name = name;
        }

        // Value behind this section's Godot editor custom monitor. Divides by
        // the frames actually rendered in the latched window, matching the
        // table's ms_frame column — the editor graph and the on-screen table
        // must not disagree about what "per frame" means.
        internal double LatchedPerFrameMs()
        {
            if (LatchedCalls == 0)
            {
                return 0.0;
            }
            double total = LatchedTotal * (1000.0 / Stopwatch.Frequency);
            if (_framesLatched > 0)
            {
                return total / _framesLatched;
            }
            return LatchedWindowSec > 0.0 ? total / (LatchedWindowSec * 60.0) : 0.0;
        }

        [Conditional("PROFILE")]
        public void Begin()
        {
            if (!Enabled)
            {
                ActiveStart = 0L;
                return;
            }
            ActiveStart = Stopwatch.GetTimestamp();
            EnterSection(ActiveStart);
        }

        [Conditional("PROFILE")]
        public void End()
        {
            long start = ActiveStart;
            if (start == 0L)
            {
                return;
            }
            ActiveStart = 0L;
            long now = Stopwatch.GetTimestamp();
            long elapsed = now - start;
            Total += elapsed;
            if (elapsed > Max)
            {
                Max = elapsed;
            }
            Calls++;
            ExitSection(now);
        }

        // Disposable scope used by Profiler.Sample(). A struct so `using`
        // doesn't allocate; Dispose is what records the elapsed time.
        public readonly struct Scope : System.IDisposable
        {
            private readonly Section _section;
            private readonly long _start;
            internal Scope(Section section, long start)
            {
                _section = section;
                _start = start;
            }
            public void Dispose()
            {
                if (_start == 0L || _section == null)
                {
                    return;
                }
                long now = Stopwatch.GetTimestamp();
                long elapsed = now - _start;
                _section.Total += elapsed;
                if (elapsed > _section.Max)
                {
                    _section.Max = elapsed;
                }
                _section.Calls++;
                ExitSection(now);
            }
        }
    }

    private static readonly Dictionary<string, Section> _byName = new();
    private static readonly List<Section> _sections = new();
    private static long _windowStartTicks;

    // UNION of time spent inside any section, and the count of rendered frames
    // it spans. Summing the section table instead would double-count every
    // nested section (Mob.UpdateAnimation lives inside Mob.PhysicsProcess), so
    // instead we track nesting depth and accumulate only the outermost span:
    // depth 0→1 stamps a start, 1→0 banks the elapsed time. That yields exactly
    // "wall time with at least one section open", which is what the frame
    // budget should be compared against.
    private static int _sectionDepth;
    private static long _unionStartTicks;
    private static long _unionTotal;
    private static long _unionLatched;
    private static long _unionCumulative;
    private static int _framesThisWindow;
    private static int _framesLatched;
    private static int _framesCumulative;

    private static void EnterSection(long nowTicks)
    {
        if (_sectionDepth++ == 0)
        {
            _unionStartTicks = nowTicks;
        }
    }

    private static void ExitSection(long nowTicks)
    {
        if (_sectionDepth > 0 && --_sectionDepth == 0 && _unionStartTicks != 0L)
        {
            _unionTotal += nowTicks - _unionStartTicks;
            _unionStartTicks = 0L;
        }
    }

    // Called once per rendered frame (DiagnosticsOverlay._Process, which runs
    // unconditionally). Gives the table a real frame count so per-frame numbers
    // reflect the frames actually drawn instead of an assumed 60Hz.
    [Conditional("PROFILE")]
    public static void MarkFrame()
    {
        if (!Enabled)
        {
            return;
        }
        _framesThisWindow++;
    }

    // Start of the Cumulative window. Stamped only by Reset (i.e. `profile 1`
    // and `profile_dump`), never by the overlay's per-window LatchAndReset, so
    // a manual dump reports the full span since you enabled profiling
    // regardless of whether the overlay is running.
    private static long _manualWindowStartTicks;

    // Lightweight event counters — no Begin/End, just incremented at call
    // sites. Use for "how many of X happened in this window" metrics like
    // entity-spawn counts per chunk-load. Surfaced under engine monitors,
    // latched on the same cadence as section totals. Insertion-ordered list
    // gives a stable print order across windows.
    private static readonly Dictionary<string, int> _counters = new();
    private static readonly Dictionary<string, int> _latchedCounters = new();
    // Cumulative counter tallies since the last manual Reset, folded from
    // _counters on each LatchAndReset (parallel to Section.CumulativeTotal) so
    // the Cumulative view's counters survive the overlay's per-window resets.
    private static readonly Dictionary<string, int> _cumulativeCounters = new();
    private static readonly List<string> _counterNames = new();

    // Per-frame gauges — last-value-wins snapshots (vs counters' running sum),
    // for "how many X right now" readouts. Latched per window like counters but
    // never reset to zero, so the overlay shows the most recent frame's value.
    private static readonly Dictionary<string, long> _gauges = new();
    private static readonly Dictionary<string, long> _latchedGauges = new();
    private static readonly List<string> _gaugeNames = new();

    // Window-relative GC tracking. Baseline is the GC.CollectionCount(N) value
    // at the start of the current window; AppendEngineMonitors subtracts the
    // baseline from the current count to get "collections in this window so
    // far". On every window roll (LatchAndReset) and every manual Reset we
    // re-seed the baseline so a fresh window starts at zero.
    private static int _windowGcBaseline0;
    private static int _windowGcBaseline1;
    private static int _windowGcBaseline2;

    // GC baseline for the Cumulative view — seeded only on Reset, so the manual
    // dump reports collections since `profile 1` rather than since the
    // overlay's last window roll.
    private static int _manualGcBaseline0;
    private static int _manualGcBaseline1;
    private static int _manualGcBaseline2;

    public static Section MakeSection(string name)
    {
        return GetOrCreate(name);
    }

    public static Section.Scope Sample(string name)
    {
#if PROFILE
        if (!Enabled)
        {
            return default;
        }
        Section s = GetOrCreate(name);
        long start = Stopwatch.GetTimestamp();
        EnterSection(start);
        return new Section.Scope(s, start);
#else
        return default;
#endif
    }

    [Conditional("PROFILE")]
    public static void IncrementCounter(string name, int delta = 1)
    {
        if (!Enabled)
        {
            return;
        }
        if (!_counters.TryGetValue(name, out int current))
        {
            _counters[name] = 0;
            _latchedCounters[name] = 0;
            _cumulativeCounters[name] = 0;
            _counterNames.Add(name);
            current = 0;
        }
        _counters[name] = current + delta;
    }

    // Set a per-frame gauge (last value wins, not summed). Use for instantaneous
    // "current count" readouts like how many mobs are animating this frame.
    [Conditional("PROFILE")]
    public static void SetGauge(string name, long value)
    {
        if (!Enabled)
        {
            return;
        }
        if (!_gauges.ContainsKey(name))
        {
            _gaugeNames.Add(name);
            _latchedGauges[name] = value;
        }
        _gauges[name] = value;
    }

    [Conditional("PROFILE")]
    public static void Begin(string name)
    {
        if (!Enabled)
        {
            return;
        }
        Section s = GetOrCreate(name);
        s.ActiveStart = Stopwatch.GetTimestamp();
        EnterSection(s.ActiveStart);
    }

    [Conditional("PROFILE")]
    public static void End(string name)
    {
        if (!_byName.TryGetValue(name, out Section s))
        {
            return;
        }
        long start = s.ActiveStart;
        if (start == 0L)
        {
            return;
        }
        s.ActiveStart = 0L;
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - start;
        s.Total += elapsed;
        if (elapsed > s.Max)
        {
            s.Max = elapsed;
        }
        s.Calls++;
        ExitSection(now);
    }

    private static Section GetOrCreate(string name)
    {
        if (!_byName.TryGetValue(name, out Section s))
        {
            s = new Section(name);
            _byName[name] = s;
            _sections.Add(s);
            RegisterCustomMonitor(s);
        }
        return s;
    }

    // Each Profiler.Section also surfaces as a Performance.AddCustomMonitor
    // entry, reading the section's latched per_frame_ms. The Godot editor's
    // Debugger → Monitors tab plots these alongside the engine's own. Names
    // use slashes for grouping (e.g. "hike/Mob/PhysicsProcess"). Skipped
    // entirely when PROFILE is undefined so a shipping build doesn't waste
    // RemoteDebugger bandwidth on monitors that can't move.
    private static void RegisterCustomMonitor(Section s)
    {
#if PROFILE
        Godot.StringName id = new Godot.StringName("hike/" + s.Name.Replace('.', '/'));
        // Performance.HasCustomMonitor will be false here — sections are
        // unique by name on the C# side. AddCustomMonitor takes a Callable
        // that returns the current value each time the editor polls.
        Godot.Performance.AddCustomMonitor(id, Godot.Callable.From(s.LatchedPerFrameMs));
#endif
    }

    // Periodic latch + reset. Called from DiagnosticsOverlay._Process every
    // frame; auto-resets the live accumulators every `profile_window` seconds
    // so the table and custom monitors show the cost of the LAST window
    // rather than cumulative-since-startup. Manual `profile_dump` continues
    // to work — it prints the live (post-last-latch) state and clears it.
    [Conditional("PROFILE")]
    public static void Tick()
    {
        if (!Enabled)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        if (_windowStartTicks == 0L)
        {
            _windowStartTicks = now;
            return;
        }
        double elapsedSec = (now - _windowStartTicks) / (double)Stopwatch.Frequency;
        double windowSec = CVars.profileWindow.Value;
        if (windowSec <= 0.0 || elapsedSec < windowSec)
        {
            return;
        }
        LatchAndReset(elapsedSec, now);
    }

    private static void LatchAndReset(double elapsedSec, long now)
    {
        for (int i = 0; i < _sections.Count; i++)
        {
            Section s = _sections[i];
            s.LatchedTotal = s.Total;
            s.LatchedMax = s.Max;
            s.LatchedCalls = s.Calls;
            s.LatchedWindowSec = elapsedSec;
            // Fold the live window into the cumulative tally before zeroing, so
            // the manual dump's Cumulative view spans the overlay's resets.
            s.CumulativeTotal += s.Total;
            if (s.Max > s.CumulativeMax)
            {
                s.CumulativeMax = s.Max;
            }
            s.CumulativeCalls += s.Calls;
            s.Total = 0;
            s.Max = 0;
            s.Calls = 0;
            s.ActiveStart = 0;
        }
        // Latch + reset bare counters that aren't tied to a Section. Same
        // window cadence as the section table so the per-window numbers
        // line up.
        for (int i = 0; i < _counterNames.Count; i++)
        {
            string n = _counterNames[i];
            _latchedCounters[n] = _counters[n];
            _cumulativeCounters[n] += _counters[n];
            _counters[n] = 0;
        }
        // Gauges latch their current (last-set) value but are NOT reset — they
        // represent an instantaneous reading, not a per-window accumulation.
        for (int i = 0; i < _gaugeNames.Count; i++)
        {
            string n = _gaugeNames[i];
            _latchedGauges[n] = _gauges[n];
        }
        _unionLatched = _unionTotal;
        _unionCumulative += _unionTotal;
        _unionTotal = 0;
        _framesLatched = _framesThisWindow;
        _framesCumulative += _framesThisWindow;
        _framesThisWindow = 0;
        // A section left open across the latch would otherwise strand the depth
        // counter above zero and suppress union tracking for the rest of the run.
        _sectionDepth = 0;
        _unionStartTicks = 0L;
        _windowGcBaseline0 = System.GC.CollectionCount(0);
        _windowGcBaseline1 = System.GC.CollectionCount(1);
        _windowGcBaseline2 = System.GC.CollectionCount(2);
        _windowStartTicks = now;
    }

    // Manual dump (console `profile_dump`) reports the Cumulative view so the
    // window spans the full time since `profile 1` even while the overlay is
    // running and latching.
    public static void Dump()
    {
        Godot.GD.Print(FormatTable(View.Cumulative));
    }

    // Builds the same one-line-per-section table that Dump prints. The overlay
    // calls this every refresh with View.Latched so on-screen numbers reflect
    // the previous full window rather than a partial one that resets every
    // frame; the hitch log uses View.Live for a recent-frames snapshot.
    public static string FormatTable(View view)
    {
        StringBuilder sb = new StringBuilder();
        AppendTable(sb, view);
        return sb.ToString();
    }

    public static void AppendTable(StringBuilder sb, View view)
    {
        long now = Stopwatch.GetTimestamp();
        double tickToMs = 1000.0 / Stopwatch.Frequency;

        sb.Append("[profile]");
        if (view == View.Latched)
        {
            sb.Append(" (latched window)");
        }
        else
        {
            long startTicks = view == View.Cumulative ? _manualWindowStartTicks : _windowStartTicks;
            double elapsedSec = startTicks == 0L
                ? 0.0
                : (now - startTicks) / (double)Stopwatch.Frequency;
            sb.Append(' ').Append(elapsedSec.ToString("F2")).Append("s window");
            if (view == View.Cumulative)
            {
                sb.Append(" (since profile reset)");
            }
        }
        sb.Append('\n');
        // ms_frame is total_ms divided by the frames ACTUALLY rendered in the
        // window. This used to divide by windowSec*60 — i.e. it assumed 60fps —
        // which silently under-reported every row by frame_ms/16.67 (over 3x at
        // 16fps) and made _PhysicsProcess sections look far cheaper than they
        // were. Falls back to the 60Hz assumption only if no frames were marked.
        sb.Append("  section                          calls   total_ms   avg_us  max_ms       ms_frame\n");

        // Overlay path filters out sections below the cutoff so the table
        // stays scannable. Manual dump / hitch dump path (useLatched=false)
        // always shows everything — those are one-shot debug snapshots where
        // missing rows would be confusing.
        double minPerFrameMs = view == View.Latched ? CVars.profileMinPerFrameMs.Value : 0.0;

        for (int i = 0; i < _sections.Count; i++)
        {
            Section s = _sections[i];
            long total;
            long max;
            int calls;
            double windowSec;
            if (view == View.Latched)
            {
                total = s.LatchedTotal;
                max = s.LatchedMax;
                calls = s.LatchedCalls;
                windowSec = s.LatchedWindowSec;
            }
            else if (view == View.Cumulative)
            {
                // Cumulative tally plus the live remainder not yet folded by a
                // latch (or all of it, when the overlay isn't running).
                total = s.CumulativeTotal + s.Total;
                max = s.Max > s.CumulativeMax ? s.Max : s.CumulativeMax;
                calls = s.CumulativeCalls + s.Calls;
                windowSec = _manualWindowStartTicks == 0L
                    ? 0.0
                    : (now - _manualWindowStartTicks) / (double)Stopwatch.Frequency;
            }
            else
            {
                total = s.Total;
                max = s.Max;
                calls = s.Calls;
                windowSec = _windowStartTicks == 0L
                    ? 0.0
                    : (now - _windowStartTicks) / (double)Stopwatch.Frequency;
            }
            if (calls == 0)
            {
                continue;
            }
            double totalMs = total * tickToMs;
            double avg = (totalMs * 1000.0) / calls;
            double maxMs = max * tickToMs;
            int frames = ViewFrames(view);
            double perFrame = frames > 0
                ? totalMs / frames
                : (windowSec > 0.0 ? totalMs / (windowSec * 60.0) : 0.0);
            if (perFrame < minPerFrameMs)
            {
                continue;
            }
            sb.Append("  ")
              .Append(s.Name.PadRight(32))
              .Append(calls.ToString().PadLeft(6))
              .Append(' ').Append(totalMs.ToString("F2").PadLeft(10))
              .Append(' ').Append(avg.ToString("F2").PadLeft(8))
              .Append(' ').Append(maxMs.ToString("F3").PadLeft(7))
              .Append(' ').Append(perFrame.ToString("F3").PadLeft(13))
              .Append('\n');
        }
        AppendEngineMonitors(sb, view);
    }

    // Engine monitors that explain frames the C# sections don't account for.
    // FPS / TIME_PROCESS / TIME_PHYSICS_PROCESS show how the frame is split;
    // RENDER_TOTAL_DRAW_CALLS_IN_FRAME / RENDER_TOTAL_OBJECTS_IN_FRAME show
    // whether render submission is the cost (each shadow-casting sprite
    // counts as a separate draw); PHYSICS_3D_ACTIVE_OBJECTS / COLLISION_PAIRS
    // show whether Jolt's broadphase + narrowphase is the cost.
    private static void AppendEngineMonitors(StringBuilder sb, View view)
    {
        // Coverage: how much of the real frame the section table actually
        // explains. All three come from the SAME window, so they're directly
        // comparable — unlike the instantaneous monitors below. A large
        // unaccounted figure means the cost is somewhere no section wraps
        // (Godot's own per-node dispatch and culling are the usual culprits,
        // and no amount of section-level tuning will touch them).
        int frames = ViewFrames(view);
        if (frames > 0)
        {
            double windowMs = ViewWindowSec(view) * 1000.0;
            double profiledMs = ViewUnionTicks(view) * (1000.0 / Stopwatch.Frequency);
            double windowFrameMs = windowMs / frames;
            double profiledPerFrame = profiledMs / frames;
            sb.Append("  --- frame coverage (this window) ---\n");
            AppendValue(sb, "frames", frames.ToString());
            AppendValue(sb, "frame_ms_avg", windowFrameMs.ToString("F2"));
            AppendValue(sb, "profiled_ms_avg", profiledPerFrame.ToString("F2"));
            AppendValue(sb, "unaccounted_ms_avg", (windowFrameMs - profiledPerFrame).ToString("F2"));
        }
        sb.Append("  --- engine monitors (instantaneous) ---\n");
        AppendMonitor(sb, "fps", Godot.Performance.Monitor.TimeFps, "F1");
        // Instantaneous budget for one RENDERED frame. Compare against
        // process_ms + physics_process_ms; what's left is render submission /
        // GPU / vsync. This is a single-frame sample and jitters — for anything
        // window-scoped use frame_ms_avg in the coverage block above, which
        // shares its denominator with the ms_frame column.
        double fps = Godot.Performance.GetMonitor(Godot.Performance.Monitor.TimeFps);
        double frameMs = fps > 0.0 ? 1000.0 / fps : 0.0;
        sb.Append("  ").Append("frame_ms".PadRight(32)).Append(frameMs.ToString("F2").PadLeft(12)).Append('\n');
        AppendMonitor(sb, "process_ms", Godot.Performance.Monitor.TimeProcess, "F2", 1000.0);
        AppendMonitor(sb, "physics_process_ms", Godot.Performance.Monitor.TimePhysicsProcess, "F2", 1000.0);
        AppendMonitor(sb, "render_draw_calls", Godot.Performance.Monitor.RenderTotalDrawCallsInFrame, "F0");
        AppendMonitor(sb, "render_objects", Godot.Performance.Monitor.RenderTotalObjectsInFrame, "F0");
        AppendMonitor(sb, "render_primitives", Godot.Performance.Monitor.RenderTotalPrimitivesInFrame, "F0");
        // Scene size. These do NOT appear in any C# section but the engine pays
        // for them every frame: Godot walks the node tree to dispatch
        // notifications and walks every VisualInstance3D to cull it, whether or
        // not it ends up drawn. So a large node_count with a small
        // render_objects means the frame is going into engine-side traversal of
        // resident-but-invisible scene content — invisible to this profiler,
        // and not fixable by optimizing any section below.
        AppendMonitor(sb, "node_count", Godot.Performance.Monitor.ObjectNodeCount, "F0");
        AppendMonitor(sb, "object_count", Godot.Performance.Monitor.ObjectCount, "F0");
        AppendMonitor(sb, "orphan_node_count", Godot.Performance.Monitor.ObjectOrphanNodeCount, "F0");
        AppendMonitor(sb, "physics_active_objects", Godot.Performance.Monitor.Physics3DActiveObjects, "F0");
        AppendMonitor(sb, "physics_collision_pairs", Godot.Performance.Monitor.Physics3DCollisionPairs, "F0");
        AppendMonitor(sb, "physics_islands", Godot.Performance.Monitor.Physics3DIslandCount, "F0");
        // Fx live counts. Maintained by Fx._Ready/_ExitTree. A spike here
        // (especially in active_audio / active_particles) at the moment fps
        // tanks is the smoking gun for footstep / loop-effect overspawn.
        sb.Append("  ").Append("fx_active".PadRight(32)).Append(Fx.ActiveCount.ToString().PadLeft(12)).Append('\n');
        sb.Append("  ").Append("fx_active_audio".PadRight(32)).Append(Fx.ActiveAudioCount.ToString().PadLeft(12)).Append('\n');
        sb.Append("  ").Append("fx_active_particles".PadRight(32)).Append(Fx.ActiveParticlesCount.ToString().PadLeft(12)).Append('\n');
        // SharedWalkabilityCache size. A high hit ratio at swarm density is the
        // whole point of the cache; if hits ≪ misses the quantum is too tight
        // or mob profiles vary too much for sharing. hits/misses ride the
        // ordinary counter path below, so they honour the requested view — they
        // used to print a latched value that only advanced while the F3 overlay
        // was open, and so read 0 from every console `profile_dump`.
        sb.Append("  ").Append("walkability_cache_entries".PadRight(32)).Append(SharedWalkabilityCache.EntryCount.ToString().PadLeft(12)).Append('\n');
        // GC collections in the current window. Gen0 churn that climbs into
        // the dozens-per-window is the smoking gun for per-frame allocations
        // in hot paths; gen2 collections are rare and expensive (correlates
        // strongly with frame hitches when they happen).
        // Cumulative view counts collections since the manual reset; Live and
        // Latched count since the overlay's last window roll.
        int gcBase0 = view == View.Cumulative ? _manualGcBaseline0 : _windowGcBaseline0;
        int gcBase1 = view == View.Cumulative ? _manualGcBaseline1 : _windowGcBaseline1;
        int gcBase2 = view == View.Cumulative ? _manualGcBaseline2 : _windowGcBaseline2;
        int gcWin0 = System.GC.CollectionCount(0) - gcBase0;
        int gcWin1 = System.GC.CollectionCount(1) - gcBase1;
        int gcWin2 = System.GC.CollectionCount(2) - gcBase2;
        sb.Append("  ").Append("gc_gen0_window".PadRight(32)).Append(gcWin0.ToString().PadLeft(12)).Append('\n');
        sb.Append("  ").Append("gc_gen1_window".PadRight(32)).Append(gcWin1.ToString().PadLeft(12)).Append('\n');
        sb.Append("  ").Append("gc_gen2_window".PadRight(32)).Append(gcWin2.ToString().PadLeft(12)).Append('\n');
        // Event counters. Live (post-Reset window) for the hitch dump; latched
        // for the overlay so the on-screen value doesn't churn every frame.
        for (int i = 0; i < _counterNames.Count; i++)
        {
            string n = _counterNames[i];
            int v;
            if (view == View.Latched)
            {
                v = _latchedCounters[n];
            }
            else if (view == View.Cumulative)
            {
                v = _cumulativeCounters[n] + _counters[n];
            }
            else
            {
                v = _counters[n];
            }
            sb.Append("  ").Append(n.PadRight(32)).Append(v.ToString().PadLeft(12)).Append('\n');
        }
        // Per-frame gauges (instantaneous readings, not per-window sums) — no
        // cumulative concept, so non-Latched views show the current value.
        for (int i = 0; i < _gaugeNames.Count; i++)
        {
            string n = _gaugeNames[i];
            long v = view == View.Latched ? _latchedGauges[n] : _gauges[n];
            sb.Append("  ").Append(n.PadRight(32)).Append(v.ToString().PadLeft(12)).Append('\n');
        }
    }

    // Per-view accessors for the window-scoped aggregates. Latched reads the
    // last full window; Cumulative folds the not-yet-latched live remainder in,
    // matching how the section rows treat CumulativeTotal + Total.
    private static int ViewFrames(View view) => view switch
    {
        View.Latched => _framesLatched,
        View.Cumulative => _framesCumulative + _framesThisWindow,
        _ => _framesThisWindow,
    };

    private static long ViewUnionTicks(View view) => view switch
    {
        View.Latched => _unionLatched,
        View.Cumulative => _unionCumulative + _unionTotal,
        _ => _unionTotal,
    };

    private static double ViewWindowSec(View view)
    {
        if (view == View.Latched)
        {
            // Every section latches the same window length; take the first
            // non-zero one rather than storing a duplicate copy.
            for (int i = 0; i < _sections.Count; i++)
            {
                if (_sections[i].LatchedWindowSec > 0.0)
                {
                    return _sections[i].LatchedWindowSec;
                }
            }
            return 0.0;
        }
        long startTicks = view == View.Cumulative ? _manualWindowStartTicks : _windowStartTicks;
        return startTicks == 0L
            ? 0.0
            : (Stopwatch.GetTimestamp() - startTicks) / (double)Stopwatch.Frequency;
    }

    private static void AppendValue(StringBuilder sb, string label, string value)
    {
        sb.Append("  ").Append(label.PadRight(32)).Append(value.PadLeft(12)).Append('\n');
    }

    private static void AppendMonitor(StringBuilder sb, string label, Godot.Performance.Monitor m, string fmt, double scale = 1.0)
    {
        double v = Godot.Performance.GetMonitor(m) * scale;
        sb.Append("  ").Append(label.PadRight(32)).Append(v.ToString(fmt).PadLeft(12)).Append('\n');
    }

    public static void Reset()
    {
        for (int i = 0; i < _sections.Count; i++)
        {
            Section s = _sections[i];
            s.Total = 0;
            s.Max = 0;
            s.Calls = 0;
            s.ActiveStart = 0;
            s.CumulativeTotal = 0;
            s.CumulativeMax = 0;
            s.CumulativeCalls = 0;
        }
        for (int i = 0; i < _counterNames.Count; i++)
        {
            _counters[_counterNames[i]] = 0;
            _cumulativeCounters[_counterNames[i]] = 0;
        }
        _unionTotal = 0;
        _unionCumulative = 0;
        _unionStartTicks = 0L;
        _sectionDepth = 0;
        _framesThisWindow = 0;
        _framesCumulative = 0;
        long now = Stopwatch.GetTimestamp();
        _windowGcBaseline0 = System.GC.CollectionCount(0);
        _windowGcBaseline1 = System.GC.CollectionCount(1);
        _windowGcBaseline2 = System.GC.CollectionCount(2);
        _manualGcBaseline0 = _windowGcBaseline0;
        _manualGcBaseline1 = _windowGcBaseline1;
        _manualGcBaseline2 = _windowGcBaseline2;
        _windowStartTicks = now;
        _manualWindowStartTicks = now;
    }

    public static void DumpAndReset()
    {
        Dump();
        Reset();
    }
}
