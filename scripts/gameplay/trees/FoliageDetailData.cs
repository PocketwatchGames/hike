using Godot;

// One detail-sprite species scattered sparsely across a tree's canopy —
// flowers, buds, acorns, berries, etc. Authored as an entry in
// FoliageMultiMesh.Details (a FoliageDetailData array). Every cluster on the
// tree scatters this species automatically; there is no per-cluster opt-in,
// the spread is driven entirely by Density below.
//
// Details are camera-facing billboards (tree_detail.gdshader), placed at
// points on each cluster's surface (via the leaf placement pipeline — the
// Placement mode, jitter, droop) and pushed outward so they sit proud of the
// leaf cards. They share the canopy's lighting, wind, tint, and player-
// occlusion fade, so a detail reads as part of the tree it sits on; only the
// texture, size, and offsets below differ from the leaves.
[Tool]
[GlobalClass]
public partial class FoliageDetailData : Resource
{
    [Export] public Texture2D Texture;

    // Detail card count per cluster, expressed as a multiplier on that
    // cluster's CardCount (so a denser/bigger cluster gets proportionally
    // more details). A cluster scatters round(CardCount * Density) of this
    // species. Keep it small — 0.1–0.2 reads as occasional acorns/flowers
    // tucked among the leaves; 1.0 would be roughly one detail per leaf card.
    [Export(PropertyHint.Range, "0,1,0.01,or_greater")] public float Density = 0.15f;

    // Tint endpoints multiplied into the detail texture, mirroring the
    // leaf_tint_a/b knobs on FoliageMultiMesh. Default white = show the
    // texture's own colors (flowers usually want their authored color, not
    // the green leaf tint). The per-cluster / per-card tint variation in
    // tree_cards_lit.gdshader still drifts the mix between these two, so
    // leaving A and B different gives subtle per-card color spread.
    [Export] public Color TintA = Colors.White;
    [Export] public Color TintB = Colors.White;

    // Detail billboard size range (world meters, the HEIGHT of the sprite),
    // sampled per card. Width follows the texture's aspect ratio. Independent
    // of the cluster's CardSizeMin/Max so a flower can be smaller than the
    // leaf cards it nestles among.
    [Export] public float SizeMin = 0.4f;
    [Export] public float SizeMax = 0.6f;

    // View-space depth bias in meters — how far toward the camera the billboard
    // is nudged so it layers in front of the leaf cards it overlaps. Bigger,
    // chunkier details (acorns) may want a touch more than flat ones (flowers);
    // too large and the detail starts showing through real occluders.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float DepthBias = 0.12f;

    // HORIZONTAL outward offset (meters, in the cluster's XZ plane) — carries a
    // detail out toward the canopy's silhouette rim so it clears the leaf cards
    // that splay out sideways. Deliberately horizontal, not along the surface
    // normal: the clusters are oblate, so their normals point nearly straight up
    // and a normal push would just lift details into the air without reaching
    // the outer edge. Crank it up to push details past the drooping leaf cards.
    [Export(PropertyHint.Range, "0,4,0.05")] public float OutwardOffset = 1.0f;

    // VERTICAL offset (meters) applied to the anchor — positive lifts, negative
    // drops. The billboard is BOTTOM-pivoted (the sprite rises up from its
    // anchor), so the default reads slightly high; set this negative (e.g.
    // -0.3) so acorns/berries HANG DOWN and emerge below the drooping leaves on
    // the sides instead of poking up through the top.
    [Export(PropertyHint.Range, "-2,2,0.05")] public float VerticalOffset;
}
