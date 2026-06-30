using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Godot;

// Disk cache for WorldGen.Generate output. The fingerprint covers
// WORLDGEN_VERSION + WorldFile.VERSION + the content of every reachable
// .tres / .tscn / .hikescene from the input WorldGenData. Cache files are
// per-(seed, size, fingerprint) so multiple seeds coexist and any data /
// version change forces regeneration. Cache load failures fall back to a
// fresh Generate, so stale or corrupted files never block startup.
public static class WorldGenCache
{
    private const string CACHE_DIR = "user://worldgen_cache";

    // Combine seed, size, and an authored-data fingerprint into the cache
    // file path. Format / WorldGen logic version is rolled into the
    // fingerprint, not the filename, so a version bump leaves old files
    // orphaned in the cache dir (cleaned by world_cache_clear).
    public static string GetCachePath(int seed, Vector3I size, string fingerprint)
    {
        return $"{CACHE_DIR}/world_seed{seed}_size{size.X}x{size.Y}x{size.Z}_{fingerprint}.hike";
    }

    public static bool Exists(string resPath)
    {
        return File.Exists(ProjectSettings.GlobalizePath(resPath));
    }

    public static void EnsureDir()
    {
        string osDir = ProjectSettings.GlobalizePath(CACHE_DIR);
        if (!Directory.Exists(osDir))
        {
            Directory.CreateDirectory(osDir);
        }
    }

    public static void Clear()
    {
        string osDir = ProjectSettings.GlobalizePath(CACHE_DIR);
        if (!Directory.Exists(osDir))
        {
            GD.Print($"[WorldGenCache] Clear: nothing to delete ({osDir})");
            return;
        }
        try
        {
            Directory.Delete(osDir, recursive: true);
            GD.Print($"[WorldGenCache] Cleared {osDir}");
        }
        catch (Exception e)
        {
            GD.PrintErr($"[WorldGenCache] Clear failed: {e.Message}");
        }
    }

    // Stable 16-char hex fingerprint of the inputs that affect generated
    // output: WORLDGEN_VERSION + WorldFile.VERSION + the byte content of
    // every transitively-reachable .tres/.tscn dependency from genData
    // plus any .hikescene paths it references via SubscenePlacement[].
    // Resources whose extension is none of those (textures, audio, shaders,
    // etc.) are walked for further deps but not hashed — their content
    // doesn't affect worldgen output.
    public static string ComputeFingerprint(WorldGenData genData)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write(WorldFile.VERSION);
        w.Write(WorldGen.WORLDGEN_VERSION);

        WalkAndHashDeps(genData, w);

        w.Flush();
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(ms.ToArray());
        return Convert.ToHexString(hash, 0, 8);
    }

    private static void WalkAndHashDeps(WorldGenData genData, BinaryWriter w)
    {
        if (genData == null || string.IsNullOrEmpty(genData.ResourcePath))
        {
            return;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(genData.ResourcePath);
        visited.Add(genData.ResourcePath);

        // SubscenePlacement.Path strings reference .hikescene files, which are
        // not Godot resources — ResourceLoader.GetDependencies won't surface
        // them. Pull them in explicitly.
        if (genData.subscenes != null)
        {
            foreach (SubscenePlacement sp in genData.subscenes)
            {
                if (sp == null || string.IsNullOrEmpty(sp.path))
                {
                    continue;
                }
                if (visited.Add(sp.path))
                {
                    queue.Enqueue(sp.path);
                }
            }
        }

        var ordered = new List<string>();
        while (queue.Count > 0)
        {
            string path = queue.Dequeue();
            ordered.Add(path);

            // Only Godot resources have queryable deps. .hikescene files are
            // custom binary — their content gets hashed below, but we don't
            // walk into them.
            if (!IsGodotResource(path))
            {
                continue;
            }

            string[] deps;
            try
            {
                deps = ResourceLoader.GetDependencies(path);
            }
            catch
            {
                continue;
            }
            if (deps == null)
            {
                continue;
            }

            foreach (string dep in deps)
            {
                string depPath = ExtractDepPath(dep);
                if (string.IsNullOrEmpty(depPath))
                {
                    continue;
                }
                if (!ShouldHashExt(depPath))
                {
                    continue;
                }
                if (visited.Add(depPath))
                {
                    queue.Enqueue(depPath);
                }
            }
        }

        // Deterministic order so hash is stable across BFS traversal order.
        ordered.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string path in ordered)
        {
            w.Write(path);
            byte[] bytes = ReadFileBytes(path);
            if (bytes == null)
            {
                w.Write(-1);
            }
            else
            {
                w.Write(bytes.Length);
                w.Write(bytes);
            }
        }
    }

    private static bool IsGodotResource(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".tres" || ext == ".tscn" || ext == ".res";
    }

    private static bool ShouldHashExt(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".tres" || ext == ".tscn" || ext == ".res" || ext == ".hikescene";
    }

    // Godot's GetDependencies entries can be either bare paths ("res://foo.tres")
    // or "uid::type::path::fallback" tuples. The last "::"-separated segment
    // before any "::" trailer is the path; falling back to "after last ::"
    // covers both common forms.
    private static string ExtractDepPath(string dep)
    {
        if (string.IsNullOrEmpty(dep))
        {
            return null;
        }
        int idx = dep.LastIndexOf("::", StringComparison.Ordinal);
        if (idx < 0)
        {
            return dep;
        }
        string tail = dep.Substring(idx + 2);
        // Some Godot versions append a "::fallback" segment; if `tail` itself
        // looks like a path, use it, otherwise re-split.
        if (tail.StartsWith("res://") || tail.StartsWith("uid://") || tail.Contains('/'))
        {
            return tail;
        }
        // tail is something other than a path — look one segment earlier.
        string head = dep.Substring(0, idx);
        int prevIdx = head.LastIndexOf("::", StringComparison.Ordinal);
        return prevIdx < 0 ? head : head.Substring(prevIdx + 2);
    }

    private static byte[] ReadFileBytes(string resPath)
    {
        string osPath = ProjectSettings.GlobalizePath(resPath);
        if (!File.Exists(osPath))
        {
            return null;
        }
        try
        {
            return File.ReadAllBytes(osPath);
        }
        catch
        {
            return null;
        }
    }
}
