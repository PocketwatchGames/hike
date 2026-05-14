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
//     zoneCount  : uint32
//     zones      : zoneCount entries
//       dataPath        : length-prefixed string (ZoneData resource path)
//       windDirection   : Vector3 (12 bytes)
//       elevation       : float32 (4 bytes)
//     regionCount : uint32
//     regions     : regionCount entries
//       dataPath        : length-prefixed string (RegionData resource path;
//                         empty string for border slots)
//     chunkCount   : uint32
//   Index : chunkCount entries
//     coord        : Vector3I (12 bytes)
//     offset       : uint64    // absolute byte offset of this chunk's payload
//     length       : uint32    // payload length in bytes
//   Payload : concatenated chunk blobs (see ChunkSerializer)
public static class WorldFile
{
    public const uint MAGIC = 0x454B4948; // 'HIKE' little-endian
    // v5: chunk payload gained a per-voxel Shape byte (SharpAxes) channel between
    //     Voxels and Sunlight, plus a fog-density byte array after Sunlight.
    // v6: chunk payload appended a per-voxel TerrainId byte (index into the
    //     world's TerrainData[]) after fog-density, before entities.
    // v7: chunk payload appended a per-voxel OverlayId byte after TerrainId.
    // v8: chunk payload appended per-voxel DetailGroup + DetailStrength bytes
    //     after OverlayId — painted detail-sprite scatter (grass/flowers/etc).
    // v9: header gained a zones table (data path + windDirection + elevation
    //     per zone); chunk payload appended a 1-byte ZoneIndex selecting
    //     a zone from that table.
    // v10: chunk payload appended a coarse windFactor subgrid (4³ bytes per
    //      chunk) before zoneIndex — drives the wind_map 3D shader global,
    //      damps water/foliage/audio in caves and indoors.
    // v11: chunk payload appended a coarse envTag subgrid (4³ bytes per
    //      chunk, EnvironmentTag enum) after windFactor, before zoneIndex
    //      — drives audio reverb-bus blending and outdoor-layer attenuation.
    // v12: Mob entity payload appended a SpawnAtNight bool (after
    //      InitialBehavior) — surface goblins are flagged so their nodes only
    //      activate when the chunk loads at night.
    // v13: Torch entity payload appended an AutoLightAtNight bool (after
    //      Active) — surface campfires are flagged so they ignite when their
    //      chunk activates after dark.
    // v14: Chest entity payload appended a SpawnAtNight bool (after Active) —
    //      campfire-encampment chests are flagged so they only materialize when
    //      the chunk activates after dark.
    // v15: header gained a regions table (RegionData resource path per region);
    //      chunk payload appended a 1-byte RegionIndex selecting an entry from
    //      that table after ZoneIndex. Regions are an independent top-level
    //      subdivision from zones — a single named region can span multiple
    //      biomes, and the zone field used to double as the region anchor.
    // v16: chunk payload appended two coarse current subgrids (4³ bytes each:
    //      currentX then currentZ) between envTag and zoneIndex — drives the
    //      water_current_map 3D shader global, advecting ripple normals on
    //      the water surface to visualize streams/rivers/tidal flow.
    public const uint VERSION = 16;

    public struct IndexEntry
    {
        public Vector3I Coord;
        public ulong Offset;
        public uint Length;
    }

    public struct ZoneEntry
    {
        public string DataPath;
        public Vector3 WindDirection;
        public float Elevation;
    }

    public struct RegionEntry
    {
        public string DataPath;
    }

    public struct Header
    {
        public Vector3I Min;
        public Vector3I Max;
        public Vector3 Spawn;
        public string SimDataPath;
        public ZoneEntry[] Zones;
        public RegionEntry[] Regions;
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
        ZoneState[] zones = worldState.Zones ?? [];
        w.Write((uint)zones.Length);
        for (int i = 0; i < zones.Length; i++)
        {
            w.Write(zones[i].Data != null ? zones[i].Data.ResourcePath : "");
            w.Write(zones[i].WindDirection.X);
            w.Write(zones[i].WindDirection.Y);
            w.Write(zones[i].WindDirection.Z);
            w.Write(zones[i].Elevation);
        }
        RegionState[] regions = worldState.Regions ?? [];
        w.Write((uint)regions.Length);
        for (int i = 0; i < regions.Length; i++)
        {
            w.Write(regions[i].Data != null ? regions[i].Data.ResourcePath : "");
        }
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
        };
        uint zoneCount = r.ReadUInt32();
        header.Zones = new ZoneEntry[zoneCount];
        for (uint i = 0; i < zoneCount; i++)
        {
            header.Zones[i] = new ZoneEntry
            {
                DataPath = r.ReadString(),
                WindDirection = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                Elevation = r.ReadSingle(),
            };
        }
        uint regionCount = r.ReadUInt32();
        header.Regions = new RegionEntry[regionCount];
        for (uint i = 0; i < regionCount; i++)
        {
            header.Regions[i] = new RegionEntry { DataPath = r.ReadString() };
        }
        header.ChunkCount = r.ReadUInt32();
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
