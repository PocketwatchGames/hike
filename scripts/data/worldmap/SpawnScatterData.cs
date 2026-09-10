using Godot;

// A named, paintable set of ENTITIES to scatter over an area by rule — the
// wildlife, forage, traps and chests of a place. The world-map painter's mob
// palette (AuthoringPaletteSource.ScatterSets) is a directory of these:
// `world_authoring/spawn_scatters/`.
//
// It IS a SpawnListData — the rows, the rates and the entries are the base
// class's — plus the two things a PALETTE needs on top of a list: a name and a
// colour to tell one set from another at a glance. So anywhere a spawn list is
// wanted a scatter set will do, including a generator zone pass.
//
// The rows were a SEPARATE file until nothing shared them. The indirection was
// there so one ambient_swamp.tres could be named by a painted set and by the
// generator's zone passes at once — and once the generator's own lists moved to
// world_gen/, every ambient list had exactly one referrer, its own set. Two
// files per set bought a sharing nobody was doing. What it costs is that two
// sets wanting identical wildlife author it twice (swamp and swamp_fire today);
// that is the ordinary price of an embedded list, and forking is what an author
// wants the moment the two diverge.
//
// It is NOT a "mob set", which is what the directory used to be called: what a
// set holds is whatever its rows name, and today that is mushrooms, berry
// trees, traps and buried spots as much as goblins.
[GlobalClass]
public partial class SpawnScatterData : SpawnListData
{
    // Shown on the painter's palette button and in the map legend.
    [Export] public string displayName = "";

    // How this set is drawn on the world map: the button swatch and the dot per
    // actual spawn. Distinct colours are what let swamp and desert wildlife be
    // told apart at a glance.
    [Export] public Color mapColor = new Color(0.4f, 0.8f, 0.4f);

    // Managed mirror of rows. The map preview asks for these once per column per
    // rebuild — tens of thousands of reads — and a Godot.Collections.Array
    // marshals a Variant on every index and on .Count. Safe to cache without
    // invalidation because *Data is immutable after load.
    private SpawnListRow[] _rowsFlat;

    public SpawnListRow[] RowsFlat
    {
        get
        {
            if (_rowsFlat == null)
            {
                Godot.Collections.Array<SpawnListRow> src = rows;
                int n = src?.Count ?? 0;
                _rowsFlat = new SpawnListRow[n];
                for (int i = 0; i < n; i++)
                {
                    _rowsFlat[i] = src[i];
                }
            }
            return _rowsFlat;
        }
    }

    public string Label => string.IsNullOrEmpty(displayName)
        ? (string.IsNullOrEmpty(ResourcePath) ? "Set" : ResourcePath.GetFile().GetBaseName())
        : displayName;
}
