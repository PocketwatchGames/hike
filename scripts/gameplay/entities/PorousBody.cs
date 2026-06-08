using Godot;

// A prop / interactive movement-collider that is "porous": it blocks movement
// and grounded line-of-sight (queries mask Solid) while letting smell, sound,
// perched vision, and flight pass straight through (those mask Environment
// alone). Trees, bushes, rocks, chests, wells — anything the world should bump
// into but creatures should still sense past.
//
// The node TYPE is the source of truth: porousness is "this collider is a
// PorousBody," visible in the scene tree, not an invisible layer integer
// remapped at spawn. A solid prop/interactive (a door, a building wall) uses a
// plain StaticBody3D on the Environment layer instead. This replaced the old
// PorousColliders.Apply spawn-time remap, which keyed off the magic "still on
// layer 1" sentinel and meant the authored layer wasn't the runtime layer.
//
// [Tool] so the forced layer also shows in the editor inspector (the named
// "Porous" checkbox), keeping the scene WYSIWYG. MeshAutoCollider bakes this
// type for FBX props; hand-authored prop/interactive scenes use it directly.
[Tool]
[GlobalClass]
public partial class PorousBody : StaticBody3D
{
    public override void _Ready()
    {
        CollisionLayer = (uint)ECollisionLayer.Porous;
    }
}
