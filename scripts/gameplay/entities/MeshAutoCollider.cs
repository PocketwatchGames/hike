using Godot;

// Design-time helper for prop scenes that instance an FBX model. Pressing
// "Bake" walks descendant MeshInstance3Ds and:
//
//   1. RECENTER — translates this node's non-collider Node3D children so the
//      combined mesh AABB is centered on this node's X/Z origin and its
//      bottom face touches this node's Y=0. Synty's PolygonGeneric rocks
//      (and many other store assets) ship with their original scene
//      composition position baked into the vertex data, so a freshly
//      instanced FBX appears offset from the origin. The author wants the
//      visible rock under the prop's spawn point; recenter does that
//      without re-exporting the FBX.
//
//   2. BAKE COLLISION — for each MeshInstance3D, adds a PorousBody +
//      ConcavePolygonShape3D as a direct child of this node with owner =
//      scene root, so the colliders persist in the .tscn. Runtime is then
//      zero-cost — the colliders are just authored scene nodes.
//
// Re-pressing the button is idempotent: recenter on an already-centered
// mesh is a zero-translation, and bake removes prior AutoCollision_*
// children before regenerating.
//
// Layer: the generated body is a PorousBody, so it sits on the Porous layer —
// the prop blocks movement / grounded sight while smell, sound, perched vision,
// and flight pass through. (This tool only services porous props; a solid prop
// authors a plain StaticBody3D on Environment instead.)
//
// Runtime fallback: if the scene was never baked, _Ready builds a PorousBody +
// trimesh shape on each descendant MeshInstance3D so unbaked scenes still
// collide on the right layer. Recentering does NOT happen at runtime — the
// .tscn either ships with the centered authored transform or it doesn't.
[Tool]
[GlobalClass]
public partial class MeshAutoCollider : Node3D
{
    private const string AutoCollisionPrefix = "AutoCollision_";

    private void ApplyMaterialOverride()
    {
        if (materialOverride == null)
        {
            return;
        }
        foreach (Node descendant in FindChildren("*", "MeshInstance3D", true, false))
        {
            if (descendant is MeshInstance3D mi)
            {
                mi.MaterialOverride = materialOverride;
            }
        }
    }

    // Forwarded to every generated PorousBody. On for props meant to be walked
    // up rather than bumped into — stairs, ramps, boardwalks.
    [Export] public bool steppable;

    // Applied to every descendant MeshInstance3D, replacing the material the FBX
    // brings in through its import settings. Authored here rather than as a
    // surface_material_override in the .tscn because the meshes live INSIDE an
    // instanced FBX scene, so overriding one means naming a node path through
    // someone else's scene — which changes whenever the model is re-exported.
    // Walking the descendants is what this component already does.
    //
    // Leave it null and the prop keeps whatever the import bound, which is what
    // every rock that is not a variant does.
    [Export] public Material materialOverride;

    [ExportToolButton("Bake")]
    public Callable BakeButton => Callable.From(Bake);

    public override void _Ready()
    {
        // Before the editor bail-out, so the swap is WYSIWYG: a variant reads as
        // its own material in the editor rather than only once the game runs.
        ApplyMaterialOverride();
        if (Engine.IsEditorHint())
        {
            return;
        }
        if (HasBakedCollider())
        {
            return;
        }
        foreach (Node descendant in FindChildren("*", "MeshInstance3D", true, false))
        {
            if (descendant is MeshInstance3D mi && mi.Mesh != null)
            {
                var body = new PorousBody { Name = AutoCollisionPrefix + mi.Name, steppable = steppable };
                mi.AddChild(body);
                body.AddChild(new CollisionShape3D
                {
                    Shape = mi.Mesh.CreateTrimeshShape(),
                    Name = "Shape",
                });
            }
        }
    }

    private bool HasBakedCollider()
    {
        foreach (Node child in GetChildren())
        {
            if (child is StaticBody3D && child.Name.ToString().StartsWith(AutoCollisionPrefix))
            {
                return true;
            }
        }
        return false;
    }

    private void Bake()
    {
        Recenter();
        BakeCollision();
    }

    private void Recenter()
    {
        Aabb? combined = ComputeCombinedAabbInLocal();
        if (!combined.HasValue)
        {
            return;
        }
        Vector3 center = combined.Value.GetCenter();
        Vector3 offset = new Vector3(-center.X, -combined.Value.Position.Y, -center.Z);
        foreach (Node child in GetChildren())
        {
            if (child is Node3D node3d && !node3d.Name.ToString().StartsWith(AutoCollisionPrefix))
            {
                node3d.Position += offset;
            }
        }
    }

    private Aabb? ComputeCombinedAabbInLocal()
    {
        Aabb? combined = null;
        Transform3D worldToLocal = GlobalTransform.AffineInverse();
        foreach (Node descendant in FindChildren("*", "MeshInstance3D", true, false))
        {
            if (descendant is MeshInstance3D mi && mi.Mesh != null)
            {
                Transform3D meshToLocal = worldToLocal * mi.GlobalTransform;
                Aabb meshAabb = mi.Mesh.GetAabb();
                Aabb localAabb = TransformAabb(meshAabb, meshToLocal);
                combined = combined.HasValue ? combined.Value.Merge(localAabb) : localAabb;
            }
        }
        return combined;
    }

    // Apply a Transform3D to every corner of an Aabb and return the AABB of
    // the transformed corners. Godot's C# bindings don't expose the
    // built-in Transform3D*Aabb operator, so do it explicitly.
    private static Aabb TransformAabb(Aabb aabb, Transform3D t)
    {
        Vector3 p = aabb.Position;
        Vector3 s = aabb.Size;
        Vector3[] corners = new Vector3[]
        {
            t * p,
            t * (p + new Vector3(s.X, 0, 0)),
            t * (p + new Vector3(0, s.Y, 0)),
            t * (p + new Vector3(0, 0, s.Z)),
            t * (p + new Vector3(s.X, s.Y, 0)),
            t * (p + new Vector3(s.X, 0, s.Z)),
            t * (p + new Vector3(0, s.Y, s.Z)),
            t * (p + s),
        };
        Vector3 min = corners[0];
        Vector3 max = corners[0];
        for (int i = 1; i < corners.Length; i++)
        {
            min = min.Min(corners[i]);
            max = max.Max(corners[i]);
        }
        return new Aabb(min, max - min);
    }

    private void BakeCollision()
    {
        Node sceneRoot = GetTree()?.EditedSceneRoot ?? Owner ?? this;

        foreach (Node child in GetChildren())
        {
            if (child is StaticBody3D body && body.Name.ToString().StartsWith(AutoCollisionPrefix))
            {
                RemoveChild(body);
                body.QueueFree();
            }
        }

        foreach (Node descendant in FindChildren("*", "MeshInstance3D", true, false))
        {
            if (descendant is MeshInstance3D mi && mi.Mesh != null)
            {
                var body = new PorousBody { Name = AutoCollisionPrefix + mi.Name, steppable = steppable };
                AddChild(body);
                body.Owner = sceneRoot;

                var shape = new CollisionShape3D
                {
                    Shape = mi.Mesh.CreateTrimeshShape(),
                    Name = "Shape",
                };
                body.AddChild(shape);
                shape.Owner = sceneRoot;
            }
        }
    }
}
