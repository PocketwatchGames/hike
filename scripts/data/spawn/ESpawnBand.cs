using System;

// Where a column sits relative to the water, as the per-column spawn scans
// classify it. A SpawnListRow's `bands` is a mask of these; a column is exactly
// one of them, so a row spawns there when the column's band is in its mask.
//
// Classified by the same test that picks the ground's material — worldgen's
// beach test, the painter's shore band — so the rows that spawn on a beach are
// exactly the ones standing on its sand.
//
// Wire values are stable: append, never reassign.
[Flags]
public enum ESpawnBand
{
    None      = 0,
    Dry       = 1 << 0, // land above the shore band, and every dry cave / passage floor
    Shore     = 1 << 1, // the dry lip beside the water — a beach
    Submerged = 1 << 2, // inside water deep enough to swim in, open or in a cave

    Land = Dry | Shore,
}

public static class SpawnBands
{
    // Voxels of water a Submerged column needs: a swimmer has to fit in it.
    // Shallower water is no band at all, so nothing spawns there.
    public const int SUBMERGED_MIN_DEPTH = 2;
}
