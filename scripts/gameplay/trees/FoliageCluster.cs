using Godot;

// One foliage cluster — a Node3D positioned in the scene, typically as a
// child of a FoliageMultiMesh. Defines the SHAPE of one ball of leaves;
// the FoliageMultiMesh parent walks its FoliageCluster children at build
// time and bakes their cards into a single combined MultiMesh.
//
// Cluster's TRANSFORM is its center+rotation+scale within the parent
// FoliageMultiMesh space. Translation is the main authoring lever (drop
// the cluster, position it on a branch); rotation and uniform scale also
// work — the shader's sphere-normal math compensates for rotation, and
// uniform scale survives normalize. Non-uniform scale will distort the
// sphere-normal direction.
[Tool]
[GlobalClass]
public partial class FoliageCluster : Node3D
{
    [Export] public Vector3 EllipsoidRadii = new Vector3(1.5f, 1.5f, 1.5f);

    // When true, FoliageStamper rasterizes this cluster's ellipsoid plus a
    // downward shadow column into WorldState.CanopyAttenuation, which
    // LightEngine then reads as extra sun + block-light falloff. Net effect:
    // the cluster's XZ footprint reads as "indoors" for rain coverage and
    // any other GetSkyLight01 probe — i.e. standing under it shelters from
    // rain. Defaults OFF so decorative foliage (tall grass, ground cover,
    // bushes) is opt-out by default — set true per-cluster on trees whose
    // canopies are tall enough to actually shelter the player.
    [Export] public bool CastsSunShadow;

    [Export] public int CardCount = 30;
    [Export] public float CardSizeMin = 1.0f;
    [Export] public float CardSizeMax = 1.5f;

    [Export] public ECanopyPlacementMode Placement = ECanopyPlacementMode.Drooping;
    [Export(PropertyHint.Range, "0,1,0.01")] public float AngleJitter;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RollJitter = 1.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DroopAmount = 1.0f;

    // Per-cluster placement seed. Different clusters under one parent
    // should ideally have different seeds so their internal card patterns
    // don't align identically; FoliageMultiMesh combines this with the
    // cluster's child-index for uniqueness, so leaving it at 0 still
    // produces distinct patterns per cluster.
    [Export] public int PlacementSeed;
}
