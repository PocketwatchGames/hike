using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

// Packed world file format. Per-chunk addressable so a future streaming
// loader can seek to a single chunk without changing the format.
//
// Layout:
//   Header
//     magic        : 4 bytes "HIKE"
//     version      : uint32
//     min          : Vector3I (3 * int32 = 12 bytes)
//     max          : Vector3I (12 bytes)
//     spawn        : Vector3  (3 * float32 = 12 bytes)
//     simDataPath  : length-prefixed string (resource path, may be empty)
//     chunkCount   : uint32
//   Index : chunkCount entries
//     coord        : Vector3I (12 bytes)
//     offset       : uint64    // absolute byte offset of this chunk's payload
//     length       : uint32    // payload length in bytes
//   Payload : concatenated chunk blobs (see ChunkSerializer)
public static class WorldFile
{
    public const uint MAGIC = 0x454B4948; // 'HIKE' little-endian
    // v4: chunk payload gained a fog-density byte array after sunlight.
    public const uint VERSION = 4;

    public struct IndexEntry
    {
        public Vector3I Coord;
        public ulong Offset;
        public uint Length;
    }

    public struct Header
    {
        public Vector3I Min;
        public Vector3I Max;
        public Vector3 Spawn;
        public string SimDataPath;
        public uint ChunkCount;
    }

    // Writes every chunk in `worldState` to `path`. Used by the world_export
    // CVar to convert a procedurally-generated WorldState into a file.
    public static void Write(string path, WorldState worldState)
    {
        string osPath = ProjectSettings.GlobalizePath(path);
        string dir = Path.GetDirectoryName(osPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Stable ordering keeps file output deterministic for diffing/testing.
        var coords = new List<Vector3I>(worldState._chunks.Keys);
        coords.Sort((a, b) =>
        {
            int c = a.X.CompareTo(b.X);
            if (c != 0) { return c; }
            c = a.Y.CompareTo(b.Y);
            if (c != 0) { return c; }
            return a.Z.CompareTo(b.Z);
        });

        // Serialize each chunk's blob into a buffer first so we know its length
        // before writing the index. Memory cost is bounded by total world size
        // and this is an offline export tool, not a hot path.
        var blobs = new List<byte[]>(coords.Count);
        foreach (Vector3I coord in coords)
        {
            ChunkState chunk = worldState._chunks[coord];
            List<EntitySimState> entities = worldState.GetEntities(coord);
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            ChunkSerializer.Write(bw, chunk, entities);
            bw.Flush();
            blobs.Add(ms.ToArray());
        }

        using FileStream fs = File.Create(osPath);
        using var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

        // --- Header ---
        w.Write(MAGIC);
        w.Write(VERSION);
        WriteVec3I(w, worldState.Min);
        WriteVec3I(w, worldState.Max);
        w.Write(worldState.Spawn.X);
        w.Write(worldState.Spawn.Y);
        w.Write(worldState.Spawn.Z);
        w.Write(worldState.SimData != null ? worldState.SimData.ResourcePath : "");
        w.Write((uint)coords.Count);

        // --- Index ---
        // Index entries are fixed size (12 + 8 + 4 = 24 bytes), so the payload
        // start offset is deterministic from the header.
        const int INDEX_ENTRY_SIZE = 12 + 8 + 4;
        long headerEnd = fs.Position;
        long payloadStart = headerEnd + (long)coords.Count * INDEX_ENTRY_SIZE;
        ulong runningOffset = (ulong)payloadStart;
        for (int i = 0; i < coords.Count; i++)
        {
            WriteVec3I(w, coords[i]);
            w.Write(runningOffset);
            w.Write((uint)blobs[i].Length);
            runningOffset += (ulong)blobs[i].Length;
        }

        // --- Payload ---
        for (int i = 0; i < blobs.Count; i++)
        {
            w.Write(blobs[i]);
        }
    }

    public static Header ReadHeader(BinaryReader r)
    {
        uint magic = r.ReadUInt32();
        if (magic != MAGIC)
        {
            throw new InvalidDataException($"Not a HIKE world file (magic = 0x{magic:X8})");
        }
        uint version = r.ReadUInt32();
        if (version != VERSION)
        {
            throw new InvalidDataException($"Unsupported HIKE world file version {version}");
        }

        var header = new Header
        {
            Min = ReadVec3I(r),
            Max = ReadVec3I(r),
            Spawn = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            SimDataPath = r.ReadString(),
            ChunkCount = r.ReadUInt32(),
        };
        return header;
    }

    public static IndexEntry ReadIndexEntry(BinaryReader r)
    {
        return new IndexEntry
        {
            Coord = ReadVec3I(r),
            Offset = r.ReadUInt64(),
            Length = r.ReadUInt32(),
        };
    }

    private static void WriteVec3I(BinaryWriter w, Vector3I v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3I ReadVec3I(BinaryReader r)
    {
        int x = r.ReadInt32();
        int y = r.ReadInt32();
        int z = r.ReadInt32();
        return new Vector3I(x, y, z);
    }
}
