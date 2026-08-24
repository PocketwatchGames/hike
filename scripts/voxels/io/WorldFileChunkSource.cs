using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

// IChunkSource backed by a single packed world file. Constructor reads the
// header and full index into memory; TryLoadChunk seeks to the chunk's payload
// and decodes it. The file handle stays open for the lifetime of the source.
public sealed class WorldFileChunkSource : IChunkSource
{
    public Vector3I Min { get; }
    public Vector3I Max { get; }
    public Vector3 Spawn { get; }
    public SimData SimData { get; }

    // The WorldGenData whose scriptData / startingParty / initialKnowledge a run
    // in this world begins with. Null when the file was baked without one.
    public WorldGenData StartContent { get; }
    public ZoneState[] Zones { get; }

    // The kit palette this file was baked against, one resource path per slot.
    // Main.LoadWorldFromFile checks it against the palette the world is about to
    // be read with — see WorldFile VERSION v46.
    public string[] KitSlots { get; }

    // Detail-palette slots, same contract — DetailGroup bytes index this.
    public string[] DetailSlots { get; }
    public RegionState[] Regions { get; }
    // Non-chunked always-resident entity states (the player's companion), read
    // from the world file's global section. Main.LoadWorldFromFile files these
    // into WorldState.PersistentEntities rather than a per-chunk bucket.
    public List<EntitySimState> PersistentEntities { get; }

    private readonly Dictionary<Vector3I, WorldFile.IndexEntry> _index;
    private readonly FileStream _stream;
    private readonly object _lock = new();
    // File-wide resource-path table from the header; every chunk's entity list
    // resolves its path indices against it.
    private readonly EntitySerializer.ReadPathTable _pathTable;

    public WorldFileChunkSource(string path)
    {
        string osPath = ProjectSettings.GlobalizePath(path);
        _stream = File.Open(osPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        var r = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);

        WorldFile.Header header = WorldFile.ReadHeader(r);
        _pathTable = header.PathTable;
        Min = header.Min;
        Max = header.Max;
        Spawn = header.Spawn;
        SimData = string.IsNullOrEmpty(header.SimDataPath) ? null : GD.Load<SimData>(header.SimDataPath);
        StartContent = string.IsNullOrEmpty(header.StartContentPath)
            ? null
            : GD.Load<WorldGenData>(header.StartContentPath);
        KitSlots = header.KitSlots ?? System.Array.Empty<string>();
        DetailSlots = header.DetailSlots ?? System.Array.Empty<string>();

        Zones = new ZoneState[header.Zones.Length];
        for (int i = 0; i < header.Zones.Length; i++)
        {
            WorldFile.ZoneEntry entry = header.Zones[i];
            Zones[i] = new ZoneState
            {
                Data = string.IsNullOrEmpty(entry.DataPath) ? null : GD.Load<ZoneData>(entry.DataPath),
                WindDirection = entry.WindDirection,
                Elevation = entry.Elevation,
            };
        }

        Regions = new RegionState[header.Regions.Length];
        for (int i = 0; i < header.Regions.Length; i++)
        {
            WorldFile.RegionEntry entry = header.Regions[i];
            Regions[i] = new RegionState
            {
                Data = string.IsNullOrEmpty(entry.DataPath) ? null : GD.Load<RegionData>(entry.DataPath),
            };
        }

        PersistentEntities = header.PersistentEntities ?? new List<EntitySimState>();

        _index = new Dictionary<Vector3I, WorldFile.IndexEntry>((int)header.ChunkCount);
        for (uint i = 0; i < header.ChunkCount; i++)
        {
            WorldFile.IndexEntry entry = WorldFile.ReadIndexEntry(r);
            _index[entry.Coord] = entry;
        }
    }

    public IEnumerable<Vector3I> EnumerateChunkCoords()
    {
        return _index.Keys;
    }

    public bool TryLoadChunk(Vector3I coord, out ChunkState state, out List<EntitySimState> entities)
    {
        if (!_index.TryGetValue(coord, out WorldFile.IndexEntry entry))
        {
            state = null;
            entities = null;
            return false;
        }

        // FileStream.Seek + Read is not thread-safe; serialize for safety so a
        // future async loader can share the source without surprises.
        lock (_lock)
        {
            _stream.Seek((long)entry.Offset, SeekOrigin.Begin);
            byte[] buffer = new byte[entry.Length];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = _stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                {
                    throw new EndOfStreamException($"Truncated chunk payload at offset {entry.Offset}");
                }
                read += n;
            }

            using var ms = new MemoryStream(buffer, writable: false);
            using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
            ChunkSerializer.Read(br, coord, out state, out entities, _pathTable);
        }
        return true;
    }

    public void Dispose()
    {
        _stream?.Dispose();
    }
}
