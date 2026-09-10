using Godot;

// A named, paintable set of ENTITIES to scatter over an area by rule — the
// wildlife, chests, loot and campfires of a place. The world-map painter's mob
// palette (AuthoringPaletteSource.MobSets) is a directory of these.
//
// It exists so "swamp wildlife" is defined ONCE: the same set can be painted
// anywhere on a map, referenced by a preset, and named by a generator kit's
// SpawnGenData, which no per-zone inline list could do without replicating
// itself.
//
// The rows do the placing. A SpawnListRow carries its OWN rate
// (squareMetersPerSpawn) plus the shared entry that knows how to spawn, so this
// adds only what a PALETTE needs on top of a list: a name and a colour to tell
// one set from another at a glance.
//
// Split out of the old SpawnSetData, which also carried the generator's tree and
// grass scatter — two channels no authored file ever used together (every
// mob_sets/*.tres set only entities, every prop_sets/*.tres only trees and
// grass). That half is SpawnGenData now.
[GlobalClass]
public partial class SpawnScatterData : Resource
{
    // Shown on the painter's palette button and in the map legend.
    [Export] public string displayName = "";

    // How this set is drawn on the world map: the button swatch and the dot per
    // actual spawn. Distinct colours are what let swamp and desert wildlife be
    // told apart at a glance.
    [Export] public Color mapColor = new Color(0.4f, 0.8f, 0.4f);

    // The rows themselves, as a shared list — so one ambient_swamp.tres is named
    // by this set and by the generator's zone passes rather than authored twice.
    [Export] public SpawnListData entities;

    // Managed mirror of entities.rows. The map preview asks for these once per
    // column per rebuild — tens of thousands of reads — and a
    // Godot.Collections.Array marshals a Variant on every index and on .Count.
    // Safe to cache without invalidation because *Data is immutable after load.
    private SpawnListRow[] _rowsFlat;

    public SpawnListRow[] RowsFlat
    {
        get
        {
            if (_rowsFlat == null)
            {
                Godot.Collections.Array<SpawnListRow> src = entities?.rows;
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
