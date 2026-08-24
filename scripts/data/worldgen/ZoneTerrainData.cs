using Godot;

// Per-zone terrain tuning, for ONE approach. `ZoneGenData.terrain` holds a
// subclass matching the approach the world runs; a zone authored for one
// approach carries none of another's fields, which is the point.
//
// This mirrors the world-level split (WorldGenData.terrain / TerrainGenData)
// one level down. The fields here are the contract every approach must answer
// for a zone — how high it sits, how much relief it gets, whether it is forced
// flat — and they are kernel-blended across zone borders like the other
// per-position scalars. Anything only one approach reads goes on the subclass
// and is blended by that approach itself (see ZoneField.SampleBlended's
// weights-out overload), so this base never grows when an approach is added.
//
// A zone whose terrain slot is null, or holds another approach's subclass,
// falls back to these defaults rather than failing — worlds under construction
// routinely have half-migrated zones.
[GlobalClass]
public abstract partial class ZoneTerrainData : Resource
{
    // Authored center elevation for the zone. The value is treated as the
    // elevation at the zone's center; WorldGen kernel-blends it across (wx, wz)
    // so adjacent zones transition smoothly. Inland zones sit at +1 by
    // convention; wetlands at -1; sea shelves at 0.
    //
    // UNITS ARE PER-APPROACH: the plateau approach reads this in plateau steps;
    // the organic approach multiplies by its own zoneElevationUnit. That is
    // exactly why the pair lives on a per-approach resource rather than on
    // ZoneGenData, where one number had to mean two things.
    [Export] public float elevation = 0f;

    // Half-amplitude of the zone's terrain variation, in the same units as
    // elevation. Mountain zones push this up for dramatic peaks; flat zones
    // keep it lower.
    [Export] public float elevationRange = 2f;

    // Force this zone's surface to a fixed flat level, overriding the noisy
    // height. WorldGen blends the column height toward flattenLevel by the
    // zone's kernel weight, so the zone core is dead flat while its edge melts
    // back into the surrounding terrain. Used for a hand-placed clearing (the
    // starting village).
    [Export] public bool flattenSurface = false;

    // Target level when flattenSurface is set, anchored at sea level: 0 = the
    // beach/water line (dry shoreline, no water), +1 = one step above, -1 =
    // submerged. Ignored unless flattenSurface.
    [Export] public int flattenLevel = 0;
}
