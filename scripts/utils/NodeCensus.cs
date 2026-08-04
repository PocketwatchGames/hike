using System.Collections.Generic;
using System.Text;
using Godot;

// One-shot scene-tree census. Answers "what are the 28k resident nodes, and
// which of them does the engine actually pay for every frame?" — a question the
// C# Profiler structurally cannot answer, because its sections measure inside
// callbacks and never the engine-side dispatch/cull that walks the tree.
//
// The three cost columns are the point. Raw node count is nearly free on its
// own; what costs is:
//   proc / phys — nodes in SceneTree's process lists. Every one is an engine →
//                 C#-binding call per frame even when the callback body is empty.
//   vis         — VisualInstance3D. Culled every frame per camera, whether or
//                 not it draws (and this project runs several SubViewport
//                 cameras, so each one is culled multiple times per frame).
//   col         — CollisionObject3D. A Jolt broadphase entry.
// A bucket with a big `total` and zeroes across those columns is not the
// problem; a small bucket with a big `proc` is.
public static class NodeCensus
{
    // Rows below this share of the table are folded into a trailing "(others)"
    // line so the report stays readable in the console.
    private const int MaxRowsPerTable = 24;

    private class Bucket
    {
        public string Name;
        public int Instances;
        public int Total;
        public int Processing;
        public int PhysicsProcessing;
        public int InternalProcessing;
        public int Visuals;
        public int VisualsVisible;
        public int Colliders;
    }

    public static void Run()
    {
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        Node root = tree?.Root;
        if (root == null)
        {
            GD.Print("node_census: no scene tree.");
            return;
        }

        var byClass = new Dictionary<string, Bucket>();
        var byScene = new Dictionary<string, Bucket>();
        var bySubtree = new Dictionary<string, Bucket>();
        var totals = new Bucket { Name = "ALL" };

        Walk(root, "(root)", "(code-created)", 0, byClass, byScene, bySubtree, totals);

        var sb = new StringBuilder();
        sb.Append("\n=== node census ===\n");
        sb.Append($"  nodes {totals.Total}   processing {totals.Processing}   physics {totals.PhysicsProcessing}   internal {totals.InternalProcessing}   visual_instances {totals.Visuals} (visible {totals.VisualsVisible})   colliders {totals.Colliders}\n");
        // The same monitors the F3 overlay shows, so a census can be checked
        // against them directly. node_count counts every live Node, including
        // any held outside the tree, so it reads slightly above the walk above;
        // render_objects is what actually survived culling this frame.
        sb.Append($"  [engine] node_count {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):F0}")
          .Append($"   object_count {Performance.GetMonitor(Performance.Monitor.ObjectCount):F0}")
          .Append($"   orphan_nodes {Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount):F0}")
          .Append($"   render_objects {Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame):F0}\n");
        AppendTable(sb, "by subtree (depth 2 under root)", bySubtree);
        AppendTable(sb, "by source scene", byScene);
        AppendTable(sb, "by class", byClass);
        // Ranked by per-frame work rather than size. This is the table to read
        // when chasing unaccounted_ms_avg: a class with 40 nodes and 40 internal
        // ticks costs more every frame than one with 4000 inert ones, and sorting
        // by total buries it in (others).
        AppendTable(sb, "by class, ranked by per-frame ticks (proc+phys+intl)", byClass, byTicks: true);
        GD.Print(sb.ToString());
    }

    // depth counts from the SceneTree root; the subtree bucket freezes at depth
    // 2 so every node lands under a stable "/root/Main/Game"-style heading
    // instead of its own leaf path.
    private static void Walk(Node node, string subtreeKey, string sceneKey, int depth,
        Dictionary<string, Bucket> byClass, Dictionary<string, Bucket> byScene,
        Dictionary<string, Bucket> bySubtree, Bucket totals)
    {
        // A node's SceneFilePath is set only on the root of an instanced scene,
        // so it propagates down to describe everything that scene brought with
        // it. Nodes built in code inherit whatever scene contains them, which is
        // the attribution we want (a code-spawned MeshInstance3D under a mob
        // should read as that mob).
        string ownScene = node.SceneFilePath;
        bool isSceneRoot = !string.IsNullOrEmpty(ownScene);
        if (isSceneRoot)
        {
            sceneKey = ownScene;
        }

        string classKey = node.GetClass();
        // C# script types all report their engine base class from GetClass(),
        // which would collapse every gameplay node into "Node3D"/"RigidBody3D".
        // Prefer the script's own type name.
        string scriptName = node.GetType().Name;
        if (scriptName != classKey)
        {
            classKey = $"{scriptName} ({classKey})";
        }

        Tally(byClass, classKey, node, isSceneRoot, totals: null);
        Tally(byScene, sceneKey, node, isSceneRoot, totals: null);
        Tally(bySubtree, subtreeKey, node, isSceneRoot, totals);

        int childCount = node.GetChildCount();
        for (int i = 0; i < childCount; i++)
        {
            Node child = node.GetChild(i);
            string childSubtree = depth < 2 ? $"{subtreeKey}/{child.Name}" : subtreeKey;
            Walk(child, childSubtree, sceneKey, depth + 1, byClass, byScene, bySubtree, totals);
        }
    }

    private static void Tally(Dictionary<string, Bucket> map, string key, Node node, bool isSceneRoot, Bucket totals)
    {
        if (!map.TryGetValue(key, out Bucket b))
        {
            b = new Bucket { Name = key };
            map[key] = b;
        }

        b.Total++;
        if (isSceneRoot)
        {
            b.Instances++;
        }
        if (node.IsProcessing())
        {
            b.Processing++;
        }
        if (node.IsPhysicsProcessing())
        {
            b.PhysicsProcessing++;
        }
        // INTERNAL processing is a separate channel that IsProcessing() does not
        // report, and it is where the engine's own per-frame node work lives —
        // AnimationPlayer advancing tracks, BoneAttachment3D copying a bone pose,
        // AudioStreamPlayer3D repanning, particles. It lands in process_ms with
        // no C# section wrapping it, so it is invisible to the Profiler AND was
        // invisible here until this column existed. Read `intl` before
        // concluding a bucket is free.
        if (node.IsProcessingInternal() || node.IsPhysicsProcessingInternal())
        {
            b.InternalProcessing++;
        }
        if (node is VisualInstance3D vi)
        {
            b.Visuals++;
            if (vi.IsVisibleInTree())
            {
                b.VisualsVisible++;
            }
        }
        if (node is CollisionObject3D)
        {
            b.Colliders++;
        }

        if (totals == null)
        {
            return;
        }
        totals.Total++;
        if (node.IsProcessing())
        {
            totals.Processing++;
        }
        if (node.IsPhysicsProcessing())
        {
            totals.PhysicsProcessing++;
        }
        if (node.IsProcessingInternal() || node.IsPhysicsProcessingInternal())
        {
            totals.InternalProcessing++;
        }
        if (node is VisualInstance3D vi2)
        {
            totals.Visuals++;
            if (vi2.IsVisibleInTree())
            {
                totals.VisualsVisible++;
            }
        }
        if (node is CollisionObject3D)
        {
            totals.Colliders++;
        }
    }

    private static int Ticks(Bucket b)
    {
        return b.Processing + b.PhysicsProcessing + b.InternalProcessing;
    }

    private static void AppendTable(StringBuilder sb, string title, Dictionary<string, Bucket> map, bool byTicks = false)
    {
        var rows = new List<Bucket>(map.Values);
        if (byTicks)
        {
            rows.Sort((a, b) => Ticks(b).CompareTo(Ticks(a)));
            rows.RemoveAll(r => Ticks(r) == 0);
        }
        else
        {
            rows.Sort((a, b) => b.Total.CompareTo(a.Total));
        }

        sb.Append($"  --- {title} ---\n");
        sb.Append("  ").Append("bucket".PadRight(56))
          .Append("total".PadLeft(8)).Append("inst".PadLeft(7)).Append("per".PadLeft(6))
          .Append("proc".PadLeft(7)).Append("phys".PadLeft(7)).Append("intl".PadLeft(7))
          .Append("vis".PadLeft(7)).Append("vis_on".PadLeft(8)).Append("col".PadLeft(7)).Append('\n');

        var others = new Bucket { Name = "(others)" };
        for (int i = 0; i < rows.Count; i++)
        {
            Bucket r = rows[i];
            if (i >= MaxRowsPerTable)
            {
                others.Instances += r.Instances;
                others.Total += r.Total;
                others.Processing += r.Processing;
                others.PhysicsProcessing += r.PhysicsProcessing;
                others.InternalProcessing += r.InternalProcessing;
                others.Visuals += r.Visuals;
                others.VisualsVisible += r.VisualsVisible;
                others.Colliders += r.Colliders;
                continue;
            }
            AppendRow(sb, r);
        }
        if (others.Total > 0)
        {
            AppendRow(sb, others);
        }
    }

    private static void AppendRow(StringBuilder sb, Bucket r)
    {
        string name = r.Name.Length > 55 ? "…" + r.Name.Substring(r.Name.Length - 54) : r.Name;
        // "per" = nodes each instance of this scene brings with it, the number
        // that matters when deciding what to prune or pool.
        string per = r.Instances > 0 ? (r.Total / (float)r.Instances).ToString("F0") : "-";
        sb.Append("  ").Append(name.PadRight(56))
          .Append(r.Total.ToString().PadLeft(8))
          .Append(r.Instances.ToString().PadLeft(7))
          .Append(per.PadLeft(6))
          .Append(r.Processing.ToString().PadLeft(7))
          .Append(r.PhysicsProcessing.ToString().PadLeft(7))
          .Append(r.InternalProcessing.ToString().PadLeft(7))
          .Append(r.Visuals.ToString().PadLeft(7))
          .Append(r.VisualsVisible.ToString().PadLeft(8))
          .Append(r.Colliders.ToString().PadLeft(7))
          .Append('\n');
    }
}
