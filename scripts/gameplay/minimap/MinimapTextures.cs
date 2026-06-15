using Godot;

// Owns the GPU-side resources for the overworld minimap and the CPU-side
// byte buffers that back them.
//
// Layout:
//   _surfaceData  — RGBA8, full world XZ extent at OutdoorMetersPerPixel
//                    R = height low byte, G = height high byte,
//                    B = resolved tile id, A = foliage id.
//                    Caller-side monotonic merge: only overwrite a pixel
//                    when the new height >= existing (vertically stacked
//                    chunk loads must converge to the highest surface).
//   _exploration  — R8, same dimensions. Reveal logic (circular outdoor,
//                    shadowcast indoor) writes max(existing, falloff).
//
// Sized once at construction from WorldState.Min/Max; one full-extent
// upload per flush.
public class MinimapTextures
{
    public const int BytesPerSurfacePixel = 4; // RGBA8

    private readonly byte[] _surfaceData;
    private readonly byte[] _exploration;
    private readonly Image _surfaceImage;
    private readonly Image _explorationImage;
    private readonly ImageTexture _surfaceTexture;
    private readonly ImageTexture _explorationTexture;

    private readonly int _widthPixels;
    private readonly int _heightPixels;
    // World-XZ corner of pixel (0,0) in the texture, in voxel coords.
    private readonly Vector2I _worldOriginXZ;
    // Chunk-coord origin (Min.X, Min.Z).
    private readonly Vector2I _chunkOriginXZ;
    private readonly int _chunksWide;
    private readonly int _chunksTall;

    private bool _surfaceDirty;
    private bool _explorationDirty;

    public ImageTexture SurfaceTexture => _surfaceTexture;
    public ImageTexture ExplorationTexture => _explorationTexture;
    public int WidthPixels => _widthPixels;
    public int HeightPixels => _heightPixels;
    public Vector2I WorldOriginXZ => _worldOriginXZ;
    public Vector2 ExtentMeters => new Vector2(
        _widthPixels * MinimapData.OutdoorMetersPerPixel,
        _heightPixels * MinimapData.OutdoorMetersPerPixel);

    public MinimapTextures(WorldState world)
    {
        _chunkOriginXZ = new Vector2I(world.Min.X, world.Min.Z);
        _chunksWide = world.Max.X - world.Min.X + 1;
        _chunksTall = world.Max.Z - world.Min.Z + 1;
        _widthPixels = _chunksWide * MinimapData.OutdoorPixelsPerChunk;
        _heightPixels = _chunksTall * MinimapData.OutdoorPixelsPerChunk;
        _worldOriginXZ = new Vector2I(world.Min.X * ChunkState.SIZE, world.Min.Z * ChunkState.SIZE);

        _surfaceData = new byte[_widthPixels * _heightPixels * BytesPerSurfacePixel];
        _exploration = new byte[_widthPixels * _heightPixels];

        _surfaceImage = Image.CreateFromData(_widthPixels, _heightPixels, false, Image.Format.Rgba8, _surfaceData);
        _explorationImage = Image.CreateFromData(_widthPixels, _heightPixels, false, Image.Format.R8, _exploration);
        _surfaceTexture = ImageTexture.CreateFromImage(_surfaceImage);
        _explorationTexture = ImageTexture.CreateFromImage(_explorationImage);
    }

    // Apply one chunk's surface contribution. cells.Length must be at least
    // OutdoorPixelsPerChunkSq. Foliage palette is consulted for priority
    // resolution; null = priority always wins (treat new stamp as priority 1).
    //
    // Monotonic merge:
    //   - Higher new height: overwrite (new column wins).
    //   - Equal height: overwrite (later chunk loads refresh existing data).
    //   - Lower height: skip (existing column was contributed by a chunk
    //     above this one — it wins).
    //
    // Foliage merge is independent of height: priority comparison only.
    public void ApplyChunkSurface(Vector3I chunkCoord, MinimapData.SurfaceCell[] cells, MinimapFoliageColors foliagePalette)
    {
        int chunkPxOriginX = (chunkCoord.X - _chunkOriginXZ.X) * MinimapData.OutdoorPixelsPerChunk;
        int chunkPxOriginZ = (chunkCoord.Z - _chunkOriginXZ.Y) * MinimapData.OutdoorPixelsPerChunk;
        if (chunkPxOriginX < 0 || chunkPxOriginZ < 0
            || chunkPxOriginX + MinimapData.OutdoorPixelsPerChunk > _widthPixels
            || chunkPxOriginZ + MinimapData.OutdoorPixelsPerChunk > _heightPixels)
        {
            return;
        }

        bool changed = false;
        for (int pz = 0; pz < MinimapData.OutdoorPixelsPerChunk; pz++)
        {
            for (int px = 0; px < MinimapData.OutdoorPixelsPerChunk; px++)
            {
                MinimapData.SurfaceCell cell = cells[pz * MinimapData.OutdoorPixelsPerChunk + px];
                if (cell.Height == MinimapData.NoSurfaceHeight)
                {
                    continue;
                }

                int gx = chunkPxOriginX + px;
                int gz = chunkPxOriginZ + pz;
                int byteIdx = (gz * _widthPixels + gx) * BytesPerSurfacePixel;

                ushort existingHeight = (ushort)(_surfaceData[byteIdx] | (_surfaceData[byteIdx + 1] << 8));
                if (cell.Height < existingHeight)
                {
                    // Lower contribution loses; foliage check still needed below.
                    int existingFoliage = _surfaceData[byteIdx + 3];
                    int newPriority = ResolvePriority(cell.FoliageId, foliagePalette);
                    int existingPriority = ResolvePriority((byte)existingFoliage, foliagePalette);
                    if (cell.FoliageId != 0 && newPriority >= existingPriority)
                    {
                        _surfaceData[byteIdx + 3] = cell.FoliageId;
                        changed = true;
                    }
                    continue;
                }

                _surfaceData[byteIdx + 0] = (byte)(cell.Height & 0xFF);
                _surfaceData[byteIdx + 1] = (byte)((cell.Height >> 8) & 0xFF);
                _surfaceData[byteIdx + 2] = cell.TileId;

                int existingFoliageHi = _surfaceData[byteIdx + 3];
                int newPrio = ResolvePriority(cell.FoliageId, foliagePalette);
                int existingPrio = ResolvePriority((byte)existingFoliageHi, foliagePalette);
                if (cell.FoliageId != 0 && newPrio >= existingPrio)
                {
                    _surfaceData[byteIdx + 3] = cell.FoliageId;
                }
                else if (cell.Height > existingHeight && cell.FoliageId == 0)
                {
                    // Strictly higher new surface clears stale foliage that
                    // belonged to the lower terrain. Equal-height refreshes
                    // keep existing foliage (avoids wiping a tree on a chunk
                    // re-apply with no new scatter).
                    _surfaceData[byteIdx + 3] = 0;
                }
                changed = true;
            }
        }
        if (changed)
        {
            _surfaceDirty = true;
        }
    }

    // Stamp a foliage entry at a world XZ position (typically a prop's
    // origin) as a small disk so the result reads as a rounded blob with
    // crisp edges (the shader uses nearest-neighbor on foliage_id, so the
    // shape comes from the stamped texels themselves). Radius is in
    // source pixels: 0 = single pixel, 1 = 5-pixel plus, 2 = 13-pixel disk.
    public void StampFoliagePoint(Vector3 worldPos, byte foliageId, MinimapFoliageColors palette, int radiusPixels = 0)
    {
        if (foliageId == 0)
        {
            return;
        }
        int cx = (Mathf.FloorToInt(worldPos.X) - _worldOriginXZ.X) / MinimapData.OutdoorMetersPerPixel;
        int cz = (Mathf.FloorToInt(worldPos.Z) - _worldOriginXZ.Y) / MinimapData.OutdoorMetersPerPixel;
        int newPrio = ResolvePriority(foliageId, palette);
        int rSq = radiusPixels * radiusPixels + radiusPixels;
        bool changed = false;
        for (int dz = -radiusPixels; dz <= radiusPixels; dz++)
        {
            for (int dx = -radiusPixels; dx <= radiusPixels; dx++)
            {
                if (dx * dx + dz * dz > rSq)
                {
                    continue;
                }
                int px = cx + dx;
                int pz = cz + dz;
                if (px < 0 || pz < 0 || px >= _widthPixels || pz >= _heightPixels)
                {
                    continue;
                }
                int byteIdx = (pz * _widthPixels + px) * BytesPerSurfacePixel;
                int existingPrio = ResolvePriority(_surfaceData[byteIdx + 3], palette);
                if (newPrio >= existingPrio)
                {
                    _surfaceData[byteIdx + 3] = foliageId;
                    changed = true;
                }
            }
        }
        if (changed)
        {
            _surfaceDirty = true;
        }
    }

    // Reveal a circular disk in the exploration mask centered at world XZ.
    // `innerFraction` controls the soft-edge: inside pxRadius * innerFraction
    // the disk paints 255, from there it linearly falls to 0 at the outer
    // edge. 1.0 = hard edge, ~0.5 = wide soft fade.
    public void RevealCircle(Vector3 worldPosXZ, float radiusMeters, float innerFraction = 0.7f)
    {
        float pxRadius = radiusMeters / MinimapData.OutdoorMetersPerPixel;
        int cx = (Mathf.FloorToInt(worldPosXZ.X) - _worldOriginXZ.X) / MinimapData.OutdoorMetersPerPixel;
        int cz = (Mathf.FloorToInt(worldPosXZ.Z) - _worldOriginXZ.Y) / MinimapData.OutdoorMetersPerPixel;
        int r = Mathf.CeilToInt(pxRadius);
        int x0 = Mathf.Max(cx - r, 0);
        int x1 = Mathf.Min(cx + r, _widthPixels - 1);
        int z0 = Mathf.Max(cz - r, 0);
        int z1 = Mathf.Min(cz + r, _heightPixels - 1);
        float innerR = pxRadius * Mathf.Clamp(innerFraction, 0f, 1f);
        float innerSq = innerR * innerR;
        float outerSq = pxRadius * pxRadius;
        bool changed = false;
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int dx = x - cx;
                int dz = z - cz;
                int distSq = dx * dx + dz * dz;
                if (distSq > outerSq)
                {
                    continue;
                }
                byte target;
                if (distSq <= innerSq)
                {
                    target = 255;
                }
                else
                {
                    float t = (outerSq - distSq) / (outerSq - innerSq);
                    target = (byte)Mathf.Clamp((int)(t * 255f), 0, 255);
                }
                int idx = z * _widthPixels + x;
                if (target > _exploration[idx])
                {
                    _exploration[idx] = target;
                    changed = true;
                }
            }
        }
        if (changed)
        {
            _explorationDirty = true;
        }
    }

    // Returns the stored top-face Y at world XZ, or 0 if the column is
    // out-of-bounds or hasn't been stamped yet. Used by the slice reveal
    // pass to find the ground elevation under each column it visits, so
    // cliffs / sloping terrain near the player reveal their proper slice
    // exploration even when the player never physically walks there.
    public ushort GetHeightAtWorld(int wx, int wz)
    {
        int px = (wx - _worldOriginXZ.X) / MinimapData.OutdoorMetersPerPixel;
        int pz = (wz - _worldOriginXZ.Y) / MinimapData.OutdoorMetersPerPixel;
        if (px < 0 || pz < 0 || px >= _widthPixels || pz >= _heightPixels)
        {
            return 0;
        }
        int byteIdx = (pz * _widthPixels + px) * BytesPerSurfacePixel;
        return (ushort)(_surfaceData[byteIdx] | (_surfaceData[byteIdx + 1] << 8));
    }

    // Push CPU buffer changes to the GPU. Full-texture upload — region
    // updates aren't exposed cleanly via ImageTexture in Godot 4.
    public void Flush()
    {
        if (_surfaceDirty)
        {
            _surfaceImage.SetData(_widthPixels, _heightPixels, false, Image.Format.Rgba8, _surfaceData);
            _surfaceTexture.Update(_surfaceImage);
            _surfaceDirty = false;
        }
        if (_explorationDirty)
        {
            _explorationImage.SetData(_widthPixels, _heightPixels, false, Image.Format.R8, _exploration);
            _explorationTexture.Update(_explorationImage);
            _explorationDirty = false;
        }
    }

    private static int ResolvePriority(byte foliageId, MinimapFoliageColors palette)
    {
        if (foliageId == 0)
        {
            return 0;
        }
        if (palette == null)
        {
            return 1;
        }
        MinimapFoliageEntry entry = palette.Get(foliageId);
        return entry?.Priority ?? 1;
    }
}
