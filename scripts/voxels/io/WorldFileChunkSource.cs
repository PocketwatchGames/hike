using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

// IChunkSource backed by a single packed world file. Constructor reads the
// header and full index into memory; TryLoadChunk seeks to the chunk's payload
// and decodes it. The file handle stays open for the lifetime of the source.
//
// v1 callers (Main.cs boot loop) iterate EnumerateChunkCoords and call
// TryLoadChunk for each chunk. Future streaming callers will only call
// TryLoadChunk for chunks they actually need — no other change required.
public sealed class WorldFileChunkSource : IChunkSource
{
    public Vector3I Min { get; }
    public Vector3I Max { get; }
    public Vector3 Spawn { get; }
    public SimData SimData { get; }
    public RegionState[] Regions { get; }

    private readonly Dictionary<Vector3I, WorldFile.IndexEntry> _index;
    private readonly FileStream _stream;
    private readonly object _lock = new();

    public WorldFileChunkSource(string path)
    {
        string osPath = ProjectSettings.GlobalizePath(path);
        _stream = File.Open(osPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        var r = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);

        WorldFile.Header header = WorldFile.ReadHeader(r);
        Min = header.Min;
        Max = header.Max;
        Spawn = header.Spawn;
        SimData = string.IsNullOrEmpty(header.SimDataPath) ? null : GD.Load<SimData>(header.SimDataPath);

        Regions = new RegionState[header.Regions.Length];
        for (int i = 0; i < header.Regions.Length; i++)
        {
            WorldFile.RegionEntry entry = header.Regions[i];
            Regions[i] = new RegionState
            {
                Data = string.IsNullOrEmpty(entry.DataPath) ? null : GD.Load<RegionData>(entry.DataPath),
                WindDirection = entry.WindDirection,
                Elevation = entry.Elevation,
            };
        }

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
            ChunkSerializer.Read(br, coord, out state, out entities);
        }
        return true;
    }

    public void Dispose()
    {
        _stream?.Dispose();
    }
}
