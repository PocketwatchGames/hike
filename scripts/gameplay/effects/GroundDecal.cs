using Godot;

// A flat ground mark — scorch, web, slime, a targeting ring — drawn by the
// top-down ground-stain projector rather than by the main camera. Ground marks
// are deliberately NOT Godot `Decal`s: a real decal washes out wherever the
// terrain shader's EMISSION dominates, so a stain is instead an unshaded,
// alpha-blended quad on the stain-proxy layer that GroundStainProjector
// composites into the ground's BASE color (see scripts/client/CLAUDE.md).
//
// This node exists so the layer, the sizing and the fade are not re-authored
// per scene: the render layer is not an authoring decision, and the two things
// an owner wants to drive at runtime — how wide the mark is and how strongly it
// reads — are both easy to get wrong by hand (a shared mesh must never be
// resized in place; alpha must not be pushed through the shared material).
//
// Owners drive it; it has no lifetime of its own. GroundDecalPreview fades it
// on its own wall clock, GasCloud on its sim-clock expiry.
[GlobalClass]
public partial class GroundDecal : MeshInstance3D
{
    // Half-extent the authored mesh spans, resolved from the mesh itself so a
    // radius override has something to scale against without the author
    // restating the size in two places.
    private float _authoredRadius;

    public override void _Ready()
    {
        // Forced rather than authored: a stain quad on any other layer draws
        // straight to the screen instead of into the projector.
        Layers = GroundStainProjector.STAIN_PROXY_LAYER_MASK;
    }

    // Resize the mark to span `radius` metres. Scales the node — the mesh is a
    // .tscn sub-resource shared across every instance of that scene, so
    // resizing it in place would bleed into every other live decal from the
    // same scene (the same trap DamageZone.OverrideAuthoring duplicates around).
    // Non-positive values keep the authored size.
    public void SetRadius(float radius)
    {
        float authored = AuthoredRadius();
        if (radius <= 0f || authored <= 0f)
        {
            return;
        }
        float s = radius / authored;
        Scale = new Vector3(s, 1f, s);
    }

    // 1 = fully drawn, 0 = invisible. Rides GeometryInstance3D.Transparency
    // (a per-instance value) so the shared material is never duplicated to fade
    // one mark; it requires that material to allow transparency, which every
    // stain material does.
    public void SetOpacity(float opacity)
    {
        Transparency = Mathf.Clamp(1f - opacity, 0f, 1f);
    }

    private float AuthoredRadius()
    {
        if (_authoredRadius <= 0f && Mesh != null)
        {
            // Works pre-_Ready, which matters: an owner may size the decal
            // before the scene is added to the tree.
            Aabb bounds = Mesh.GetAabb();
            _authoredRadius = Mathf.Max(bounds.Size.X, bounds.Size.Z) * 0.5f;
        }
        return _authoredRadius;
    }
}
