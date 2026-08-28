using System;
using Godot;

// A SpawnListData row: an entry plus the RATE this list wants it at. The
// per-column scan places one entity per hit, so a rate is the only thing
// deciding how many of it a zone gets — which is why a list row has no count.
[GlobalClass]
public partial class SpawnListRow : SpawnRow
{
    // Average qualifying square meters between spawns (each candidate column is
    // 1m²). Inverse of a per-1m² probability: 1000 means "≈one spawn per 1000m²
    // of eligible terrain", 200 is dense. Authored as a friendly integer range
    // so the editor's default spinbox step doesn't quietly round a sub-0.001
    // probability to zero.
    //
    // 0 keeps the row out of the area scan entirely.
    [Export(PropertyHint.Range, "0,5000,1,or_greater")] public float squareMetersPerSpawn;

    public bool RollAreaChance(Random rng)
    {
        if (squareMetersPerSpawn <= 0f)
        {
            return false;
        }
        return rng.NextDouble() * squareMetersPerSpawn < 1f;
    }
}
