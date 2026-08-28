using System;
using Godot;

// A SpawnGroupData row: an entry plus how many of it the cluster holds and
// where in the cluster it goes. No rate — the group already decided to fire,
// and its own rate rides the SpawnListRow that names the group.
[GlobalClass]
public partial class SpawnGroupRow : SpawnRow
{
    // How many to scatter within the group's scatterRadius — 2..3 goblins per
    // camp.
    [Export(PropertyHint.Range, "1,16,1")] public int countMin = 1;
    [Export(PropertyHint.Range, "1,16,1")] public int countMax = 1;

    // Pin to the group's anchor (the cluster centre) instead of scattering —
    // the centrepiece. A camp's hearth, and the safety zone at a village
    // centre, whose radius has to be centred on the cluster rather than offset
    // to wherever the scatter happened to drop it. The entry's placement gates
    // still run.
    [Export] public bool placeAtAnchor;

    public int RollCount(Random rng)
    {
        return rng.Next(countMin, Math.Max(countMin, countMax) + 1);
    }
}
