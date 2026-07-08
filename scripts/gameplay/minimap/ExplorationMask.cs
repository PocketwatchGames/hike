using System.Collections.Generic;

// CPU-side fog-of-war reveal buffers for one Knowledge store — the map a
// character has personally charted. R8 bytes matching the minimap's exploration
// texture layout: one world-sized outdoor buffer plus a sparse per-slice-level
// dictionary for the indoor atlas. Reveal writes max(existing, falloff) (see
// MinimapTextures / MinimapSliceAtlas, which own the geometry and do the writes
// into these buffers).
//
// Two live per run, mirroring the teachable-concept split: the permanent party
// pool (Party.Knowledge.Exploration) and the active member's provisional field
// buffer (PlayerState.Knowledge.Exploration). The minimap displays
// max(party, active) — the controlled player's un-banked reveal shows there
// immediately — while the world map displays the party pool only. Banking at a
// campfire folds the active buffer into the party pool (Knowledge.MergeFrom →
// ExplorationMask.MergeFrom) and clears it, so the reveal graduates onto the
// world map. Buffers are allocated lazily on first reveal, so an unexplored
// member/pool costs nothing. Plain byte data — SaveGame-serializable.
public class ExplorationMask
{
    // Outdoor world-extent R8 buffer (OutdoorMetersPerPixel). Null until first
    // revealed. Sized by the minimap to MinimapTextures' exploration dimensions.
    public byte[] Outdoor;

    // Per-slice-level R8 buffers (IndoorMetersPerPixel, full XZ extent), keyed by
    // sliceLevel. Sparse — only slices the character has actually revealed exist.
    public readonly Dictionary<int, byte[]> Slices = new();

    public byte[] EnsureOutdoor(int size)
    {
        if (Outdoor == null || Outdoor.Length != size)
        {
            Outdoor = new byte[size];
        }
        return Outdoor;
    }

    public byte[] EnsureSlice(int sliceLevel, int size)
    {
        if (!Slices.TryGetValue(sliceLevel, out byte[] buffer) || buffer.Length != size)
        {
            buffer = new byte[size];
            Slices[sliceLevel] = buffer;
        }
        return buffer;
    }

    // Fold `other` into this buffer set (per-pixel max). Used to bank a member's
    // field reveal into the permanent party pool.
    public void MergeFrom(ExplorationMask other)
    {
        if (other == null)
        {
            return;
        }
        if (other.Outdoor != null)
        {
            MaxInto(EnsureOutdoor(other.Outdoor.Length), other.Outdoor);
        }
        foreach (KeyValuePair<int, byte[]> kv in other.Slices)
        {
            if (kv.Value == null)
            {
                continue;
            }
            MaxInto(EnsureSlice(kv.Key, kv.Value.Length), kv.Value);
        }
    }

    public void Clear()
    {
        Outdoor = null;
        Slices.Clear();
    }

    static void MaxInto(byte[] dst, byte[] src)
    {
        int n = System.Math.Min(dst.Length, src.Length);
        for (int i = 0; i < n; i++)
        {
            if (src[i] > dst[i])
            {
                dst[i] = src[i];
            }
        }
    }
}
