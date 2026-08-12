using Godot;

// Per-zone tuning for the PLATEAU approach (PlateauTerrainGen). Pair with a
// PlateauTerrainData on the world.
//
// elevation / elevationRange are inherited and read in PLATEAU STEPS here: +1
// is one plateauStep above sea level.
[GlobalClass]
public partial class PlateauZoneTerrainData : ZoneTerrainData
{
    // |tunnelNoise| below this carves a tunnel band. 0 gives a zone with no
    // tunnels at all; blended across zone borders, so density fades rather
    // than snapping at the seam.
    [Export] public float tunnelThreshold = 0.1f;

    // |caveNoise| above this carves cave. Higher = more solid rock, so 1.0 is
    // effectively cave-free.
    [Export] public float caveThreshold = 0.25f;

    // Cave 3D-noise frequency. ONE field spans the world, so the generator
    // takes this from the first zone only — caveThreshold above is what
    // actually varies cave density per zone. Authored here rather than on
    // PlateauTerrainData because the value is a property of the rock a zone is
    // made of, and moving it would misrepresent that even though only one
    // zone's copy is read.
    [Export] public float caveNoiseFrequency = 0.04f;

    // Path height-smoothing parameters. Currently unread — kept as authored
    // knobs reserved for the path/ramp authoring pass this approach never got.
    [Export] public float pathThreshold = 0.1f;
    [Export] public float pathBlendBand = 0.05f;
}
