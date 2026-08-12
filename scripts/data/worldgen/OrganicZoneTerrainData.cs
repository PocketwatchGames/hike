using Godot;

// Per-zone tuning for the ORGANIC approach (OrganicTerrainGen). Pair with an
// OrganicTerrainData on the world.
//
// elevation / elevationRange are inherited and scaled into voxels by that
// resource's zoneElevationUnit, so the numbers read the same as they did under
// the plateau approach.
[GlobalClass]
public partial class OrganicZoneTerrainData : ZoneTerrainData
{
    // How this zone splits its sloping ground between the two characters that
    // play well: terraced bench-and-cliff, versus open walkable slope. 1
    // terraces nearly all of it (mesa country — wide flats separated by walls),
    // 0 leaves nearly all of it as slope.
    //
    // The point is the MIX, not the extreme: a long uninterrupted slope is poor
    // to play on, so even a low value keeps breaking it up — this weights how
    // often, it does not switch the intermixing off. Flat ground is unaffected
    // (there is no slope to terrace), so this is not a flatness control.
    [Export(PropertyHint.Range, "0,1,0.01")] public float benchedFraction = 0.5f;

    // Scales the vertical size of this zone's terrain walls — it leans the wall
    // draw toward the tall end of the world's authored band without leaving it,
    // so a zone can read as craggy or gentle while every wall stays between
    // cliffMinDrop and cliffMaxDrop. Lowlands read better under 1, mountains
    // above it.
    [Export(PropertyHint.Range, "0.25,3,0.05")] public float cliffScale = 1f;
}
