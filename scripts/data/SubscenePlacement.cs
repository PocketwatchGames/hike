using Godot;

// Declarative placement of a subscene in WorldGenData. Path points at a
// `.hikescene` file; AnchorXZ is the world XZ where the subscene's anchor
// should land. Y is computed by SubsceneStamper.ComputeSurfaceAnchor —
// average top-surface elevation across the footprint — so the cottage
// sits flush on whatever terrain WorldGen built underneath it.
//
// Placement is intentionally dumb: no slope check, no overlap test, no
// rotation. Use it for hand-curated landmarks where the authored XZ is
// known to land on reasonable terrain. Procedural placement is a
// separate, deferred problem.
[GlobalClass]
public partial class SubscenePlacement : Resource
{
    [Export(PropertyHint.File, "*.hikescene")] public string path;
    [Export] public Vector2I anchorXZ;
}
