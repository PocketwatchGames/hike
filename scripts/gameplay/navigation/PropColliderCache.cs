using System.Collections.Generic;
using Godot;

// One solid collider from a prop scene: the shape, and where it sits relative
// to the scene's root Node3D.
public struct PropCollider
{
    public Shape3D Shape;
    public Transform3D Local;
}

// Lazy per-scene cache of solid-collider footprints, mirroring
// FoliageOccluderCache exactly: instantiate each scene once (NOT added to the
// SceneTree, so no _Ready fires), walk it for the CollisionShape3Ds that sit
// under a Solid-layer body, snapshot their composed local transforms, free the
// temporary.
//
// Why the snapshot rather than PathBlockerRasterizer's live walk: a world being
// BAKED has entities as data, not nodes, so there is no GlobalTransform to read
// and no tree to read it from. A collider's placement within its scene is a
// property of the scene, so it is resolved once per PackedScene and then placed
// per entity.
//
// Instantiate is a Node API call, so like FoliageOccluderCache this is
// MAIN-THREAD ONLY — which is why the stamper that uses it runs in the
// producers' main-thread epilogue rather than inside WorldFinish.
public static class PropColliderCache
{
    private static readonly Dictionary<string, PropCollider[]> _byScenePath = new();

    public static PropCollider[] GetColliders(PackedScene scene)
    {
        if (scene == null)
        {
            return System.Array.Empty<PropCollider>();
        }
        string key = scene.ResourcePath;
        if (string.IsNullOrEmpty(key))
        {
            return Collect(scene);
        }
        if (_byScenePath.TryGetValue(key, out PropCollider[] cached))
        {
            return cached;
        }
        PropCollider[] colliders = Collect(scene);
        _byScenePath[key] = colliders;
        return colliders;
    }

    private static PropCollider[] Collect(PackedScene scene)
    {
        Node root = scene.Instantiate();
        if (root == null)
        {
            return System.Array.Empty<PropCollider>();
        }
        var list = new List<PropCollider>();
        try
        {
            // A scene whose colliders are generated rather than authored builds
            // them in _Ready, which never fires here. Ask for them explicitly or
            // the chest, the signpost and the stone read as having no footprint
            // at all.
            foreach (Node node in root.FindChildren("*", "MeshAutoCollider", true, false))
            {
                ((MeshAutoCollider)node).EnsureRuntimeColliders();
            }
            if (root is MeshAutoCollider rootCollider)
            {
                rootCollider.EnsureRuntimeColliders();
            }
            Walk(root, Transform3D.Identity, false, list);
        }
        finally
        {
            root.Free();
        }
        return list.ToArray();
    }

    // `inSolidBody` carries down from the nearest enclosing Solid-layer
    // CollisionObject3D, matching PathBlockerRasterizer's rule: only shapes
    // belonging to a body on Environment or Porous block anything. A hurtbox or
    // an interact area hangs off the same prop and must not count.
    private static void Walk(Node node, Transform3D parentXform, bool inSolidBody,
        List<PropCollider> output)
    {
        Transform3D xform = parentXform;
        if (node is Node3D n3)
        {
            xform = parentXform * n3.Transform;
        }
        if (node is CollisionObject3D body)
        {
            inSolidBody = (body.CollisionLayer & (uint)ECollisionLayer.Solid) != 0;
        }
        if (inSolidBody && node is CollisionShape3D cs && cs.Shape != null && !cs.Disabled)
        {
            output.Add(new PropCollider { Shape = cs.Shape, Local = xform });
        }
        int childCount = node.GetChildCount();
        for (int i = 0; i < childCount; i++)
        {
            Walk(node.GetChild(i), xform, inSolidBody, output);
        }
    }
}
