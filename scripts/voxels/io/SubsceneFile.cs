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
//     voxels         : SX*SY*SZ bytes (int row-major X,Y,Z)
//     shape          : SX*SY*SZ bytes (SharpAxes byte per cell)
//     terrainId      : SX*SY*SZ bytes
//     overlayId      : SX*SY*SZ bytes
//     detailGroup    : SX*SY*SZ bytes
//     detailStrength : SX*SY*SZ bytes
//     presenceMask   : ceil(SX*SY*SZ / 8) bytes  — 1 bit per cell, MSB-first within each byte
//     [if WIND   bit] windFactor : EX*EY*EZ bytes (env-subgrid resolution; ENV_VOXELS_PER_CELL voxels per cell)
//     [if ENVTAG bit] envTag     : EX*EY*EZ bytes
//     [if OVERLAYFACES bit] overlayFaces : SX*SY*SZ bytes (EVoxelFace bits per
//                    cell, 0 = all faces)
//
// The ENTITIES are not in this file. They are spawn-entry placements, in a
// `.tres` beside it (SubsceneEntities.PathFor): `house01.hikescene` +
// `house01.entities.tres` are one document, written and read together.
//
// Wire-format additions: append new optional channels by allocating a new
// CHANNEL_* bit and appending the read/write at the end of the existing
// channel block. Mandatory channels can ONLY be appended
// at the end of the body, never inserted; bump VERSION when you do so.
//
// The directory is the one exception to that rule, and it earns it by sitting
// at a known offset: ReadDirectory seeks straight to it and reads nothing else,
// which is what lets the variant inspector query a scene's pools without
// decoding a dungeon's worth of voxels.
public static class SubsceneFile
{
    // Where authored subscenes live. Under res:// because worldgen's
    // SubscenePlacement references them by res:// path.
    public const string DEFAULT_SCENE_DIR = "res://resources/data/world_authoring/subscenes/";

    // A scene only one world stamps lives in that world's folder, under this
    // (WorldScope.RootOf(world) + WORLD_SCENE_SUBDIR); the painter offers it
    // only in that world.
    public const string WORLD_SCENE_SUBDIR = "subscenes/";

    public const uint MAGIC = 0x4E435348; // 'HSCN' little-endian
    // v21: entities are spawn-entry placements in the `.entities.tres` beside
    //      the scene, not serialized states in its body. Nothing older reads:
    //      every scene was converted when the format changed.
    public const uint VERSION = 21;

    // Bytes before the directory block: magic + version + size + anchor +
    // channelMask + dirLength. ReadDirectory seeks past exactly this much.
    private const int HEADER_BYTES = 4 + 4 + 12 + 12 + 4 + 4;

    [System.Flags]
    public enum ChannelMask : uint
    {
        None = 0,
        // 1u << 0 was a wind / interiorness slot. Do not reuse the bit.
        EnvTag = 1u << 1,
        OverlayFaces = 1u << 2,
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
        if (sub.EnvTag != null) { mask |= ChannelMask.EnvTag; }
        if (sub.OverlayFaces != null) { mask |= ChannelMask.OverlayFaces; }

        // Buffered so its length can precede it — the length is what makes the
        // block skippable by a full read and bounded by a directory-only read.
        byte[] directory = EncodeDirectory(SubsceneDirectory.FromEntities(sub.Entities));
        // First, so a scene whose entities cannot be written leaves the old
        // pair on disk rather than a new body beside stale entities.
        WriteEntities(path, sub);

        w.Write(MAGIC);
        w.Write(VERSION);
        WriteVec3I(w, sub.Size);
        WriteVec3(w, sub.Anchor);
        w.Write((uint)mask);
        w.Write((uint)directory.Length);
        w.Write(directory);

        Vector3I size = sub.Size;
        WriteByteChannel(w, sub.Voxels, size);
        WriteByteChannel(w, sub.Shape, size);
        WriteByteChannel(w, sub.TerrainId, size);
        WriteByteChannel(w, sub.OverlayId, size);
        WriteByteChannel(w, sub.DetailGroup, size);
        WriteByteChannel(w, sub.DetailStrength, size);
        WritePresenceMask(w, sub.PresenceMask, size);

        if ((mask & ChannelMask.EnvTag) != 0)
        {
            WriteByteChannel(w, sub.EnvTag, sub.EnvSize);
        }
        if ((mask & ChannelMask.OverlayFaces) != 0)
        {
            WriteByteChannel(w, sub.OverlayFaces, size);
        }
    }

    // The placements, as the `.tres` beside the scene. Ids are assigned here to
    // any placement that has none (a fresh one), from the counter the file
    // carries, so an id is never handed out twice.
    private static void WriteEntities(string scenePath, SubsceneState sub)
    {
        string docPath = SubsceneEntities.PathFor(scenePath);
        int next = Mathf.Max(1, sub.NextEntityId);
        var placements = new SceneEntityPlacement[sub.Entities.Count];
        var used = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < placements.Length; i++)
        {
            SceneEntity entity = sub.Entities[i];
            SceneEntityPlacement p = entity.Placement;
            // A turned state's entities stand somewhere their placements do not
            // say; writing one would save the turn into the scene.
            if (entity.Position != p.position || !Mathf.IsEqualApprox(entity.RotationY, p.rotationY))
            {
                throw new System.InvalidOperationException("a rotated scene cannot be written back.");
            }
            if (p.id > 0 && !used.Add(p.id))
            {
                throw new System.InvalidOperationException($"two entities share id {p.id}.");
            }
            next = Mathf.Max(next, p.id + 1);
            placements[i] = p;
        }
        foreach (SceneEntityPlacement p in placements)
        {
            if (p.id <= 0)
            {
                p.id = next++;
            }
            EmbeddedResources.DetachFromOtherFiles(p, docPath);
        }
        var doc = new SubsceneEntities { placements = placements, nextId = next };
        Error err = ResourceSaver.Save(doc, docPath);
        if (err != Error.Ok)
        {
            throw new IOException($"could not write '{docPath}' ({err})");
        }
        sub.NextEntityId = next;
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
        if (version != VERSION)
        {
            throw new InvalidDataException($"'{path}' is subscene version {version}; this build reads only {VERSION}.");
        }

        Vector3I size = ReadVec3I(r);
        Vector3 anchor = ReadVec3(r);
        var mask = (ChannelMask)r.ReadUInt32();
        // Skipped, not parsed: a full read reconstructs the entities the
        // directory summarizes, so re-deriving it costs nothing and can't
        // disagree with them.
        uint directoryLength = r.ReadUInt32();
        r.BaseStream.Seek(directoryLength, SeekOrigin.Current);

        var sub = new SubsceneState(size) { Anchor = anchor };
        ReadByteChannel(r, sub.Voxels, size);
        ReadByteChannel(r, sub.Shape, size);
        ReadByteChannel(r, sub.TerrainId, size);
        ReadByteChannel(r, sub.OverlayId, size);
        ReadByteChannel(r, sub.DetailGroup, size);
        ReadByteChannel(r, sub.DetailStrength, size);
        ReadPresenceMask(r, sub.PresenceMask, size);

        if ((mask & ChannelMask.EnvTag) != 0)
        {
            sub.EnsureEnvTag();
            ReadByteChannel(r, sub.EnvTag, sub.EnvSize);
        }
        if ((mask & ChannelMask.OverlayFaces) != 0)
        {
            sub.EnsureOverlayFaces();
            ReadByteChannel(r, sub.OverlayFaces, size);
        }

        ReadEntities(path, sub);
        return sub;
    }

    private static void ReadEntities(string scenePath, SubsceneState sub)
    {
        string docPath = SubsceneEntities.PathFor(scenePath);
        if (!ResourceLoader.Exists(docPath))
        {
            throw new IOException($"'{scenePath}' has no entity file beside it ('{docPath}').");
        }
        // Ignore, not Reuse: the editor rewrites this file in-session, and a
        // cached copy would reopen the scene as it was before the save. The
        // palette entries it names still load through the cache (Godot's rule
        // for a dependency), so a placement's `source` IS the palette file.
        var doc = ResourceLoader.Load<SubsceneEntities>(docPath, null, ResourceLoader.CacheMode.Ignore)
            ?? throw new IOException($"'{docPath}' did not load as SubsceneEntities.");
        foreach (SceneEntityPlacement p in doc.placements ?? System.Array.Empty<SceneEntityPlacement>())
        {
            if (p != null)
            {
                sub.Entities.Add(new SceneEntity(p));
            }
        }
        sub.NextEntityId = doc.nextId;
    }

    // The scene's variant pools, without decoding its voxel body. Reads the
    // fixed header plus the directory block and stops — the point is that the
    // authoring inspector can query a scene cheaply and often. Returns an empty
    // directory for a file that can't be read.
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
        if (headReader.ReadUInt32() != MAGIC || headReader.ReadUInt32() != VERSION)
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
