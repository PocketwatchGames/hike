using Godot;

// What GROWS on a patch of ground, by rule — a pine stand, a palm oasis.
// Referenced by ZoneGenData.foliage and by nothing else: the world-map
// painter places a painted prop region DIRECTLY from a PropListData, because
// the reason to paint props is to say where the player cannot walk, while the
// noise fields below shape a wood instead. Scenery grown by rule, not furniture
// put somewhere on purpose.
//
// Named for the family it belongs to — ZoneGenData, RegionGenData,
// TerrainGenData are its neighbours, and like them nothing but the generator
// reads it.
//
// It exists so "pine stand" is defined ONCE. The same set can be referenced by
// several zones, which no inline list could do without replicating itself.
//
// ONE set holds both canopy and ground cover, because a forest is a single
// authored idea. They are separate slots rather than one list because their
// densities differ by an order of magnitude — trees every ~64 m, grass every
// ~6 m — and a single rate cannot say both. Two slots is also exactly the two
// PropTypes, so this cannot be under-general: anything placed is either an
// occluding tree or ground foliage.
[GlobalClass]
public partial class FoliageGenData : Resource
{
    // Shown wherever a zone's scatter is named in the editor.
    [Export] public string displayName = "";

    // Canopy — PropType.Tree.
    //
    // Trees come from TWO passes:
    //   A: treesPerChunkMin..Max attempts at random cells in every chunk — the
    //      scattered trees that stand outside any wood.
    //   B: forest pockets — where the noise clears forestThreshold, a per column
    //      roll of forestDensity * (f - threshold) / (1 - threshold), uncapped,
    //      which is what makes a wood dense in its middle and thin at its edge.
    [Export(PropertyHint.Range, "0.001,1,0.001")] public float forestNoiseFrequency = 0.05f;
    [Export(PropertyHint.Range, "-1,1,0.01")] public float forestThreshold = 0.01f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float forestDensity = 0.5f;
    [Export(PropertyHint.Range, "0,32,1")] public int treesPerChunkMin = 0;
    [Export(PropertyHint.Range, "0,32,1")] public int treesPerChunkMax = 4;
    [Export] public WeightedScene[] treeScenes = System.Array.Empty<WeightedScene>();

    // Ground cover — PropType.Foliage. Worldgen gates grass on one threshold
    // and then places on EVERY admitted column; there is no density roll and no
    // ramp, which is why its grass reads as solid clumps. grassNoiseFrequency is
    // world-level in worldgen (WorldGenData), grassThreshold per zone.
    [Export(PropertyHint.Range, "0.001,1,0.001")] public float grassNoiseFrequency = 0.1f;
    [Export(PropertyHint.Range, "-1,1,0.01")] public float grassThreshold = 0.3f;
    [Export] public WeightedScene[] foliageScenes = System.Array.Empty<WeightedScene>();

    public string Label => string.IsNullOrEmpty(displayName)
        ? (string.IsNullOrEmpty(ResourcePath) ? "Set" : ResourcePath.GetFile().GetBaseName())
        : displayName;
}
