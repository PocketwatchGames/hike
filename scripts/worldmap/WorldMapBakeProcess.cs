using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Godot;

// A painter bake runs as its OWN headless Godot process, so it survives the
// painter and the game window closing, and a re-save kills it and starts over.
//
// The only channel between the two is a status file beside the .hike
// (`<world>.hike.bake`): the parent writes it on launch, the child rewrites it
// as it goes, and whoever comes next reads it to find a bake still running. It
// is on disk rather than in the painter because the painter that started a bake
// may be gone by the time the next one is asked for. The child's console output
// goes to `<world>.hike.bake.log` — it has no console, and a pipe back to a
// parent that may exit would break under it.
//
// Killing is safe at any point because nothing reaches the .hike until the very
// end, and WorldFile.Write lands it with a rename.
public static class WorldMapBakeProcess
{
    public const string STATUS_SUFFIX = ".bake";
    public const string LOG_SUFFIX = ".bake.log";

    public enum EState
    {
        Running,
        Succeeded,
        Failed,
    }

    public struct Status
    {
        public int Pid;
        // Process.StartTime in UTC ticks. A pid alone can be reused by an
        // unrelated process after the bake exits, and killing THAT is the one
        // outcome this file must never cause.
        public long StartTicks;
        public EState State;
        public float Ratio;
        public string Phase;
        public long ElapsedMs;
    }

    public static string StatusPath(string hikePath)
    {
        return ProjectSettings.GlobalizePath(hikePath) + STATUS_SUFFIX;
    }

    public static string LogPath(string hikePath)
    {
        return ProjectSettings.GlobalizePath(hikePath) + LOG_SUFFIX;
    }

    // Kill whatever bake is running for this document's world, then launch a
    // fresh one over what is on disk now. The caller has already saved.
    public static bool Restart(WorldMapData data)
    {
        string hike = data.OutputWorldPath;
        if (string.IsNullOrEmpty(hike))
        {
            GD.PrintErr($"WorldMapBakeProcess: {data.NoWorldMessage()}");
            return false;
        }
        Kill(hike);

        var info = new ProcessStartInfo
        {
            FileName = OS.GetExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = ProjectSettings.GlobalizePath("res://"),
        };
        info.ArgumentList.Add("--path");
        info.ArgumentList.Add(ProjectSettings.GlobalizePath("res://"));
        info.ArgumentList.Add("--headless");
        info.ArgumentList.Add("--log-file");
        info.ArgumentList.Add(LogPath(hike));
        info.ArgumentList.Add("--");
        info.ArgumentList.Add($"worldmap_bake {data.ResourcePath}");
        // worldmap_bake does not quit on its own; a bake that forgot to would
        // sit there holding nothing but a process slot, forever, unseen.
        info.ArgumentList.Add("quit");

        Process child;
        try
        {
            child = Process.Start(info);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"WorldMapBakeProcess: could not launch the bake: {e.Message}");
            return false;
        }
        if (child == null)
        {
            GD.PrintErr("WorldMapBakeProcess: could not launch the bake.");
            return false;
        }
        // Written by the parent before the child is far enough along to write
        // its own, so a second save in quick succession can already find it.
        Write(hike, new Status
        {
            Pid = child.Id,
            StartTicks = child.StartTime.ToUniversalTime().Ticks,
            State = EState.Running,
            Ratio = 0f,
            Phase = "Starting",
        });
        child.Dispose();
        return true;
    }

    // The status of the last bake of this world, or null if none was ever
    // started. A Running bake whose process is gone (crashed, or killed from
    // outside) reads as Failed — it is never coming back to say so itself.
    public static Status? Read(string hikePath)
    {
        string path = StatusPath(hikePath);
        Dictionary<string, string> fields;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            fields = new Dictionary<string, string>();
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    fields[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        var s = new Status
        {
            Pid = ParseInt(fields, "pid"),
            StartTicks = ParseLong(fields, "start"),
            State = fields.TryGetValue("state", out string st) && System.Enum.TryParse(st, out EState e) ? e : EState.Failed,
            Ratio = fields.TryGetValue("ratio", out string r)
                && float.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out float rv) ? rv : 0f,
            Phase = fields.TryGetValue("phase", out string ph) ? ph : "",
            ElapsedMs = ParseLong(fields, "elapsed"),
        };
        if (s.State == EState.Running && FindProcess(s) == null)
        {
            s.State = EState.Failed;
        }
        return s;
    }

    // Called by the CHILD, as it goes. The write is tmp-then-rename so the
    // painter polling it never reads half a file.
    public static void Write(string hikePath, Status s)
    {
        string path = StatusPath(hikePath);
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, string.Join("\n",
                "pid=" + s.Pid.ToString(CultureInfo.InvariantCulture),
                "start=" + s.StartTicks.ToString(CultureInfo.InvariantCulture),
                "state=" + s.State,
                "ratio=" + s.Ratio.ToString(CultureInfo.InvariantCulture),
                "phase=" + (s.Phase ?? "").Replace('\n', ' '),
                "elapsed=" + s.ElapsedMs.ToString(CultureInfo.InvariantCulture)));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException e)
        {
            GD.PrintErr($"WorldMapBakeProcess: could not write bake status '{path}': {e.Message}");
        }
    }

    // A status writer for the bake running in THIS process, throttled — the
    // bake reports progress per strip, far more often than anyone polls.
    public static System.Action<float, string> Reporter(string hikePath, Stopwatch clock)
    {
        using Process self = Process.GetCurrentProcess();
        var status = new Status
        {
            Pid = self.Id,
            StartTicks = self.StartTime.ToUniversalTime().Ticks,
            State = EState.Running,
        };
        long lastMs = long.MinValue;
        return (ratio, phase) =>
        {
            long now = clock.ElapsedMilliseconds;
            if (phase == status.Phase && now - lastMs < REPORT_INTERVAL_MS)
            {
                return;
            }
            lastMs = now;
            status.Ratio = ratio;
            status.Phase = phase;
            status.ElapsedMs = now;
            Write(hikePath, status);
        };
    }

    // Called by the CHILD when it is done, either way.
    public static void Finish(string hikePath, bool ok, long elapsedMs)
    {
        using Process self = Process.GetCurrentProcess();
        Write(hikePath, new Status
        {
            Pid = self.Id,
            StartTicks = self.StartTime.ToUniversalTime().Ticks,
            State = ok ? EState.Succeeded : EState.Failed,
            Ratio = 1f,
            Phase = ok ? "Done" : "Failed",
            ElapsedMs = elapsedMs,
        });
    }

    private static void Kill(string hikePath)
    {
        Status? s = Read(hikePath);
        if (s == null || s.Value.State != EState.Running)
        {
            return;
        }
        Process p = FindProcess(s.Value);
        if (p == null)
        {
            return;
        }
        using (p)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                // Wait for it to be gone, so it cannot write its status (or a
                // .hike.tmp) over the bake that replaces it.
                p.WaitForExit(KILL_WAIT_MS);
                GD.Print($"WorldMapBakeProcess: stopped the running bake (pid {s.Value.Pid}) to restart it");
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"WorldMapBakeProcess: could not stop bake pid {s.Value.Pid}: {e.Message}");
            }
        }
    }

    // The bake's process if it is still the one the status names, else null.
    private static Process FindProcess(Status s)
    {
        if (s.Pid <= 0)
        {
            return null;
        }
        Process p;
        try
        {
            p = Process.GetProcessById(s.Pid);
        }
        catch (System.ArgumentException)
        {
            return null;
        }
        try
        {
            if (!p.HasExited && p.StartTime.ToUniversalTime().Ticks == s.StartTicks)
            {
                return p;
            }
        }
        catch (System.Exception)
        {
            // Access denied or exited between the two calls: not ours either way.
        }
        p.Dispose();
        return null;
    }

    private static int ParseInt(Dictionary<string, string> f, string key)
    {
        return f.TryGetValue(key, out string v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : 0;
    }

    private static long ParseLong(Dictionary<string, string> f, string key)
    {
        return f.TryGetValue(key, out string v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long r) ? r : 0;
    }

    private const long REPORT_INTERVAL_MS = 200;
    private const int KILL_WAIT_MS = 5000;
}
