using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Godot;

// Load-time instrumentation for the entity spawn drain and the chunk-mesh
// fill: per-entity-type cost split into CreateEntity (scene instantiation) vs
// RegisterEntity (OnSpawned + bookkeeping). Off unless `spawn_cost_profile 1`
// is set before the world loads — the timestamps are per entity, and the dump
// is a screenful.
public static class SpawnCostProfile
{
    private struct Entry
    {
        public int Count;
        public double CreateMs;
        public double RegisterMs;
    }

    private static readonly Dictionary<string, Entry> _byType = new();

    public static bool Enabled => CVars.spawnCostProfile.Value;

    // Timestamp for a Record/RecordOther pair. Returns 0 while profiling is
    // off so a call site costs nothing but the flag read.
    public static long Stamp()
    {
        return Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    public static void Record(string type, long t0, long t1, long t2)
    {
        if (!Enabled)
        {
            return;
        }
        double toMs = 1000.0 / Stopwatch.Frequency;
        _byType.TryGetValue(type, out Entry e);
        e.Count++;
        e.CreateMs += (t1 - t0) * toMs;
        e.RegisterMs += (t2 - t1) * toMs;
        _byType[type] = e;
    }

    public static void RecordOther(string label, long t0)
    {
        if (!Enabled)
        {
            return;
        }
        double ms = (Stopwatch.GetTimestamp() - t0) * (1000.0 / Stopwatch.Frequency);
        _byType.TryGetValue(label, out Entry e);
        e.Count++;
        e.RegisterMs += ms;
        _byType[label] = e;
    }

    public static void Dump()
    {
        if (!Enabled)
        {
            return;
        }
        var rows = new List<KeyValuePair<string, Entry>>(_byType);
        rows.Sort((a, b) => (b.Value.CreateMs + b.Value.RegisterMs).CompareTo(a.Value.CreateMs + a.Value.RegisterMs));
        var sb = new StringBuilder();
        sb.AppendLine("[SpawnCost] type                     count   create_ms  register_ms   total_ms   per_entity_ms");
        double tc = 0, tr = 0;
        int tn = 0;
        foreach (KeyValuePair<string, Entry> kv in rows)
        {
            Entry e = kv.Value;
            double total = e.CreateMs + e.RegisterMs;
            tc += e.CreateMs;
            tr += e.RegisterMs;
            tn += e.Count;
            sb.AppendLine($"[SpawnCost] {kv.Key,-24} {e.Count,5} {e.CreateMs,11:F1} {e.RegisterMs,12:F1} {total,10:F1} {total / e.Count,15:F2}");
        }
        sb.AppendLine($"[SpawnCost] {"TOTAL",-24} {tn,5} {tc,11:F1} {tr,12:F1} {tc + tr,10:F1} {(tn > 0 ? (tc + tr) / tn : 0),15:F2}");
        GD.Print(sb.ToString());
        _byType.Clear();
    }
}
