using Godot;

// Shared remap for IPorous entities: any collider in the subtree authored on
// the bare Environment layer (1) is moved to Porous, leaving colliders on other
// layers as-authored (so a tallgrass-style rustle area or a bespoke collider
// keeps its layer). Used by World at spawn for props and porous interactives so
// the Porous flag means the same thing everywhere.
public static class PorousColliders
{
    public static void Apply(Node root)
    {
        if (root is CollisionObject3D body && body.CollisionLayer == (uint)ECollisionLayer.Environment)
        {
            body.CollisionLayer = (uint)ECollisionLayer.Porous;
        }
        foreach (Node child in root.GetChildren())
        {
            Apply(child);
        }
    }
}
