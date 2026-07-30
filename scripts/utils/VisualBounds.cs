using Godot;

// Implemented by a visual whose VisualInstance3D.GetAabb() doesn't describe what
// it actually draws — our sprite shaders build their own quad, so Sprite3D's
// mesh bounds come out one world unit per texel. VisualBounds trusts this
// instead of GetAabb() and stops descending there, so render proxies a node
// spawns for itself (shadow caster, water reflection) can't drag the box out to
// wherever they happen to sit.
public interface IVisualBounds
{
    // Bounds in the node's own local space; null when it draws nothing.
    Aabb? LocalVisualBounds { get; }
}

// World-space bounds of everything a node draws, merged across its whole
// VisualInstance3D subtree. Null when nothing under the node renders — callers
// decide what a mesh-less node should fall back to (a click box, a frame size).
public static class VisualBounds
{
    public static Aabb? Of(Node3D root)
    {
        Aabb? combined = null;
        if (root != null)
        {
            Accumulate(root, ref combined);
        }
        return combined;
    }

    private static void Accumulate(Node node, ref Aabb? combined)
    {
        if (node is IVisualBounds custom && node is Node3D spatial)
        {
            Aabb? local = custom.LocalVisualBounds;
            if (local.HasValue)
            {
                Merge(ref combined, Transform(local.Value, spatial.GlobalTransform));
            }
            return;
        }
        if (node is VisualInstance3D visual)
        {
            Merge(ref combined, Transform(visual.GetAabb(), visual.GlobalTransform));
        }
        foreach (Node child in node.GetChildren())
        {
            Accumulate(child, ref combined);
        }
    }

    private static void Merge(ref Aabb? combined, Aabb world)
    {
        combined = combined.HasValue ? combined.Value.Merge(world) : world;
    }

    // Godot's C# bindings don't expose the Transform3D * Aabb operator, so
    // transform the eight corners and re-fit.
    public static Aabb Transform(Aabb aabb, Transform3D transform)
    {
        Vector3 p = aabb.Position;
        Vector3 s = aabb.Size;
        Vector3 min = transform * p;
        Vector3 max = min;
        for (int corner = 1; corner < 8; corner++)
        {
            Vector3 world = transform * (p + new Vector3(
                (corner & 1) != 0 ? s.X : 0f,
                (corner & 2) != 0 ? s.Y : 0f,
                (corner & 4) != 0 ? s.Z : 0f));
            min = min.Min(world);
            max = max.Max(world);
        }
        return new Aabb(min, max - min);
    }
}
