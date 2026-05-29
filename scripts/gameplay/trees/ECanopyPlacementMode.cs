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
    // drooping canopy (willow, weeping birch).
    Drooping,
    // Cards stand vertically on a fan around the ellipsoid's base circle
    // (Y = CenterOffset.y), facing outward radially. Reads as upright
    // grass-like strands; pair with a small flat ellipsoid for a tuft.
    UprightStrand,
}
