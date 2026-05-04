// Selects which AmbienceState scalar an AmbienceLayerData remaps into a
// volume / pitch curve. Authored on the resource so the same layer asset
// can be reused (e.g. one wind layer in temperate zones and a desert
// zone overrides only the stream + a couple of curves).
//
// Wire values are stable — appended only, never reused, since these
// values land in .tres files via the editor's enum picker. Add new
// fields at the end and never reorder.
public enum AmbienceField : byte
{
    Wetness = 0,
    WindSpeed = 1,
    FoliageDensity = 2,
    WaterDensity = 3,
    ShorelineFactor = 4,
    Openness = 5,
    Caveness = 6,
    FogDensity = 7,

    // Constant 1.0 — for layers whose volume should only depend on the
    // time-of-day curve, not on a state field. Insect bed / distant
    // ocean fall in this bucket.
    Constant = 255,
}
