using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

// Generic CPU section profiler.
//
// SHIPPING BUILDS: every public profiling call is tagged
// [Conditional("PROFILE")]. The PROFILE symbol is defined for Debug /
// ExportDebug in hike.csproj and NOT defined for Release / ExportRelease, so
// in shipping builds the C# compiler emits no call site at all — argument
// expressions still evaluate (they're just plain strings here, so it's free).
// The static Section cache is still allocated, but never populated or read.
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
//      First hit on a name allocates a Section and caches it; subsequent hits
//      reuse the cached entry. The lookup is one Dictionary<string, Section>
//      probe per call when profiling is enabled, and a single bool branch when
//      it isn't.
//
//   2. Inline begin/end (when a `using` block would force awkward indentation):
//
//        Profiler.Begin("Mob.PerceptionRays");
//        // ... work ...
//        Profiler.End("Mob.PerceptionRays");
//
//      The string is looked up on both Begin and End. Names must match exactly.
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
// All three accumulate into the same Section by name, so you can switch a
// section between flavors without losing history within a window.
//
// CONSOLE:
//   profile 1            enable sampling + reset accumulators
//   profile 0            disable sampling
//   profile_dump         print a one-line-per-section table and reset
//
// Reported per dump:
//   calls      - how many times the section ran since the last reset
//   total_ms   - wall time spent in the section
//   avg_us     - mean per call
//   max_ms     - worst single call
//   per_frame  - total_ms / window-seconds * 60. Rough ms/frame at 60 Hz.
//
// Main-thread only: sections write into per-section fields without locking.
// Calling from a worker thread will produce wrong totals.
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

        internal Section(string name)
        {
            Name = name;
        }

        // Cached-reference flavor. Begin/End on the Section directly skip the
        // dictionary lookup. ActiveStart is per-Section so reentrant or nested
        // sections still need separate Section instances — same as the
        // by-name flavor.
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

    // Cached-reference factory. Safe to call at static-init time; keeps a
    // section live even when PROFILE is not defined (the compiler can't strip
    // the field initializer based on a Conditional attribute, only the call
    // sites). The Section object itself is cheap.
    public static Section MakeSection(string name)
    {
        return GetOrCreate(name);
    }

    // Inline scope. Use as: `using (Profiler.Sample("Mob.TickAI")) { ... }`.
    // Returns default(Scope) when PROFILE is undefined or profiling is off,
    // whose Dispose is a no-op.
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

    // By-name begin/end. Names must match exactly between Begin and End.
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
        }
        return s;
    }

    public static void Dump()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSec = _windowStartTicks == 0L
            ? 0.0
            : (now - _windowStartTicks) / (double)Stopwatch.Frequency;
        double tickToMs = 1000.0 / Stopwatch.Frequency;

        StringBuilder sb = new StringBuilder();
        sb.Append("[profile] ").Append(elapsedSec.ToString("F2")).Append("s window\n");
        sb.Append("  section                          calls   total_ms   avg_us  max_ms  per_frame_ms\n");
        for (int i = 0; i < _sections.Count; i++)
        {
            Section s = _sections[i];
            int n = s.Calls;
            if (n == 0)
            {
                continue;
            }
            double total = s.Total * tickToMs;
            double avg = (total * 1000.0) / n;
            double max = s.Max * tickToMs;
            double perFrame = elapsedSec > 0.0 ? total / (elapsedSec * 60.0) : 0.0;
            sb.Append("  ")
              .Append(s.Name.PadRight(32))
              .Append(n.ToString().PadLeft(6))
              .Append(' ').Append(total.ToString("F2").PadLeft(10))
              .Append(' ').Append(avg.ToString("F2").PadLeft(8))
              .Append(' ').Append(max.ToString("F3").PadLeft(7))
              .Append(' ').Append(perFrame.ToString("F3").PadLeft(13))
              .Append('\n');
        }
        Godot.GD.Print(sb.ToString());
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
        _windowStartTicks = Stopwatch.GetTimestamp();
    }

    public static void DumpAndReset()
    {
        Dump();
        Reset();
    }
}
