using Godot;

// asset_src/ at the project root holds files that exist only to be BAKED into
// something the game loads: atlas source maps, animation FBXs. It carries a
// .gdignore, so Godot never imports or exports anything in it and nothing can
// reference it by res:// path or uid. A bake tool names a source file by a path
// relative to that folder and resolves it here.
public static class AssetSource
{
    public const string DIR_NAME = "asset_src";

    public static string GlobalPath(string relativePath)
    {
        string root = ProjectSettings.GlobalizePath("res://").PathJoin(DIR_NAME);
        return string.IsNullOrEmpty(relativePath) ? root : root.PathJoin(relativePath);
    }

    // What an inspector file dialog hands back is absolute; what a .tres stores
    // is relative, so the document is the same on every machine. A path outside
    // asset_src is kept as given and reported — the bake then fails naming it.
    public static string Relativize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }
        string normalized = path.Replace('\\', '/');
        string root = GlobalPath("").Replace('\\', '/').TrimEnd('/') + "/";
        if (normalized.StartsWith(root, System.StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Substring(root.Length);
        }
        if (System.IO.Path.IsPathRooted(normalized) || normalized.StartsWith("res://"))
        {
            GD.PushError($"AssetSource: '{path}' is not inside {DIR_NAME}/; bake sources must live there.");
        }
        return normalized;
    }
}
