using System.IO;
using System.Text;
using Godot;

// Packed subscene file format. Single subscene per file. Sized to the
// subscene's bbox rather than the world's chunk grid, so a 20×7×15 cottage
// stays a 20×7×15 blob instead of being padded out to two 16³ chunks.
//
// Layout:
//   Header (fixed size — HEADER_BYTES)
//     magic        : 4 bytes "HSCN"
//     version      : uint32
//     size         : Vector3I (12 bytes)         — voxel bbox dimensions
//     anchor       : Vector3  (12 bytes)         — placement reference, subscene-local
//     channelMask  : uint32                      — bit set per optional channel present
//     [v6+] dirLength : uint32                   — bytes of the directory block below
//   [v6+] Directory (dirLength bytes)
//     tagCount     : 7-bit int, then (string tag, 7-bit int count) per pool
//   Body (in fixed order; arrays sized to size.X * size.Y * size.Z unless noted)
//     voxels         : SX*SY*SZ bytes (VoxelType row-major X,Y,Z)
//     shape          : SX*SY*SZ bytes (SharpAxes byte per cell)
//     terrainId      : SX*SY*SZ bytes
//     overlayId      : SX*SY*SZ bytes
//     detailGroup    : SX*SY*SZ bytes
//     detailStrength : SX*SY*SZ bytes
//     presenceMask   : ceil(SX*SY*SZ / 8) bytes  — 1 bit per cell, MSB-first within each byte
//     [if WIND   bit] windFactor : EX*EY*EZ bytes (env-subgrid resolution; ENV_VOXELS_PER_CELL voxels per cell)
//     [if ENVTAG bit] envTag     : EX*EY*EZ bytes
//     entities      : type-tagged list (see EntitySerializer; positions are subscene-local)
//
// Wire-format additions: append new optional channels by allocating a new
// CHANNEL_* bit and appending the read/write at the end of the existing
// channel block (before entities). Mandatory channels can ONLY be appended
// at the end of the body, never inserted; bump VERSION when you do so.
//
// The directory is the one exception to that rule, and it earns it by sitting
// at a known offset: ReadDirectory seeks straight to it and reads nothing else,
// which is what lets the variant inspector query a scene's pools without
// decoding a dungeon's worth of voxels. Version-gated, so pre-v6 files (which
// have no tags to summarize) still read.
public static class SubsceneFile
{
    // Where authored subscenes live. Under res:// because worldgen's
    // SubscenePlacement references them by res:// path.
    public const string DEFAULT_SCENE_DIR = "res://resources/data/subscenes/";

    public const uint MAGIC = 0x4E435348; // 'HSCN' little-endian
    // v2: the entity list gained EntitySerializer's resource-path table prefix
    //     (see WorldFile v34) — subscenes share that serializer, so their wire
    //     layout moved with it.
    // v4: Roof entity payload gained a trailing per-instance `broken` float.
    //     v3 subscenes are still read — their roofs simply load intact.
    // v5: Roof entity payload gained a trailing form byte (gable / hip). v4 and
    //     earlier subscenes still read — their roofs load as gables.
    // v6: entities gained a trailing variant pool tag, and the header gained a
    //     directory summarizing those tags. v5 and earlier still read — their
    //     entities load untagged, i.e. unconditional, which is what they were.
    public const uint VERSION = 6;

    // Bytes before the directory block: magic + version + size + anchor +
    // channelMask + dirLength. ReadDirectory seeks past exactly this much.
    private const int HEADER_BYTES = 4 + 4 + 12 + 12 + 4 + 4;

    [System.Flags]
    public enum ChannelMask : uint
    {
        None = 0,
        Wind = 1u << 0,
        EnvTag = 1u << 1,
    }

    public static void Write(string path, SubsceneState sub)
    {
        string osPath = ProjectSettings.GlobalizePath(path);
        string dir = Path.GetDirectoryName(osPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using FileStream fs = File.Create(osPath);
        using var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

        ChannelMask mask = ChannelMask.None;
        if (sub.WindFactor != null) { mask |= ChannelMask.Wind; }
        if (sub.EnvTag != null) { mask |= ChannelMask.EnvTag; }

        // Buffered so its length can precede it — the length is what makes the
        // block skippable by a full read and bounded by a directory-only read.
        byte[] directory = EncodeDirectory(SubsceneDirectory.FromEntities(sub.Entities));

        w.Write(MAGIC);
        w.Write(VERSION);
        WriteVec3I(w, sub.Size);
        WriteVec3(w, sub.Anchor);
        w.Write((uint)mask);
        w.Write((uint)directory.Length);
        w.Write(directory);

        Vector3I size = sub.Size;
        WriteVoxelChannel(w, sub.Voxels, size);
        WriteByteChannel(w, sub.Shape, size);
        WriteByteChannel(w, sub.TerrainId, size);
        WriteByteChannel(w, sub.OverlayId, size);
        WriteByteChannel(w, sub.DetailGroup, size);
        WriteByteChannel(w, sub.DetailStrength, size);
        WritePresenceMask(w, sub.PresenceMask, size);

        if ((mask & ChannelMask.Wind) != 0)
        {
            WriteByteChannel(w, sub.WindFactor, sub.EnvSize);
        }
        if ((mask & ChannelMask.EnvTag) != 0)
        {
            WriteByteChannel(w, sub.EnvTag, sub.EnvSize);
        }

        EntitySerializer.WriteList(w, sub.Entities);
    }

    // Reads through Godot's FileAccess, not System.IO: in an exported build a
    // `res://` subscene lives inside the .pck, where there is no OS path for
    // File.OpenRead to open. Whole-file read is fine — subscenes are bbox-sized
    // blobs, not the chunk-addressable world format.
    public static SubsceneState Read(string path)
    {
        byte[] bytes = Godot.FileAccess.GetFileAsBytes(path);
        if (bytes == null || bytes.Length == 0)
        {
            throw new IOException($"could not read '{path}' ({Godot.FileAccess.GetOpenError()})");
        }
        using var ms = new MemoryStream(bytes);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);

        uint magic = r.ReadUInt32();
        if (magic != MAGIC)
        {
            throw new InvalidDataException($"Not a HSCN subscene file (magic = 0x{magic:X8})");
        }
        uint version = r.ReadUInt32();
        // v2 and v3 are still readable. v2 entities carry no trailing RotationY
        // (they load facing zero); v3 roofs carry no trailing `broken` (they load
        // intact). Both pick the field up the next time the scene is saved.
        // Anything older is gone.
        const uint MIN_READABLE_VERSION = 2;
        if (version > VERSION || version < MIN_READABLE_VERSION)
        {
            throw new InvalidDataException($"Unsupported HSCN subscene version {version}");
        }

        Vector3I size = ReadVec3I(r);
        Vector3 anchor = ReadVec3(r);
        var mask = (ChannelMask)r.ReadUInt32();
        if (version >= 6)
        {
            // Skipped, not parsed: a full read reconstructs the entities the
            // directory summarizes, so re-deriving it costs nothing and can't
            // disagree with them.
            uint directoryLength = r.ReadUInt32();
            r.BaseStream.Seek(directoryLength, SeekOrigin.Current);
        }

        var sub = new SubsceneState(size) { Anchor = anchor };
        ReadVoxelChannel(r, sub.Voxels, size);
        ReadByteChannel(r, sub.Shape, size);
        ReadByteChannel(r, sub.TerrainId, size);
        ReadByteChannel(r, sub.OverlayId, size);
        ReadByteChannel(r, sub.DetailGroup, size);
        ReadByteChannel(r, sub.DetailStrength, size);
        ReadPresenceMask(r, sub.PresenceMask, size);

        if ((mask & ChannelMask.Wind) != 0)
        {
            sub.EnsureWindFactor();
            ReadByteChannel(r, sub.WindFactor, sub.EnvSize);
        }
        if ((mask & ChannelMask.EnvTag) != 0)
        {
            sub.EnsureEnvTag();
            ReadByteChannel(r, sub.EnvTag, sub.EnvSize);
        }

        int roofFormat = version >= 5 ? EntitySerializer.ROOF_FORMAT_FORM
            : version >= 4 ? EntitySerializer.ROOF_FORMAT_BROKEN
            : EntitySerializer.ROOF_FORMAT_ORIGINAL;
        sub.Entities = EntitySerializer.ReadList(r, shared: null, hasRotation: version >= 3, roofFormat: roofFormat, hasTag: version >= 6);
        return sub;
    }

    // The scene's variant pools, without decoding its voxel body. Reads the
    // fixed header plus the directory block and stops — the point is that the
    // authoring inspector can query a scene cheaply and often. Returns an empty
    // directory for a pre-v6 file (no tags existed) or one that can't be read.
    public static SubsceneDirectory ReadDirectory(string path)
    {
        using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
        {
            GD.PrintErr($"SubsceneFile: could not open '{path}' ({Godot.FileAccess.GetOpenError()})");
            return new SubsceneDirectory();
        }

        byte[] head = file.GetBuffer(HEADER_BYTES);
        if (head.Length < HEADER_BYTES)
        {
            return new SubsceneDirectory();
        }
        using var headStream = new MemoryStream(head);
        using var headReader = new BinaryReader(headStream, Encoding.UTF8, leaveOpen: false);
        if (headReader.ReadUInt32() != MAGIC || headReader.ReadUInt32() < 6)
        {
            return new SubsceneDirectory();
        }
        headStream.Seek(12 + 12 + 4, SeekOrigin.Current); // size + anchor + channelMask
        uint directoryLength = headReader.ReadUInt32();

        byte[] block = file.GetBuffer((long)directoryLength);
        if (block.Length < directoryLength)
        {
            return new SubsceneDirectory();
        }
        using var blockStream = new MemoryStream(block);
        using var blockReader = new BinaryReader(blockStream, Encoding.UTF8, leaveOpen: false);
        return DecodeDirectory(blockReader);
    }

    private static byte[] EncodeDirectory(SubsceneDirectory directory)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write7BitEncodedInt(directory.Entries.Length);
            foreach (SubsceneDirectory.Entry entry in directory.Entries)
            {
                w.Write(entry.Tag ?? "");
                w.Write7BitEncodedInt(entry.Count);
            }
        }
        return ms.ToArray();
    }

    private static SubsceneDirectory DecodeDirectory(BinaryReader r)
    {
        int count = r.Read7BitEncodedInt();
        var directory = new SubsceneDirectory { Entries = new SubsceneDirectory.Entry[count] };
        for (int i = 0; i < count; i++)
        {
            directory.Entries[i] = new SubsceneDirectory.Entry
            {
                Tag = r.ReadString(),
                Count = r.Read7BitEncodedInt(),
            };
        }
        return directory;
    }

    private static void WriteVoxelChannel(BinaryWriter w, VoxelType[,,] arr, Vector3I size)
    {
        for (int x = 0; x < size.X; x++)
        {
            for (int y = 0; y < size.Y; y++)
            {
                for (int z = 0; z < size.Z; z++)
                {
                    w.Write((byte)arr[x, y, z]);
                }
            }
        }
    }

    private static void ReadVoxelChannel(BinaryReader r, VoxelType[,,] arr, Vector3I size)
    {
        for (int x = 0; x < size.X; x++)
        {
            for (int y = 0; y < size.Y; y++)
            {
                for (int z = 0; z < size.Z; z++)
                {
                    arr[x, y, z] = (VoxelType)r.ReadByte();
                }
            }
        }
    }

    private static void WriteByteChannel(BinaryWriter w, byte[,,] arr, Vector3I size)
    {
        for (int x = 0; x < size.X; x++)
        {
            for (int y = 0; y < size.Y; y++)
            {
                for (int z = 0; z < size.Z; z++)
                {
                    w.Write(arr[x, y, z]);
                }
            }
        }
    }

    private static void ReadByteChannel(BinaryReader r, byte[,,] arr, Vector3I size)
    {
        for (int x = 0; x < size.X; x++)
        {
            for (int y = 0; y < size.Y; y++)
            {
                for (int z = 0; z < size.Z; z++)
                {
                    arr[x, y, z] = r.ReadByte();
                }
            }
        }
    }

    private static void WritePresenceMask(BinaryWriter w, bool[,,] mask, Vector3I size)
    {
        int total = size.X * size.Y * size.Z;
        int byteCount = (total + 7) / 8;
        var packed = new byte[byteCount];
        int bit = 0;
        for (int x = 0; x < size.X; x++)
        {
            for (int y = 0; y < size.Y; y++)
            {
                for (int z = 0; z < size.Z; z++)
                {
                    if (mask[x, y, z])
                    {
                        packed[bit >> 3] |= (byte)(0x80 >> (bit & 7));
                    }
                    bit++;
                }
            }
        }
        w.Write(packed);
    }

    private static void ReadPresenceMask(BinaryReader r, bool[,,] mask, Vector3I size)
    {
        int total = size.X * size.Y * size.Z;
        int byteCount = (total + 7) / 8;
        byte[] packed = r.ReadBytes(byteCount);
        if (packed.Length != byteCount)
        {
            throw new EndOfStreamException("Truncated subscene presence mask");
        }
        int bit = 0;
        for (int x = 0; x < size.X; x++)
        {
            for (int y = 0; y < size.Y; y++)
            {
                for (int z = 0; z < size.Z; z++)
                {
                    mask[x, y, z] = (packed[bit >> 3] & (0x80 >> (bit & 7))) != 0;
                    bit++;
                }
            }
        }
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

    private static void WriteVec3(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3 ReadVec3(BinaryReader r)
    {
        float x = r.ReadSingle();
        float y = r.ReadSingle();
        float z = r.ReadSingle();
        return new Vector3(x, y, z);
    }
}
