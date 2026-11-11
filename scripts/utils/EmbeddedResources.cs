using System.Collections.Generic;
using Godot;

// Godot names an embedded resource by its CONTAINER: a fork loaded out of
// house01.entities.tres has the path `house01.entities.tres::Resource_ab12`.
// Save that resource into a different file and it is written as a reference
// back into house01's file rather than as an embedded copy — so saving a scene
// under a new name, or copying entities out of another scene, would leave the
// new file pointing into the old one.
public static class EmbeddedResources
{
    // Deep enough for an entry fork → list → record → record; anything deeper
    // is a type that contains itself.
    private const int MAX_DEPTH = 8;

    // Clears the path of every resource under `root` that is embedded in some
    // file OTHER than `targetFile`, so it embeds in the file being written.
    // Resources that are files of their own (a palette entry, a texture) are
    // references and are left alone, along with everything under them.
    public static void DetachFromOtherFiles(Resource root, string targetFile)
    {
        Walk(root, targetFile, new HashSet<ulong>(), 0);
    }

    private static void Walk(Resource r, string targetFile, HashSet<ulong> seen, int depth)
    {
        if (r == null || depth > MAX_DEPTH || !seen.Add(r.GetInstanceId()))
        {
            return;
        }
        string path = r.ResourcePath ?? "";
        int separator = path.IndexOf("::", System.StringComparison.Ordinal);
        if (path.Length > 0 && separator < 0)
        {
            return;
        }
        if (separator >= 0 && path[..separator] != targetFile)
        {
            r.ResourcePath = "";
        }
        foreach (Godot.Collections.Dictionary property in r.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)(long)property["usage"];
            if ((usage & PropertyUsageFlags.Storage) == 0)
            {
                continue;
            }
            Variant value = r.Get(property["name"].AsStringName());
            if (value.VariantType == Variant.Type.Object)
            {
                Walk(value.AsGodotObject() as Resource, targetFile, seen, depth + 1);
            }
            else if (value.VariantType == Variant.Type.Array)
            {
                foreach (Variant element in value.AsGodotArray())
                {
                    if (element.VariantType == Variant.Type.Object)
                    {
                        Walk(element.AsGodotObject() as Resource, targetFile, seen, depth + 1);
                    }
                }
            }
        }
    }
}
