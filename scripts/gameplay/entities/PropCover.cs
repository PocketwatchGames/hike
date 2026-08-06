using System.Collections.Generic;
using Godot;

// Declares that a prop is a CEILING — an arch, a platform, a bridge deck, an
// awning — so the world records the cover it provides instead of only its
// collider.
//
// Without this a prop that is plainly something you stand under is invisible to
// every system that reads cover off the world rather than off physics: the
// cutaway's probes, the sunlight pass, fog. Only a raycast could see it, and a
// raycast cannot tell a ceiling from a tree branch. Declaring it once here fixes
// all of them at the same time, and gets the shadow underneath for free.
//
// Authored exactly like FoliageCluster: drop the node into the prop scene at the
// height of the underside, size the rectangle to the part you can walk beneath.
// One flat sheet, not a volume — the sunlight walk stops at the first thing that
// blocks the sky, so the sheet is enough and a filled volume would cost many
// times the voxels for an identical result. Same reason RoofSunStamper stamps
// only a roof's base.
//
// Do NOT put one of these on a tree. Foliage is deliberately excluded from the
// cutaway, and canopy shelter is what FoliageCluster.castsSunShadow is for.
[Tool]
[GlobalClass]
public partial class PropCover : Node3D
{
    // Half-extents of the covered rectangle in the node's own XZ, metres. The
    // node's Y is the sheet's elevation — put it at the UNDERSIDE, which is the
    // face you see from below and the height the space beneath should cut at.
    [Export] public Vector2 halfExtents = new Vector2(1.5f, 1.5f);
}

// One cover sheet baked out of a prop scene: the rectangle and its height,
// relative to the prop's root.
public struct PropCoverPatch
{
    public Vector3 CenterLocal;
    public float HalfX;
    public float HalfZ;
}

// Lazy per-scene cache of PropCoverPatch lists, mirroring FoliageOccluderCache:
// instantiates each prop scene exactly once (never added to the SceneTree, so no
// _Ready fires), walks it for PropCover nodes, snapshots their composed local
// transforms, and frees the temporary.
public static class PropCoverCache
{
    private static readonly Dictionary<string, PropCoverPatch[]> _byScenePath = new();

    public static PropCoverPatch[] GetPatches(PackedScene scene)
    {
        if (scene == null)
        {
            return System.Array.Empty<PropCoverPatch>();
        }
        string key = scene.ResourcePath;
        if (string.IsNullOrEmpty(key))
        {
            return Collect(scene);
        }
        if (_byScenePath.TryGetValue(key, out PropCoverPatch[] cached))
        {
            return cached;
        }
        PropCoverPatch[] patches = Collect(scene);
        _byScenePath[key] = patches;
        return patches;
    }

    private static PropCoverPatch[] Collect(PackedScene scene)
    {
        Node root = scene.Instantiate();
        if (root == null)
        {
            return System.Array.Empty<PropCoverPatch>();
        }
        var list = new List<PropCoverPatch>();
        try
        {
            Walk(root, Transform3D.Identity, list);
        }
        finally
        {
            root.Free();
        }
        return list.ToArray();
    }

    private static void Walk(Node node, Transform3D parentXform, List<PropCoverPatch> output)
    {
        Transform3D xform = parentXform;
        if (node is Node3D n3)
        {
            xform = parentXform * n3.Transform;
        }
        if (node is PropCover cover)
        {
            // Scale rides the composed transform, so a prop scaled in its scene
            // covers the area it visually covers.
            Vector3 scale = xform.Basis.Scale;
            output.Add(new PropCoverPatch
            {
                CenterLocal = xform.Origin,
                HalfX = Mathf.Abs(cover.halfExtents.X * scale.X),
                HalfZ = Mathf.Abs(cover.halfExtents.Y * scale.Z),
            });
        }
        foreach (Node child in node.GetChildren())
        {
            Walk(child, xform, output);
        }
    }
}
