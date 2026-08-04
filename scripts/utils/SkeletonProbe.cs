using Godot;

// Bisection tool for the one per-frame cost the C# Profiler structurally cannot
// reach: Skeleton3D's pose/skin update.
//
// Skeleton3D does that work on Godot's INTERNAL process channel. No
// Profiler.Sample can wrap it (there is no C# frame to wrap), and Node.
// IsProcessing() doesn't even report it — NodeCensus needs IsProcessingInternal()
// to see it at all. So it lands squarely in the profiler's unaccounted_ms_avg,
// and the only way to size it is to turn it off and read the delta in
// process_ms. See CVars.skeletonInternal.
//
// Poses freeze while internal processing is off. That is expected and is the
// point — a frozen pose with no change in process_ms means skeletons weren't the
// cost; a big drop means they were.
public static class SkeletonProbe
{
    public static void SetInternalProcessing(bool enabled)
    {
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        Node root = tree?.Root;
        if (root == null)
        {
            GD.Print("skeleton_internal: no scene tree.");
            return;
        }
        int touched = 0;
        Walk(root, enabled, ref touched);
        GD.Print($"skeleton_internal {(enabled ? 1 : 0)}: {touched} Skeleton3D(s) updated.");
    }

    private static void Walk(Node node, bool enabled, ref int touched)
    {
        if (node is Skeleton3D skeleton)
        {
            skeleton.SetProcessInternal(enabled);
            skeleton.SetPhysicsProcessInternal(enabled);
            touched++;
        }
        int childCount = node.GetChildCount();
        for (int i = 0; i < childCount; i++)
        {
            Walk(node.GetChild(i), enabled, ref touched);
        }
    }
}
