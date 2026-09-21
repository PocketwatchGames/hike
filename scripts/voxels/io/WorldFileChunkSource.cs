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
    public WorldStartData StartContent { get; }
    public ZoneState[] Zones { get; }

    // The terrain palette this file was baked against, one resource path per slot.
    // Main.LoadWorldFromFile checks it against the palette the world is about to
    // be read with — see WorldFile VERSION v46.
    public string[] TerrainSlots { get; }

    // Detail-palette slots, same contract — DetailGroup bytes index this.
    public string[] DetailSlots { get; }
    public RegionState[] Regions { get; }

    // Named points of interest baked into the file. Main.LoadWorldFromFile
    // copies these into WorldState — nothing recomputes them after worldgen.
    public Dictionary<string, Vector3> PointsOfInterest { get; }

    // Named buried treasures still in the ground, the same way — what a
    // treasure map resolves its name against.
    public Dictionary<string, Vector3> TreasureSpots { get; }
    // Non-chunked always-resident entity states (the player's companion), read
    // from the world file's global section. Main.LoadWorldFromFile files these
    // into WorldState.PersistentEntities rather than a per-chunk bucket.
    public List<EntitySimState> PersistentEntities { get; }
    public string BakeId { get; }

    private readonly Dictionary<Vector3I, WorldFile.IndexEntry> _index;
    private readonly Stream _stream;
    private readonly object _lock = new();
    // File-wide resource-path table from the header; every chunk's entity list
    // resolves its path indices against it.
    private readonly EntitySerializer.ReadPathTable _pathTable;

    // The header and index are thousands of tiny reads; unbuffered, each one is a
    // native FileAccess call.
    private const int READ_BUFFER_BYTES = 1 << 16;

    public WorldFileChunkSource(string path)
    {
        // Godot's FileAccess, not System.IO: in an exported build a res:// world
        // lives inside the .pck and has no OS path.
        _stream = new BufferedStream(new GodotFileReadStream(path), READ_BUFFER_BYTES);
        var r = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);

        // A rejected header (a stale version) must not leave the file open: the
        // running game would hold the .hike and the re-bake that fixes it would
        // fail to write.
        WorldFile.Header header;
        try
        {
            header = WorldFile.ReadHeader(r);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
        _pathTable = header.PathTable;
        Min = header.Min;
        Max = header.Max;
        Spawn = header.Spawn;
        SimData = string.IsNullOrEmpty(header.SimDataPath) ? null : GD.Load<SimData>(header.SimDataPath);
        StartContent = string.IsNullOrEmpty(header.StartContentPath)
            ? null
            : GD.Load<WorldStartData>(header.StartContentPath);
        TerrainSlots = header.TerrainSlots ?? System.Array.Empty<string>();
        DetailSlots = header.DetailSlots ?? System.Array.Empty<string>();
        PointsOfInterest = header.PointsOfInterest ?? new Dictionary<string, Vector3>();
        TreasureSpots = header.TreasureSpots ?? new Dictionary<string, Vector3>();

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
        BakeId = header.BakeId ?? "";

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

        // Stream.Seek + Read is not thread-safe; serialize for safety so a
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

    // Read-only, seekable Stream over a Godot FileAccess.
    private sealed class GodotFileReadStream : Stream
    {
        private readonly Godot.FileAccess _file;

        public GodotFileReadStream(string path)
        {
            _file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (_file == null)
            {
                throw new IOException($"could not open '{path}' ({Godot.FileAccess.GetOpenError()})");
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => (long)_file.GetLength();

        public override long Position
        {
            get => (long)_file.GetPosition();
            set => _file.Seek((ulong)value);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            byte[] bytes = _file.GetBuffer(count);
            System.Array.Copy(bytes, 0, buffer, offset, bytes.Length);
            return bytes.Length;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => offset,
            };
            Position = target;
            return target;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new System.NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new System.NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file.Close();
                _file.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
