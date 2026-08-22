using Godot;

// An entity whose cutaway decision is not taken at its own origin.
//
// GameClient hides a whole entity node once its GlobalPosition is above the clip
// height, which assumes the origin sits at the bottom of what it draws. A few do
// not: a waterfall's node is on its LIP, because that is the chunk it files into
// and where it reads from above, while the cascade itself hangs far below. Testing
// the origin took the entire fall away the moment its lip crossed the cut — and
// the part still under the ceiling is exactly what should have kept drawing.
//
// Only for entities that ALSO cut themselves per fragment in-shader. This picks
// the height at which the node stops existing; it does not slice anything, so an
// entity relying on it to hide its top half will simply poke through the ceiling.
public interface IClipAnchored
{
    // World Y the cutaway decision is taken at.
    float ClipAnchorY { get; }
}

// Re-anchors the ceiling cutaway for a visual that floats above the thing it
// belongs to.
//
// model_lit resolves the cutaway once per mesh, at that mesh's own origin, so a
// model hovering metres over its prop cuts away on its own: the peek iris opens
// on the forge's floating upgrade preview while the pedestal underneath stays
// solid. This measures each mesh's height above `_anchor` and pushes it as that
// mesh's `clip_anchor_offset`, so the whole arrangement dithers away as one
// piece at the host's elevation.
//
// Wiring: drop one under the interactive, point `_meshRoots` at the floating
// container(s) and `_anchor` at the node whose height they should clip at
// (normally the entity root). Offsets are relative, measured once from the
// authored rest pose, so the entity can be placed anywhere and a bob / spin
// afterwards carries the anchor with it.
[GlobalClass]
public partial class ClipAnchor : Node3D
{
    // The height the meshes should clip at.
    [Export] private Node3D _anchor;
    // Every MeshInstance3D under these is re-anchored. Containers rather than
    // meshes because the floating visual is usually an instanced FBX that split
    // into several MeshInstance3D.
    [Export] private Godot.Collections.Array<Node3D> _meshRoots = new();

    private static readonly StringName ClipAnchorOffsetParam = "clip_anchor_offset";

    public override void _Ready()
    {
        if (_anchor == null)
        {
            return;
        }
        float anchorY = _anchor.GlobalPosition.Y;
        foreach (Node3D root in _meshRoots)
        {
            if (root != null)
            {
                ApplyRecursive(root, anchorY);
            }
        }
    }

    private static void ApplyRecursive(Node node, float anchorY)
    {
        if (node is MeshInstance3D mesh)
        {
            mesh.SetInstanceShaderParameter(ClipAnchorOffsetParam, anchorY - mesh.GlobalPosition.Y);
        }
        foreach (Node child in node.GetChildren())
        {
            ApplyRecursive(child, anchorY);
        }
    }
}
