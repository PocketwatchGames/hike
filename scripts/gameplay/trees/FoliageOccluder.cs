using System.Collections.Generic;
using Godot;

// One ellipsoidal sun-occluding volume — the voxel-space footprint of a
// single FoliageCluster within a prop scene. CenterLocal is the cluster's
// position relative to the prop's root Node3D (composed up through any
// intermediate transforms like a FoliageMultiMesh's offset); Radii is the
// ellipsoid's authored half-extents in the same local frame. CastsSunShadow
// and FadesWhenOccludingPlayer mirror the matching FoliageCluster fields —
// captured here so the per-cluster flags survive caching, letting CPU
// probes (FoliageStamper for canopy attenuation, World.IsFadeVolumeOccluded
// for the player-occlusion cutaway expansion) iterate prop occluders
// without re-walking live scene nodes per frame.
public struct FoliageOccluder
{
    public Vector3 CenterLocal;
    public Vector3 Radii;
    public bool CastsSunShadow;
    public bool FadesWhenOccludingPlayer;
}

// Lazy per-scene cache of FoliageOccluder lists. Instantiates each tree
// scene exactly once (NOT added to the SceneTree, so no _Ready fires),
// walks its hierarchy for FoliageCluster nodes, snapshots their composed
// local transforms + radii, and frees the temporary instance.
public static class FoliageOccluderCache
{
    private static readonly Dictionary<string, FoliageOccluder[]> _byScenePath = new();

    public static FoliageOccluder[] GetOccluders(PackedScene scene)
    {
        if (scene == null)
        {
            return System.Array.Empty<FoliageOccluder>();
        }
        string key = scene.ResourcePath;
        if (string.IsNullOrEmpty(key))
        {
            // No stable key — fall back to recomputing every call. Should
            // not happen in practice since prop scenes are .tscn files.
            return Collect(scene);
        }
        if (_byScenePath.TryGetValue(key, out FoliageOccluder[] cached))
        {
            return cached;
        }
        FoliageOccluder[] occluders = Collect(scene);
        _byScenePath[key] = occluders;
        return occluders;
    }

    private static FoliageOccluder[] Collect(PackedScene scene)
    {
        Node root = scene.Instantiate();
        if (root == null)
        {
            return System.Array.Empty<FoliageOccluder>();
        }
        var list = new List<FoliageOccluder>();
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

    private static void Walk(Node node, Transform3D parentXform, List<FoliageOccluder> output)
    {
        Transform3D xform = parentXform;
        if (node is Node3D n3)
        {
            xform = parentXform * n3.Transform;
        }
        if (node is FoliageCluster cluster)
        {
            output.Add(new FoliageOccluder
            {
                CenterLocal = xform.Origin,
                Radii = cluster.EllipsoidRadii,
                CastsSunShadow = cluster.CastsSunShadow,
                FadesWhenOccludingPlayer = cluster.FadesWhenOccludingPlayer,
            });
        }
        int childCount = node.GetChildCount();
        for (int i = 0; i < childCount; i++)
        {
            Walk(node.GetChild(i), xform, output);
        }
    }
}
