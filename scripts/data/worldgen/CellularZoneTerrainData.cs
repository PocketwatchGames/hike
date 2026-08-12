using Godot;

// Per-zone tuning for the CELLULAR approach (CellularTerrainGen). Pair with a
// CellularTerrainData on the world.
//
// The inherited elevation / elevationRange do the heavy lifting and are what
// give each zone its own band: a zone's terraces are centred on elevation and
// spread over elevationRange, so the mountain zone gets both a wider range of
// cell heights and a higher maximum than the lowlands purely from those two
// numbers. The fields here only bias HOW that band is cut into cells.
[GlobalClass]
public partial class CellularZoneTerrainData : ZoneTerrainData
{
    // Scales the field spread at which this zone's cells subdivide. Below 1 the
    // zone subdivides more readily, so it ends up with smaller, more numerous
    // terraces at the same relief; above 1 it holds broad flat tops.
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float cellSubdivideScale = 1f;

    // Scales the tallest wall allowed between two of this zone's cells, without
    // leaving the world's authored ceiling. Lowlands read better under 1,
    // mountains above it.
    [Export(PropertyHint.Range, "0.1,3,0.05")] public float cliffScale = 1f;
}
