using Godot;
using Godot.Collections;

// A cluster of heterogeneous entities (mobs, chests, traps, loot, etc.)
// spawned together around a single anchor point. The group defines composition
// and scatter; the call site picks the anchor and supplies position resolution
// (surface lookup, cave-pocket lookup, etc.). Each entry rolls its own
// CountMin..CountMax and emits its entity type via SpawnEntryData.Spawn.
[GlobalClass]
public partial class SpawnGroupData : Resource
{
    // Maximum horizontal distance from the anchor that entries can be placed.
    [Export] public float ScatterRadius = 3f;

    [Export] public Array<SpawnEntryData> Entries = new();
}
