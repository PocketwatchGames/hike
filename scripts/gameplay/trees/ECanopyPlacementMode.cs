// Placement strategy for the cards within one FoliageCluster. Top-level so
// the Godot.SourceGenerators ScriptPropertiesGenerator can marshal the
// [Export] correctly.
public enum ECanopyPlacementMode
{
    // Cards lie on the ellipsoid surface; each card's outward normal is
    // the ellipsoid gradient at its position. Reads as "leaves wrapping a
    // soft volume" — the standard broadleaf canopy.
    HemisphericalRadial,
    // Same placement as HemisphericalRadial, but each card's outward
    // normal is tilted toward -Y by DroopAmount. Reads as a weeping /
    // drooping canopy (willow, weeping birch). The tip swings INWARD
    // (back over the trunk) on its way down, so cards hang close to the
    // crown's vertical axis.
    Drooping,
    // Rotates each card's TIP (its long axis — the direction a bough
    // points) about the horizontal tangent around the trunk, driven by
    // DroopAmount: 0 = points straight up, 0.5 = points straight out
    // (horizontal), 1 = points straight down. Distinct from Drooping,
    // which keeps the card facing along the ellipsoid gradient and only
    // tilts the tip — that swing gets projected away on the side cards,
    // so it can't make a bough genuinely sweep outward/down. Pine boughs
    // read best in the upper half of the range (~0.6-0.8), where the tip
    // points outward and down.
    SweptBough,
    // Cards stand vertically on a fan around the ellipsoid's base circle
    // (Y = CenterOffset.y), facing outward radially. Reads as upright
    // grass-like strands; pair with a small flat ellipsoid for a tuft.
    UprightStrand,
}
