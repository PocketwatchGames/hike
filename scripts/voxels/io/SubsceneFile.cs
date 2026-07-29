using System.IO;
using System.Text;
using Godot;

// Packed subscene file format. Single subscene per file. Sized to the
// subscene's bbox rather than the world's chunk grid, so a 20×7×15 cottage
// stays a 20×7×15 blob instead of being padded out to two 16³ chunks.
//
// Layout:
//   Header
//     magic        : 4 bytes "HSCN"
//     version      : uint32
//     size         : Vector3I (12 bytes)         — voxel bbox dimensions
//     anchor       : Vector3  (12 bytes)         — placement reference, subscene-local
//     channelMask  : uint32                      — bit set per optional channel present
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
public static class SubsceneFile
{
    public const uint MAGIC = 0x4E435348; // 'HSCN' little-endian
    // v2: the entity list gained EntitySerializer's resource-path table prefix
    //     (see WorldFile v34) — subscenes share that serializer, so their wire
    //     layout moved with it.
    public const uint VERSION = 2;

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

        w.Write(MAGIC);
        w.Write(VERSION);
        WriteVec3I(w, sub.Size);
        WriteVec3(w, sub.Anchor);
        w.Write((uint)mask);

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

    public static SubsceneState Read(string path)
    {
        string osPath = ProjectSettings.GlobalizePath(path);
        using FileStream fs = File.OpenRead(osPath);
        using var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

        uint magic = r.ReadUInt32();
        if (magic != MAGIC)
        {
            throw new InvalidDataException($"Not a HSCN subscene file (magic = 0x{magic:X8})");
        }
        uint version = r.ReadUInt32();
        if (version != VERSION)
        {
            throw new InvalidDataException($"Unsupported HSCN subscene version {version}");
        }

        Vector3I size = ReadVec3I(r);
        Vector3 anchor = ReadVec3(r);
        var mask = (ChannelMask)r.ReadUInt32();

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

        sub.Entities = EntitySerializer.ReadList(r);
        return sub;
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
