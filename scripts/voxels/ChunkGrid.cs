using System.Collections.Generic;
using Godot;

// Flat, chunk-indexed view of a WorldState's chunk dictionary — the O(1)
// replacement for a Dictionary<Vector3I, ChunkState> lookup inside a per-voxel
// inner loop.
//
// The whole-world voxel floods (sunlight, interiorness) hashed a Vector3I once
// per channel per neighbour — ~48 hashes per popped voxel — which was most of
// what they cost. A world is a bounded box of chunks, so the same question is
// an index into a flat array.
//
// A voxel is addressed as a PACKED index, `chunkIndex << 12 | local`, which is
// what makes it pay twice: a flood queue holds one int per entry instead of
// three, and stepping to a neighbour is an add inside a chunk (the common case,
// 14 times out of 16 per axis) and one table read across a chunk boundary — no
// coordinate arithmetic and no bounds test either way.
//
// Build one per pass and throw it away. It caches chunk references off the
// world, so anything that adds, removes or replaces a chunk invalidates it.
public sealed class ChunkGrid
{
    // ChunkState.SIZE is a power of two, so >> and & replace floor-division and
    // modulo and stay correct for negative world coordinates (an arithmetic
    // shift floors; the mask is the positive remainder). The static ctor keeps
    // SHIFT honest if SIZE ever moves.
    public const int SHIFT = 4;
    public const int MASK = ChunkState.SIZE - 1;

    // Local voxel index within a chunk: lx << 8 | ly << 4 | lz.
    public const int VOXEL_BITS = 12;
    public const int LOCAL_MASK = (1 << VOXEL_BITS) - 1;

    // Neighbour directions, in the order Step takes.
    public static readonly Vector3I[] Offsets =
    {
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, 1), new(0, 0, -1),
    };

    // Per-direction local-index arithmetic: the delta inside a chunk, the delta
    // when the step wraps into the neighbouring chunk, and which lane value
    // means "already on that face".
    private static readonly int[] StepDelta = { 256, -256, 16, -16, 1, -1 };
    private static readonly int[] WrapDelta = { -3840, 3840, -240, 240, -15, 15 };
    private static readonly int[] EdgeShift = { 8, 8, 4, 4, 0, 0 };
    private static readonly int[] EdgeValue = { MASK, 0, MASK, 0, MASK, 0 };

    static ChunkGrid()
    {
        if ((1 << SHIFT) != ChunkState.SIZE)
        {
            throw new System.InvalidOperationException(
                $"ChunkGrid.SHIFT ({SHIFT}) does not match ChunkState.SIZE ({ChunkState.SIZE})");
        }
    }

    private readonly int _minCx, _minCy, _minCz;
    private readonly int _spanX, _spanY, _spanZ;

    private readonly ChunkState[] _chunks;
    // Chunk index of each chunk's six face neighbours, -1 where absent, so a
    // step off the world needs no bounds test of its own.
    private readonly int[] _neighbor;
    // World-voxel origin per chunk, for decoding a packed index back to
    // coordinates without a division.
    private readonly int[] _originX, _originY, _originZ;

    public int Count => _chunks.Length;

    public ChunkGrid(WorldState world) : this(world, world._chunks.Keys)
    {
    }

    // Covers exactly `coords` — anything outside them reads as absent, which is
    // what a regional pass wants for the set it is allowed to travel through.
    public ChunkGrid(WorldState world, ICollection<Vector3I> coords)
    {
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        foreach (Vector3I c in coords)
        {
            if (c.X < minX) { minX = c.X; }
            if (c.Y < minY) { minY = c.Y; }
            if (c.Z < minZ) { minZ = c.Z; }
            if (c.X > maxX) { maxX = c.X; }
            if (c.Y > maxY) { maxY = c.Y; }
            if (c.Z > maxZ) { maxZ = c.Z; }
        }
        if (coords.Count == 0)
        {
            minX = minY = minZ = 0;
            maxX = maxY = maxZ = -1;
        }
        _minCx = minX;
        _minCy = minY;
        _minCz = minZ;
        _spanX = System.Math.Max(0, maxX - minX + 1);
        _spanY = System.Math.Max(0, maxY - minY + 1);
        _spanZ = System.Math.Max(0, maxZ - minZ + 1);

        long cells = (long)_spanX * _spanY * _spanZ;
        if (cells >= 1L << (31 - VOXEL_BITS))
        {
            throw new System.InvalidOperationException(
                $"ChunkGrid: {cells} chunk cells exceeds the packed-index range");
        }

        _chunks = new ChunkState[cells];
        _originX = new int[cells];
        _originY = new int[cells];
        _originZ = new int[cells];
        foreach (Vector3I c in coords)
        {
            ChunkState chunk = world.GetChunk(c);
            if (chunk == null)
            {
                continue;
            }
            int i = ChunkIndex(c.X, c.Y, c.Z);
            _chunks[i] = chunk;
            _originX[i] = c.X * ChunkState.SIZE;
            _originY[i] = c.Y * ChunkState.SIZE;
            _originZ[i] = c.Z * ChunkState.SIZE;
        }

        _neighbor = new int[cells * Offsets.Length];
        for (int i = 0; i < _chunks.Length; i++)
        {
            for (int d = 0; d < Offsets.Length; d++)
            {
                int slot = i * Offsets.Length + d;
                if (_chunks[i] == null)
                {
                    _neighbor[slot] = -1;
                    continue;
                }
                Vector3I n = _chunks[i].ChunkCoord + Offsets[d];
                int ni = ChunkIndex(n.X, n.Y, n.Z);
                _neighbor[slot] = ni >= 0 && _chunks[ni] != null ? ni : -1;
            }
        }
    }

    // -1 when the chunk is outside the grid's box. Does NOT test residency —
    // callers that need it read Chunk(index).
    public int ChunkIndex(int cx, int cy, int cz)
    {
        int dx = cx - _minCx;
        int dy = cy - _minCy;
        int dz = cz - _minCz;
        if ((uint)dx >= (uint)_spanX || (uint)dy >= (uint)_spanY || (uint)dz >= (uint)_spanZ)
        {
            return -1;
        }
        return (dx * _spanY + dy) * _spanZ + dz;
    }

    public ChunkState Chunk(int chunkIndex)
    {
        return _chunks[chunkIndex];
    }

    // Packed index of a world voxel, or -1 if its chunk is absent.
    public int Pack(int wx, int wy, int wz)
    {
        int ci = ChunkIndex(wx >> SHIFT, wy >> SHIFT, wz >> SHIFT);
        if (ci < 0 || _chunks[ci] == null)
        {
            return -1;
        }
        return (ci << VOXEL_BITS) | ((wx & MASK) << 8) | ((wy & MASK) << 4) | (wz & MASK);
    }

    public static int ChunkOf(int packed) => packed >> VOXEL_BITS;

    public static int LocalX(int packed) => (packed >> 8) & MASK;

    public static int LocalY(int packed) => (packed >> 4) & MASK;

    public static int LocalZ(int packed) => packed & MASK;

    public int WorldX(int packed) => _originX[packed >> VOXEL_BITS] + ((packed >> 8) & MASK);

    public int WorldY(int packed) => _originY[packed >> VOXEL_BITS] + ((packed >> 4) & MASK);

    public int WorldZ(int packed) => _originZ[packed >> VOXEL_BITS] + (packed & MASK);

    // The packed voxel one step in `dir` (an index into Offsets), or -1 if that
    // leaves the grid. Inside the chunk — the common case — it is one add.
    public int Step(int packed, int dir)
    {
        int local = packed & LOCAL_MASK;
        if (((local >> EdgeShift[dir]) & MASK) != EdgeValue[dir])
        {
            return packed + StepDelta[dir];
        }
        int nc = _neighbor[(packed >> VOXEL_BITS) * Offsets.Length + dir];
        return nc < 0 ? -1 : (nc << VOXEL_BITS) | (local + WrapDelta[dir]);
    }

    // A sparse per-chunk side channel (canopy, occluders, scratch cost)
    // flattened onto the same indices, so a flood reads it with the chunk index
    // it already has. Entries are null wherever the dictionary has none.
    public T[] Resolve<T>(Dictionary<Vector3I, T> source) where T : class
    {
        var flat = new T[_chunks.Length];
        for (int i = 0; i < _chunks.Length; i++)
        {
            if (_chunks[i] != null && source.TryGetValue(_chunks[i].ChunkCoord, out T value))
            {
                flat[i] = value;
            }
        }
        return flat;
    }
}
