using System.Collections.Generic;
using System.IO;

// CPU-side fog-of-war reveal buffers — the party's charted map (MapChart). R8
// bytes matching the minimap's exploration texture layout: one world-sized
// outdoor buffer plus a sparse per-slice-level dictionary for the indoor atlas.
// Reveal writes max(existing, falloff) (see MinimapTextures / MinimapSliceAtlas,
// which own the geometry and do the writes into these buffers). Allocated lazily
// on first reveal. Plain byte data — SaveGame-serializable.
public class ExplorationMask
{
    // Outdoor world-extent R8 buffer (OutdoorMetersPerPixel). Null until first
    // revealed. Sized by the minimap to MinimapTextures' exploration dimensions.
    public byte[] Outdoor;

    // Per-slice-level R8 buffers (IndoorMetersPerPixel, full XZ extent), keyed by
    // sliceLevel. Sparse — only slices the party has actually revealed exist.
    public readonly Dictionary<int, byte[]> Slices = new();

    public byte[] EnsureOutdoor(int size)
    {
        if (Outdoor == null || Outdoor.Length != size)
        {
            Outdoor = new byte[size];
        }
        return Outdoor;
    }

    public void Serialize(BinaryWriter w)
    {
        WriteBuffer(w, Outdoor);
        w.Write(Slices.Count);
        foreach (KeyValuePair<int, byte[]> kv in Slices)
        {
            w.Write(kv.Key);
            WriteBuffer(w, kv.Value);
        }
    }

    // A buffer whose size no longer matches the minimap's is re-allocated blank
    // by the next Ensure* — the world's extent changed under the save.
    public void Deserialize(BinaryReader r)
    {
        Outdoor = ReadBuffer(r);
        Slices.Clear();
        int slices = r.ReadInt32();
        for (int i = 0; i < slices; i++)
        {
            int level = r.ReadInt32();
            byte[] buffer = ReadBuffer(r);
            if (buffer != null)
            {
                Slices[level] = buffer;
            }
        }
    }

    private static void WriteBuffer(BinaryWriter w, byte[] buffer)
    {
        w.Write(buffer?.Length ?? -1);
        if (buffer != null)
        {
            w.Write(buffer);
        }
    }

    private static byte[] ReadBuffer(BinaryReader r)
    {
        int length = r.ReadInt32();
        return length < 0 ? null : r.ReadBytes(length);
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
}
