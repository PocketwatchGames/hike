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
//   per_frame  - total_ms / window-seconds * 60. Rough ms/frame at 60 Hz.
//
// Main-thread only: sections write into per-section fields without locking.
public static class Profiler
{
    public static bool Enabled => CVars.profile.Value;

    public sealed class Section
    {
        internal readonly string Name;
        internal long ActiveStart;
        internal long Total;
        internal long Max;
        internal int Calls;

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

        internal double LatchedPerFrameMs()
        {
            if (LatchedCalls == 0 || LatchedWindowSec <= 0.0)
            {
                return 0.0;
            }
            double total = LatchedTotal * (1000.0 / Stopwatch.Frequency);
            return total / (LatchedWindowSec * 60.0);
        }

        [Conditional("PROFILE")]
        public void Begin()
        {
            ActiveStart = Enabled ? Stopwatch.GetTimestamp() : 0L;
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
            long elapsed = Stopwatch.GetTimestamp() - start;
            Total += elapsed;
            if (elapsed > Max)
            {
                Max = elapsed;
            }
            Calls++;
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
                long elapsed = Stopwatch.GetTimestamp() - _start;
                _section.Total += elapsed;
                if (elapsed > _section.Max)
                {
                    _section.Max = elapsed;
                }
                _section.Calls++;
            }
        }
    }

    private static readonly Dictionary<string, Section> _byName = new();
    private static readonly List<Section> _sections = new();
    private static long _windowStartTicks;

    // Lightweight event counters — no Begin/End, just incremented at call
    // sites. Use for "how many of X happened in this window" metrics like
    // entity-spawn counts per chunk-load. Surfaced under engine monitors,
    // latched on the same cadence as section totals. Insertion-ordered list
    // gives a stable print order across windows.
    private static readonly Dictionary<string, int> _counters = new();
    private static readonly Dictionary<string, int> _latchedCounters = new();
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
        return new Section.Scope(s, Stopwatch.GetTimestamp());
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
        long elapsed = Stopwatch.GetTimestamp() - start;
        s.Total += elapsed;
        if (elapsed > s.Max)
        {
            s.Max = elapsed;
        }
        s.Calls++;
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
            s.Total = 0;
            s.Max = 0;
            s.Calls = 0;
            s.ActiveStart = 0;
        }
        // Latch + reset bare counters that aren't tied to a Section. Same
        // window cadence as the section table so the per-window numbers
        // line up.
        SharedWalkabilityCache.LatchCounters();
        SharedWalkabilityCache.SweepStale();
        for (int i = 0; i < _counterNames.Count; i++)
        {
            string n = _counterNames[i];
            _latchedCounters[n] = _counters[n];
            _counters[n] = 0;
        }
        // Gauges latch their current (last-set) value but are NOT reset — they
        // represent an instantaneous reading, not a per-window accumulation.
        for (int i = 0; i < _gaugeNames.Count; i++)
        {
            string n = _gaugeNames[i];
            _latchedGauges[n] = _gauges[n];
        }
        _windowGcBaseline0 = System.GC.CollectionCount(0);
        _windowGcBaseline1 = System.GC.CollectionCount(1);
        _windowGcBaseline2 = System.GC.CollectionCount(2);
        _windowStartTicks = now;
    }

    public static void Dump()
    {
        Godot.GD.Print(FormatTable(useLatched: false));
    }

    // Builds the same one-line-per-section table that Dump prints. The
    // overlay calls this every refresh with useLatched=true so on-screen
    // numbers reflect the previous full window rather than a partial one
    // that resets every frame.
    public static string FormatTable(bool useLatched)
    {
        StringBuilder sb = new StringBuilder();
        AppendTable(sb, useLatched);
        return sb.ToString();
    }

    public static void AppendTable(StringBuilder sb, bool useLatched)
    {
        long now = Stopwatch.GetTimestamp();
        double tickToMs = 1000.0 / Stopwatch.Frequency;

        sb.Append("[profile]");
        if (useLatched)
        {
            sb.Append(" (latched window)");
        }
        else
        {
            double elapsedSec = _windowStartTicks == 0L
                ? 0.0
                : (now - _windowStartTicks) / (double)Stopwatch.Frequency;
            sb.Append(' ').Append(elapsedSec.ToString("F2")).Append("s window");
        }
        sb.Append('\n');
        sb.Append("  section                          calls   total_ms   avg_us  max_ms  per_frame_ms\n");

        // Overlay path filters out sections below the cutoff so the table
        // stays scannable. Manual dump / hitch dump path (useLatched=false)
        // always shows everything — those are one-shot debug snapshots where
        // missing rows would be confusing.
        double minPerFrameMs = useLatched ? CVars.profileMinPerFrameMs.Value : 0.0;

        for (int i = 0; i < _sections.Count; i++)
        {
            Section s = _sections[i];
            long total;
            long max;
            int calls;
            double windowSec;
            if (useLatched)
            {
                total = s.LatchedTotal;
                max = s.LatchedMax;
                calls = s.LatchedCalls;
                windowSec = s.LatchedWindowSec;
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
            double perFrame = windowSec > 0.0 ? totalMs / (windowSec * 60.0) : 0.0;
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
        AppendEngineMonitors(sb, useLatched);
    }

    // Engine monitors that explain frames the C# sections don't account for.
    // FPS / TIME_PROCESS / TIME_PHYSICS_PROCESS show how the frame is split;
    // RENDER_TOTAL_DRAW_CALLS_IN_FRAME / RENDER_TOTAL_OBJECTS_IN_FRAME show
    // whether render submission is the cost (each shadow-casting sprite
    // counts as a separate draw); PHYSICS_3D_ACTIVE_OBJECTS / COLLISION_PAIRS
    // show whether Jolt's broadphase + narrowphase is the cost.
    private static void AppendEngineMonitors(StringBuilder sb, bool useLatched)
    {
        sb.Append("  --- engine monitors (instantaneous) ---\n");
        AppendMonitor(sb, "fps", Godot.Performance.Monitor.TimeFps, "F1");
        AppendMonitor(sb, "process_ms", Godot.Performance.Monitor.TimeProcess, "F2", 1000.0);
        AppendMonitor(sb, "physics_process_ms", Godot.Performance.Monitor.TimePhysicsProcess, "F2", 1000.0);
        AppendMonitor(sb, "render_draw_calls", Godot.Performance.Monitor.RenderTotalDrawCallsInFrame, "F0");
        AppendMonitor(sb, "render_objects", Godot.Performance.Monitor.RenderTotalObjectsInFrame, "F0");
        AppendMonitor(sb, "render_primitives", Godot.Performance.Monitor.RenderTotalPrimitivesInFrame, "F0");
        AppendMonitor(sb, "physics_active_objects", Godot.Performance.Monitor.Physics3DActiveObjects, "F0");
        AppendMonitor(sb, "physics_collision_pairs", Godot.Performance.Monitor.Physics3DCollisionPairs, "F0");
        AppendMonitor(sb, "physics_islands", Godot.Performance.Monitor.Physics3DIslandCount, "F0");
        // Fx live counts. Maintained by Fx._Ready/_ExitTree. A spike here
        // (especially in active_audio / active_particles) at the moment fps
        // tanks is the smoking gun for footstep / loop-effect overspawn.
        sb.Append("  ").Append("fx_active".PadRight(32)).Append(Fx.ActiveCount.ToString().PadLeft(12)).Append('\n');
        sb.Append("  ").Append("fx_active_audio".PadRight(32)).Append(Fx.ActiveAudioCount.ToString().PadLeft(12)).Append('\n');
        sb.Append("  ").Append("fx_active_particles".PadRight(32)).Append(Fx.ActiveParticlesCount.ToString().PadLeft(12)).Append('\n');
        // SharedWalkabilityCache hit/miss/size. A high hit ratio at swarm
        // density is the whole point of the cache; if hits ≪ misses the
        // quantum is too tight or mob profiles vary too much for sharing.
        sb.Append("  ").Append("walkability_cache_hits".PadRight(32)).Append(SharedWalkabilityCache.HitsLatched.ToString().PadLeft(12)).Append('\n');
        sb.Append("  ").Append("walkability_cache_misses".PadRight(32)).Append(SharedWalkabilityCache.MissesLatched.ToString().PadLeft(12)).Append('\n');
        sb.Append("  ").Append("walkability_cache_entries".PadRight(32)).Append(SharedWalkabilityCache.EntryCount.ToString().PadLeft(12)).Append('\n');
        // GC collections in the current window. Gen0 churn that climbs into
        // the dozens-per-window is the smoking gun for per-frame allocations
        // in hot paths; gen2 collections are rare and expensive (correlates
        // strongly with frame hitches when they happen).
        int gcWin0 = System.GC.CollectionCount(0) - _windowGcBaseline0;
        int gcWin1 = System.GC.CollectionCount(1) - _windowGcBaseline1;
        int gcWin2 = System.GC.CollectionCount(2) - _windowGcBaseline2;
        sb.Append("  ").Append("gc_gen0_window".PadRight(32)).Append(gcWin0.ToString().PadLeft(12)).Append('\n');
        sb.Append("  ").Append("gc_gen1_window".PadRight(32)).Append(gcWin1.ToString().PadLeft(12)).Append('\n');
        sb.Append("  ").Append("gc_gen2_window".PadRight(32)).Append(gcWin2.ToString().PadLeft(12)).Append('\n');
        // Event counters. Live (post-Reset window) for the hitch dump; latched
        // for the overlay so the on-screen value doesn't churn every frame.
        for (int i = 0; i < _counterNames.Count; i++)
        {
            string n = _counterNames[i];
            int v = useLatched ? _latchedCounters[n] : _counters[n];
            sb.Append("  ").Append(n.PadRight(32)).Append(v.ToString().PadLeft(12)).Append('\n');
        }
        // Per-frame gauges (instantaneous readings, not per-window sums).
        for (int i = 0; i < _gaugeNames.Count; i++)
        {
            string n = _gaugeNames[i];
            long v = useLatched ? _latchedGauges[n] : _gauges[n];
            sb.Append("  ").Append(n.PadRight(32)).Append(v.ToString().PadLeft(12)).Append('\n');
        }
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
        }
        for (int i = 0; i < _counterNames.Count; i++)
        {
            _counters[_counterNames[i]] = 0;
        }
        _windowGcBaseline0 = System.GC.CollectionCount(0);
        _windowGcBaseline1 = System.GC.CollectionCount(1);
        _windowGcBaseline2 = System.GC.CollectionCount(2);
        _windowStartTicks = Stopwatch.GetTimestamp();
    }

    public static void DumpAndReset()
    {
        Dump();
        Reset();
    }
}
