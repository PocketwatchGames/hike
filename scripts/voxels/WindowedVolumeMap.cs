using System.Collections.Generic;
using Godot;

// Shared base for the five per-voxel/-cell ImageTexture3D mirrors the shaders
// sample (LightMap, SkyExposureMap, FogMap, WindMap, WaterCurrentMap). Instead
// of sizing the texture to the whole world (impossible at the target world
// size, and a full GPU re-upload every flush), the texture is a fixed-size
// player-centric WINDOW covering the load radius, addressed TOROIDALLY:
//
//   texel(globalCell) = globalCell mod W           (per axis, W = window cells)
//
// This mapping is independent of where the window is centered, so:
//   * The shader samples `world_pos * inv_size` with a REPEAT sampler (the
//     hardware does the mod); there is no moving origin to push each frame.
//   * On recenter, each world cell keeps its texel forever, so the chunk
//     ENTERING the window writes into exactly the texels the chunk LEAVING it
//     vacated (they are W cells apart ≡ same texel). Recenter only re-encodes
//     the newly-entered chunks — no data movement, no per-crossing full re-encode.
//   * There is NO filter seam as long as W strictly exceeds the resident
//     diameter: the contiguous resident world range maps injectively to texels,
//     and the texel-wrap boundary always sits between two world-adjacent,
//     both-resident cells, so filter_linear across it is exact.
//
// These textures are visual-only — gameplay reads the CPU WorldState arrays
// directly, never the texture — so an undersized/aliased window is cosmetic,
// never a sim desync.
//
// Overlook caveat: bird's-eye overlook streams visual-only backdrop chunks past
// the window (OVERLOOK_LOAD_DISTANCE_MAX ≫ window diameter); those sample wrapped
// (aliased) lighting. They're distant, top-down, mostly flat sunlight, behind
// the overlook fog curtain — expected invisible. If it ever shows, special-case
// overlook (skip the windowed sample / force full-bright on visual-only chunks).
//
// Granularity is per-subclass: voxel maps use ChunkState.SIZE cells per chunk
// (1 voxel = 1 cell); the coarse wind/water maps use ChunkState.ENV_SUBGRID_SIZE
// cells per chunk (one cell = ENV_VOXELS_PER_CELL voxels). The world span of the
// window — and therefore inv_size — is identical for both
// (windowChunks * ChunkState.SIZE voxels).
public abstract class WindowedVolumeMap
{
    protected readonly int _cellsPerChunk;
    private readonly int _bytesPerPixel;
    private readonly Image.Format _format;

    // Window dimensions in texels (cells), = _windowChunks * _cellsPerChunk.
    protected readonly int _width;
    protected readonly int _height;
    protected readonly int _depth;

    // Window size in CHUNKS per axis. Clamped to the world extent, so a world
    // smaller than the requested window collapses that axis to the world size
    // (degenerates to full coverage on that axis — still exact).
    private readonly int _windowChunksX;
    private readonly int _windowChunksY;
    private readonly int _windowChunksZ;

    // World chunk-coord bounds, for clamping the window to the authored world.
    private readonly Vector3I _worldMinChunk;
    private readonly Vector3I _worldMaxChunk;

    // Current window's min chunk corner.
    private Vector3I _winMinChunk;

    protected readonly byte[][] _slicePixels;
    private readonly Image[] _slices;
    private readonly Godot.Collections.Array<Image> _imageList;
    private readonly ImageTexture3D _texture;
    private bool _textureCreated;

    // Chunks needing re-encode before the next upload. Populated by
    // MarkChunkDirty and Recenter; drained by Flush.
    private readonly HashSet<Vector3I> _dirtyChunks = new();

    // Shaders sample with `world_pos * inv_size` (Origin is zero — toroidal
    // addressing has no moving origin). InvSize = 1 / windowWorldSize.
    public Vector3 Origin => Vector3.Zero;
    public Vector3 InvSize { get; }
    // Window span in world-voxel units (used by the wind particle attractor).
    public Vector3 WindowWorldSize { get; }
    public ImageTexture3D Texture => _texture;

    protected WindowedVolumeMap(WorldState world, Vector3I centerChunk,
        int windowDiameterChunks, int cellsPerChunk, int bytesPerPixel, Image.Format format)
    {
        _cellsPerChunk = cellsPerChunk;
        _bytesPerPixel = bytesPerPixel;
        _format = format;

        _worldMinChunk = world.Min;
        _worldMaxChunk = world.Max;

        _windowChunksX = Mathf.Min(windowDiameterChunks, world.Max.X - world.Min.X + 1);
        _windowChunksY = Mathf.Min(windowDiameterChunks, world.Max.Y - world.Min.Y + 1);
        _windowChunksZ = Mathf.Min(windowDiameterChunks, world.Max.Z - world.Min.Z + 1);

        _width = _windowChunksX * cellsPerChunk;
        _height = _windowChunksY * cellsPerChunk;
        _depth = _windowChunksZ * cellsPerChunk;

        WindowWorldSize = new Vector3(
            _windowChunksX * ChunkState.SIZE,
            _windowChunksY * ChunkState.SIZE,
            _windowChunksZ * ChunkState.SIZE);
        InvSize = Vector3.One / WindowWorldSize;

        _winMinChunk = ClampWindowMin(centerChunk);

        _slicePixels = new byte[_depth][];
        _slices = new Image[_depth];
        _imageList = new Godot.Collections.Array<Image>();
        for (int z = 0; z < _depth; z++)
        {
            _slicePixels[z] = new byte[_width * _height * bytesPerPixel];
            _slices[z] = Image.CreateFromData(_width, _height, false, format, _slicePixels[z]);
            _imageList.Add(_slices[z]);
        }

        _texture = new ImageTexture3D();
    }

    // Subclasses call this at the end of their constructor (after seeding any
    // non-zero default bytes) to encode the initial window and create the GPU
    // texture. Kept out of the base constructor so it doesn't invoke the
    // EncodeChunkPixels override before the subclass is fully constructed.
    protected void InitialEncodeAndUpload(WorldState world)
    {
        for (int cz = _winMinChunk.Z; cz < _winMinChunk.Z + _windowChunksZ; cz++)
        {
            for (int cy = _winMinChunk.Y; cy < _winMinChunk.Y + _windowChunksY; cy++)
            {
                for (int cx = _winMinChunk.X; cx < _winMinChunk.X + _windowChunksX; cx++)
                {
                    EncodeChunkIfPresent(world, new Vector3I(cx, cy, cz));
                }
            }
        }
        Upload(initialCreate: true);
    }

    public void MarkChunkDirty(Vector3I coord)
    {
        if (InWindow(coord))
        {
            _dirtyChunks.Add(coord);
        }
    }

    // Re-center the window on the player's chunk. Marks every chunk that is now
    // in-window but wasn't before as dirty (so it re-encodes into the texels the
    // outgoing chunk vacated). Returns true if the window moved.
    public bool Recenter(Vector3I centerChunk)
    {
        Vector3I newMin = ClampWindowMin(centerChunk);
        if (newMin == _winMinChunk) { return false; }

        Vector3I oldMin = _winMinChunk;
        _winMinChunk = newMin;
        for (int cz = newMin.Z; cz < newMin.Z + _windowChunksZ; cz++)
        {
            for (int cy = newMin.Y; cy < newMin.Y + _windowChunksY; cy++)
            {
                for (int cx = newMin.X; cx < newMin.X + _windowChunksX; cx++)
                {
                    // Already inside the old window? Its texels are already
                    // correct (toroidal addressing didn't move them). Only the
                    // freshly-entered chunks need re-encoding.
                    if (cx >= oldMin.X && cx < oldMin.X + _windowChunksX
                        && cy >= oldMin.Y && cy < oldMin.Y + _windowChunksY
                        && cz >= oldMin.Z && cz < oldMin.Z + _windowChunksZ)
                    {
                        continue;
                    }
                    _dirtyChunks.Add(new Vector3I(cx, cy, cz));
                }
            }
        }
        return true;
    }

    // Encodes dirty chunks that are in-window and resident, then re-uploads.
    // A dirty chunk that has left the window is dropped; one not yet resident
    // (null ChunkState — future streaming) stays dirty to encode on arrival.
    public void Flush(WorldState world)
    {
        if (_dirtyChunks.Count == 0) { return; }

        bool anyEncoded = false;
        var processed = new List<Vector3I>();
        foreach (Vector3I coord in _dirtyChunks)
        {
            if (!InWindow(coord))
            {
                processed.Add(coord);
                continue;
            }
            if (world.GetChunk(coord) == null) { continue; }
            EncodeChunkIfPresent(world, coord);
            processed.Add(coord);
            anyEncoded = true;
        }
        for (int i = 0; i < processed.Count; i++)
        {
            _dirtyChunks.Remove(processed[i]);
        }

        if (anyEncoded)
        {
            Upload(initialCreate: false);
        }
    }

    private void EncodeChunkIfPresent(WorldState world, Vector3I coord)
    {
        ChunkState chunk = world.GetChunk(coord);
        if (chunk == null) { return; }

        // Toroidal texel base for the chunk. globalCell is a multiple of
        // _cellsPerChunk and _width a multiple of it too, so the base lands on a
        // chunk boundary in texel space and the chunk never wraps mid-interior.
        int baseX = WrapBase(coord.X * _cellsPerChunk, _width);
        int baseY = WrapBase(coord.Y * _cellsPerChunk, _height);
        int baseZ = WrapBase(coord.Z * _cellsPerChunk, _depth);

        EncodeChunkPixels(chunk, baseX, baseY, baseZ);

        // Refresh the Image wrappers for the slices this chunk touched so the
        // next Upload picks up the mutated bytes (Image is a thin wrapper over
        // the array we own — CreateFromData re-reads without copying).
        for (int s = 0; s < _cellsPerChunk; s++)
        {
            int sliceIdx = baseZ + s;
            _slices[sliceIdx] = Image.CreateFromData(_width, _height, false, _format, _slicePixels[sliceIdx]);
            _imageList[sliceIdx] = _slices[sliceIdx];
        }
    }

    // Writes this chunk's cells into _slicePixels. baseX/Y/Z are the wrapped
    // texel-base of the chunk; cell (s*) at texel (base* + s*). Subclass writes
    // _slicePixels[baseZ+sz] at ((baseY+sy)*_width + baseX+sx) * bytesPerPixel.
    protected abstract void EncodeChunkPixels(ChunkState chunk, int baseX, int baseY, int baseZ);

    private void Upload(bool initialCreate)
    {
        if (initialCreate || !_textureCreated)
        {
            _texture.Create(_format, _width, _height, _depth, false, _imageList);
            _textureCreated = true;
        }
        else
        {
            // No partial 3D-texture update in Godot — this pushes the whole
            // slice set, but the window is small so it's cheap. (Pre-windowing,
            // this full-world Update racing GPU samples caused an intermittent
            // single-frame water-surface darkening at the upload cadence; the
            // small window makes it a non-issue. If a sample-vs-update race ever
            // resurfaces, double-buffer: write a back ImageTexture3D, then swap
            // the global so sampling never races the write.)
            _texture.Update(_imageList);
        }
    }

    private bool InWindow(Vector3I coord)
    {
        return coord.X >= _winMinChunk.X && coord.X < _winMinChunk.X + _windowChunksX
            && coord.Y >= _winMinChunk.Y && coord.Y < _winMinChunk.Y + _windowChunksY
            && coord.Z >= _winMinChunk.Z && coord.Z < _winMinChunk.Z + _windowChunksZ;
    }

    private Vector3I ClampWindowMin(Vector3I centerChunk)
    {
        return new Vector3I(
            ClampMinAxis(centerChunk.X, _windowChunksX, _worldMinChunk.X, _worldMaxChunk.X),
            ClampMinAxis(centerChunk.Y, _windowChunksY, _worldMinChunk.Y, _worldMaxChunk.Y),
            ClampMinAxis(centerChunk.Z, _windowChunksZ, _worldMinChunk.Z, _worldMaxChunk.Z));
    }

    private static int ClampMinAxis(int center, int windowChunks, int worldMin, int worldMax)
    {
        int min = center - windowChunks / 2;
        int maxMin = worldMax - windowChunks + 1;
        if (min < worldMin) { min = worldMin; }
        if (min > maxMin) { min = maxMin; }
        return min;
    }

    private static int WrapBase(int globalCell, int windowCells)
    {
        int m = globalCell % windowCells;
        return m < 0 ? m + windowCells : m;
    }
}
