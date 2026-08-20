using Godot;

// A named, reusable set of things to place over an area — a pine stand, a palm
// oasis, a wolf pack, a scattering of ruined walls. Referenced by a painter
// palette (WorldMapData.propSets) and, in time, by a TerrainKitData as its
// ambient scatter.
//
// It exists so "pine stand" is defined ONCE. The same set can be painted over
// any ground, referenced by several kits, and appear in more than one zone,
// which no per-zone or per-kit inline list could do without replicating itself.
//
// ONE set holds both canopy and ground cover, because a forest is a single
// authored idea and it would be tedious to paint its trees and its grass as two
// regions that must agree. They are separate slots rather than one list because
// their densities differ by an order of magnitude — trees every ~64 m, grass
// every ~6 m — and a single rate cannot say both. Two slots is also exactly the
// two PropTypes, so this cannot be under-general: anything placed is either an
// occluding tree or ground foliage.
//
// Entities are a third channel: a SpawnListData whose entries each carry their
// OWN rate and spawn logic (mobs, chests, loot, traps, campfires).
[GlobalClass]
public partial class SpawnSetData : Resource
{
    // Shown on the painter's palette button and in the map legend.
    [Export] public string displayName = "";

    // How this set is drawn on the world map: the button swatch and the dot per
    // actual spawn. Distinct colours are what let a pine stand and a palm oasis
    // be told apart at a glance.
    [Export] public Color mapColor = new Color(0.4f, 0.8f, 0.4f);

    // Canopy — PropType.Tree. These are worldgen's own knobs, under worldgen's
    // own names, because the placement here runs worldgen's math exactly:
    // TerrainKitData.forestNoiseFrequency / forestThreshold / forestDensity and
    // treesPerChunkMin/Max. Anything invented here (a rate in square metres, a
    // normalised gate) produced a different curve, and different curves are how
    // a painted forest and a generated one stop looking alike.
    //
    // Trees come from TWO passes, as they do in worldgen:
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

    // Metres a placed prop may wander off its column centre (WorldGenData's
    // tallGrassJitter).
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float positionJitter = 0.2f;

    // Entities placed by this set. Each entry rolls its own authored rate.
    [Export] public SpawnListData entities;

    // One noise field per pass, built once and shared by the map preview and the
    // bake — they must sample the SAME field or the dots stop predicting the
    // world. Worldgen builds its forest noise at frequency 1 and multiplies the
    // COORDINATES by the kit frequency; setting Frequency here is the same
    // transform. Seeded off the set's own name, stably (see StableSeed).
    private FastNoiseLite _forestNoise;
    private FastNoiseLite _grassNoise;

    public FastNoiseLite ForestNoise => _forestNoise ??= MakeNoise(forestNoiseFrequency, 0);
    public FastNoiseLite GrassNoise => _grassNoise ??= MakeNoise(grassNoiseFrequency, 1);

    private FastNoiseLite MakeNoise(float frequency, int channel)
    {
        return new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = frequency,
            FractalOctaves = 2,
            Seed = StableSeed(Label) * 31 + channel,
        };
    }

    // NOT string.GetHashCode: .NET randomises that per PROCESS, so the patches
    // would move between the painter session that drew them and the bake that
    // reads them back. FNV-1a is stable forever.
    private static int StableSeed(string text)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in text ?? "")
            {
                h = (h ^ c) * 16777619u;
            }
            return (int)h;
        }
    }

    // Managed mirror of entities.entries. The map preview asks for these once
    // per column per rebuild — tens of thousands of reads — and a
    // Godot.Collections.Array marshals a Variant on every index and on .Count.
    // Safe to cache without invalidation because *Data is immutable after load.
    private SpawnEntryData[] _entriesFlat;

    public SpawnEntryData[] EntriesFlat
    {
        get
        {
            if (_entriesFlat == null)
            {
                Godot.Collections.Array<SpawnEntryData> src = entities?.entries;
                int n = src?.Count ?? 0;
                _entriesFlat = new SpawnEntryData[n];
                for (int i = 0; i < n; i++)
                {
                    _entriesFlat[i] = src[i];
                }
            }
            return _entriesFlat;
        }
    }

    public string Label => string.IsNullOrEmpty(displayName)
        ? (string.IsNullOrEmpty(ResourcePath) ? "Set" : ResourcePath.GetFile().GetBaseName())
        : displayName;
}
