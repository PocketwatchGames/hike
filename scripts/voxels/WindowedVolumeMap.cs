using System.Collections.Generic;
using Godot;

// Shared base for the five per-voxel/-cell volume textures the shaders sample
// (LightMap, SkyExposureMap, FogMap, WindMap, WaterCurrentMap). Instead of
// sizing the texture to the whole world (impossible at the target world size,
// and a full GPU re-upload every flush), the texture is a fixed-size
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
// PARTIAL UPLOAD: the texture is a RenderingDevice 3D texture wrapped in a
// Texture3DRD (so it still binds as a normal sampler3D global with repeat +
// linear). A dirty chunk is encoded into a small chunk-sized STAGING texture
// (one texture_update) and then GPU-copied into its window-aligned region of
// the volume (one texture_copy). Only changed chunks touch the GPU each flush —
// Godot's ImageTexture3D has no partial update, so the old path re-uploaded the
// entire window (every slice marshaled across the C#↔C++ boundary) on any
// change. A chunk always occupies a contiguous, non-wrapping cells³ texel block
// (WrapBase lands on a chunk boundary and W is a multiple of cellsPerChunk), so
// the region copy is a single in-bounds blit.
//
// Granularity is per-subclass: voxel maps use ChunkState.SIZE cells per chunk
// (1 voxel = 1 cell); the coarse wind/water maps use ChunkState.ENV_SUBGRID_SIZE
// cells per chunk (one cell = ENV_VOXELS_PER_CELL voxels). The world span of the
// window — and therefore inv_size — is identical for both
// (windowChunks * ChunkState.SIZE voxels).
//
// THREADING: the texture_update / texture_copy calls run on the main thread
// against the global RenderingDevice (same pattern as Texture3DRD / compute-in-
// _process usage). If this ever races the render thread on some driver, the fix
// is to marshal the per-flush ops through RenderingServer.CallOnRenderThread.
public abstract class WindowedVolumeMap
{
    protected readonly int _cellsPerChunk;
    private readonly int _bytesPerPixel;
    private readonly RenderingDevice.DataFormat _rdFormat;

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

    // RenderingDevice handles. _volumeRid is the windowed 3D texture the shaders
    // sample (wrapped by _texture); _stagingRid is a reusable cells³ scratch
    // texture each dirty chunk is uploaded into before being GPU-copied into the
    // volume. Both freed in Free().
    private readonly RenderingDevice _rd;
    private Rid _volumeRid;
    private Rid _stagingRid;
    private readonly Texture3Drd _texture;

    // Reused chunk-local encode buffer (cells³ * bpp, z-major). Filled by the
    // subclass's EncodeChunkPixels, uploaded into the staging texture, copied
    // into the volume.
    private readonly byte[] _chunkScratch;

    // Chunks needing re-encode before the next upload. Populated by
    // MarkChunkDirty and Recenter; drained by Flush.
    private readonly HashSet<Vector3I> _dirtyChunks = new();
    // Scratch reused by Flush to record which dirty entries were handled.
    private readonly List<Vector3I> _processedScratch = new();

    // Shaders sample with `world_pos * inv_size` (Origin is zero — toroidal
    // addressing has no moving origin). InvSize = 1 / windowWorldSize.
    public Vector3 Origin => Vector3.Zero;
    public Vector3 InvSize { get; }
    // Window span in world-voxel units (used by the wind particle attractor).
    public Vector3 WindowWorldSize { get; }
    public Texture3D Texture => _texture;

    protected WindowedVolumeMap(WorldState world, Vector3I centerChunk,
        int windowDiameterChunks, int cellsPerChunk, int bytesPerPixel, Image.Format format)
    {
        _cellsPerChunk = cellsPerChunk;
        _bytesPerPixel = bytesPerPixel;
        _rdFormat = RdFormat(format);

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

        _chunkScratch = new byte[cellsPerChunk * cellsPerChunk * cellsPerChunk * bytesPerPixel];

        // Null under the headless dummy renderer (no RenderingDevice). These
        // maps are visual-only, so every GPU op below no-ops in that case and
        // the sim runs unaffected.
        _rd = RenderingServer.GetRenderingDevice();
        _stagingRid = _rd != null ? CreateStagingTexture() : default;
        _texture = new Texture3Drd();
    }

    // Subclasses call this at the end of their constructor (after any non-zero
    // DefaultPixel is in effect) to encode the initial window into one full
    // buffer and create the GPU volume from it. Kept out of the base constructor
    // so it doesn't invoke the EncodeChunkPixels/DefaultPixel overrides before
    // the subclass is fully constructed.
    protected void InitialEncodeAndUpload(WorldState world)
    {
        if (_rd == null) { return; }
        byte[] full = new byte[_width * _height * _depth * _bytesPerPixel];
        SeedDefault(full);

        for (int cz = _winMinChunk.Z; cz < _winMinChunk.Z + _windowChunksZ; cz++)
        {
            for (int cy = _winMinChunk.Y; cy < _winMinChunk.Y + _windowChunksY; cy++)
            {
                for (int cx = _winMinChunk.X; cx < _winMinChunk.X + _windowChunksX; cx++)
                {
                    ChunkState chunk = world.GetChunk(new Vector3I(cx, cy, cz));
                    if (chunk == null) { continue; }
                    EncodeChunkPixels(chunk, _chunkScratch);
                    ScatterChunkIntoFull(full, new Vector3I(cx, cy, cz));
                }
            }
        }

        var data = new Godot.Collections.Array<byte[]> { full };
        var fmt = new RDTextureFormat
        {
            Format = _rdFormat,
            Width = (uint)_width,
            Height = (uint)_height,
            Depth = (uint)_depth,
            TextureType = RenderingDevice.TextureType.Type3D,
            Mipmaps = 1,
            ArrayLayers = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                      | RenderingDevice.TextureUsageBits.CanUpdateBit
                      | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _volumeRid = _rd.TextureCreate(fmt, new RDTextureView(), data);
        _texture.TextureRdRid = _volumeRid;
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

    // Encodes dirty chunks that are in-window and resident, GPU-copying each into
    // its window region. A dirty chunk that has left the window is dropped; one
    // not yet resident (null ChunkState — future streaming) stays dirty to encode
    // on arrival.
    public void Flush(WorldState world)
    {
        // Headless (no RenderingDevice): drop dirty chunks without touching the
        // GPU so the set doesn't grow unbounded as the window recenters.
        if (_rd == null) { _dirtyChunks.Clear(); return; }
        if (_dirtyChunks.Count == 0) { return; }

        _processedScratch.Clear();
        foreach (Vector3I coord in _dirtyChunks)
        {
            if (!InWindow(coord))
            {
                _processedScratch.Add(coord);
                continue;
            }
            ChunkState chunk = world.GetChunk(coord);
            if (chunk == null) { continue; }
            EncodeChunkPixels(chunk, _chunkScratch);
            UploadChunkRegion(coord);
            _processedScratch.Add(coord);
        }
        for (int i = 0; i < _processedScratch.Count; i++)
        {
            _dirtyChunks.Remove(_processedScratch[i]);
        }
    }

    // Uploads _chunkScratch into the staging texture and GPU-copies it into the
    // volume at the chunk's wrapped, chunk-aligned texel base.
    private void UploadChunkRegion(Vector3I coord)
    {
        int baseX = WrapBase(coord.X * _cellsPerChunk, _width);
        int baseY = WrapBase(coord.Y * _cellsPerChunk, _height);
        int baseZ = WrapBase(coord.Z * _cellsPerChunk, _depth);

        _rd.TextureUpdate(_stagingRid, 0, _chunkScratch);
        _rd.TextureCopy(
            _stagingRid, _volumeRid,
            Vector3.Zero,
            new Vector3(baseX, baseY, baseZ),
            new Vector3(_cellsPerChunk, _cellsPerChunk, _cellsPerChunk),
            0, 0, 0, 0);
    }

    // Copies the chunk-local _chunkScratch into the full-window buffer at the
    // chunk's wrapped texel base (used only by the one-shot InitialEncode).
    private void ScatterChunkIntoFull(byte[] full, Vector3I coord)
    {
        int baseX = WrapBase(coord.X * _cellsPerChunk, _width);
        int baseY = WrapBase(coord.Y * _cellsPerChunk, _height);
        int baseZ = WrapBase(coord.Z * _cellsPerChunk, _depth);
        int rowBytes = _cellsPerChunk * _bytesPerPixel;
        for (int lz = 0; lz < _cellsPerChunk; lz++)
        {
            for (int ly = 0; ly < _cellsPerChunk; ly++)
            {
                int src = ((lz * _cellsPerChunk + ly) * _cellsPerChunk) * _bytesPerPixel;
                int dst = (((baseZ + lz) * _height + (baseY + ly)) * _width + baseX) * _bytesPerPixel;
                System.Array.Copy(_chunkScratch, src, full, dst, rowBytes);
            }
        }
    }

    // Tiles DefaultPixel across the full-window buffer so non-resident regions
    // decode to the map's neutral value (signed-zero wind / water, dark light).
    // No-op when DefaultPixel is null (zero bytes are already correct).
    private void SeedDefault(byte[] full)
    {
        byte[] pixel = DefaultPixel;
        if (pixel == null) { return; }
        for (int o = 0; o < full.Length; o += _bytesPerPixel)
        {
            for (int b = 0; b < _bytesPerPixel; b++)
            {
                full[o + b] = pixel[b];
            }
        }
    }

    // Writes this chunk's cells into dst (length cells³ * bpp) in z-major order:
    // cell (lx,ly,lz) at ((lz*cells + ly)*cells + lx) * bpp. This is the layout
    // both the staging texture upload and the full-buffer scatter expect.
    protected abstract void EncodeChunkPixels(ChunkState chunk, byte[] dst);

    // Per-pixel default for cells outside any resident chunk. Null (the base
    // default) means all-zero. Override for the signed-zero-encoded maps.
    protected virtual byte[] DefaultPixel => null;

    // Releases the GPU textures. Call on teardown — RIDs are not GC-managed.
    public void Free()
    {
        if (_rd == null) { return; }
        if (_volumeRid.IsValid)
        {
            _rd.FreeRid(_volumeRid);
            _volumeRid = new Rid();
        }
        if (_stagingRid.IsValid)
        {
            _rd.FreeRid(_stagingRid);
            _stagingRid = new Rid();
        }
    }

    private Rid CreateStagingTexture()
    {
        var fmt = new RDTextureFormat
        {
            Format = _rdFormat,
            Width = (uint)_cellsPerChunk,
            Height = (uint)_cellsPerChunk,
            Depth = (uint)_cellsPerChunk,
            TextureType = RenderingDevice.TextureType.Type3D,
            Mipmaps = 1,
            ArrayLayers = 1,
            UsageBits = RenderingDevice.TextureUsageBits.CanUpdateBit
                      | RenderingDevice.TextureUsageBits.CanCopyFromBit,
        };
        return _rd.TextureCreate(fmt, new RDTextureView());
    }

    private static RenderingDevice.DataFormat RdFormat(Image.Format format)
    {
        switch (format)
        {
            case Image.Format.R8: return RenderingDevice.DataFormat.R8Unorm;
            case Image.Format.Rg8: return RenderingDevice.DataFormat.R8G8Unorm;
            case Image.Format.Rgba8: return RenderingDevice.DataFormat.R8G8B8A8Unorm;
            default: throw new System.ArgumentException($"Unsupported volume map format {format}");
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
