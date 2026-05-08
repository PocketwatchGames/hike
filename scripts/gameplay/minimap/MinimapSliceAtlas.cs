using System.Collections.Generic;
using Godot;

// Sparse per-vertical-slice texture set for the indoor / underground
// minimap. One SliceLayer per slice level (sliceLevel = floor(worldY /
// PlateauHeight)) is allocated lazily — only slices that actually contain
// content (a non-pure chunk overlapping that band) get a layer.
//
// Each layer owns:
//   * tile data (RGBA8: height_lo, height_hi, tile_id, foliage_id) sized to
//     the world XZ extent at IndoorMetersPerPixel (1m/px → chunks_x*16
//     pixels wide). The shader sees the same layout as outdoor so a single
//     fragment program handles both modes.
//   * exploration mask (R8) at matching dimensions. Indoor reveal writes to
//     this; persists per-slice-level so re-entering an explored cave keeps
//     its previous reveal.
//
// Slice layers are never evicted — they're persistent state. For the target
// huge world this needs a sliding window similar to the planned outdoor
// streaming, but at current dev scale the dictionary stays small (only the
// few slices the player has visited).
public class MinimapSliceAtlas
{
    private readonly Dictionary<int, SliceLayer> _layers = new();
    private readonly int _widthPixels;
    private readonly int _heightPixels;
    private readonly Vector2I _worldOriginXZ;
    private readonly Vector2I _chunkOriginXZ;

    public Vector2I WorldOriginXZ => _worldOriginXZ;
    public int WidthPixels => _widthPixels;
    public int HeightPixels => _heightPixels;

    public MinimapSliceAtlas(WorldState world)
    {
        _chunkOriginXZ = new Vector2I(world.Min.X, world.Min.Z);
        int chunksWide = world.Max.X - world.Min.X + 1;
        int chunksTall = world.Max.Z - world.Min.Z + 1;
        _widthPixels = chunksWide * MinimapData.IndoorPixelsPerChunk;
        _heightPixels = chunksTall * MinimapData.IndoorPixelsPerChunk;
        _worldOriginXZ = new Vector2I(world.Min.X * ChunkState.SIZE, world.Min.Z * ChunkState.SIZE);
    }

    // Generate and apply all slice tiles for a chunk that has just loaded.
    // Pure-air chunks contribute nothing (no slice gets a tile from them);
    // pure-solid chunks produce wall-only tiles in every slice they overlap;
    // mixed chunks may contribute a different tile per slice.
    //
    // `buffer` is a reusable IndoorPixelsPerChunkSq-length array provided by
    // the caller so this method allocates nothing per invocation.
    public void ApplyChunkSlices(
        Vector3I chunkCoord,
        ChunkState chunk,
        DetailGroupData[] detailPalette,
        TerrainData[] terrainPalette,
        WorldState worldState,
        MinimapFoliageColors foliagePalette,
        MinimapData.SliceCell[] buffer)
    {
        int chunkBaseSliceLevel = chunkCoord.Y * MinimapData.SlicesPerChunk;
        for (int s = 0; s < MinimapData.SlicesPerChunk; s++)
        {
            MinimapData.GenerateSliceTile(chunk, s, detailPalette, terrainPalette, worldState, buffer);
            if (IsBufferAllEmpty(buffer))
            {
                continue;
            }
            int sliceLevel = chunkBaseSliceLevel + s;
            SliceLayer layer = GetOrCreateLayer(sliceLevel);
            layer.ApplyChunkSlice(chunkCoord, _chunkOriginXZ, buffer, foliagePalette);
        }
    }

    // Reveal a circular disk on the indoor exploration mask at `sliceLevel`.
    // If the slice has no allocated layer (no chunk in this band has had any
    // content), the reveal call no-ops — the player is in genuinely-empty
    // air and there's nothing to remember as explored.
    public void RevealCircle(int sliceLevel, Vector3 worldPosXZ, float radiusMeters, float innerFraction = 0.7f)
    {
        if (!_layers.TryGetValue(sliceLevel, out SliceLayer layer))
        {
            return;
        }
        layer.RevealCircle(_worldOriginXZ, worldPosXZ, radiusMeters, innerFraction);
    }

    // Reveal a single (wx, wz) cell at the given slice level. Used by the
    // outdoor heightmap-driven reveal pass to set per-column visibility on
    // whatever slice a column's ground falls in. No-ops if the slice has no
    // allocated layer.
    public void RevealCellAtWorld(int sliceLevel, int wx, int wz, byte value)
    {
        if (!_layers.TryGetValue(sliceLevel, out SliceLayer layer))
        {
            return;
        }
        layer.RevealCellAtWorld(_worldOriginXZ, wx, wz, value);
    }

    public SliceLayer TryGetLayer(int sliceLevel)
    {
        _layers.TryGetValue(sliceLevel, out SliceLayer layer);
        return layer;
    }

    public void Flush()
    {
        foreach (SliceLayer layer in _layers.Values)
        {
            layer.Flush();
        }
    }

    private SliceLayer GetOrCreateLayer(int sliceLevel)
    {
        if (_layers.TryGetValue(sliceLevel, out SliceLayer layer))
        {
            return layer;
        }
        layer = new SliceLayer(_widthPixels, _heightPixels, sliceLevel);
        _layers[sliceLevel] = layer;
        return layer;
    }

    private static bool IsBufferAllEmpty(MinimapData.SliceCell[] buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i].TileId != 0 || buffer[i].FoliageId != 0 || buffer[i].Flags != 0)
            {
                return false;
            }
        }
        return true;
    }

    public class SliceLayer
    {
        public const int BytesPerPixel = 4; // RGBA8

        private readonly byte[] _tileData;
        private readonly byte[] _exploration;
        private readonly Image _tileImage;
        private readonly Image _explorationImage;
        private readonly ImageTexture _tileTexture;
        private readonly ImageTexture _explorationTexture;
        private readonly int _width;
        private readonly int _height;
        private readonly int _sliceLevel;
        // Synthesized "height" stored in the tile texture so the same shader
        // path that does outdoor elevation shading produces sensible results
        // indoors — every pixel in this slice reports the same Y, so the
        // shader's reference-elevation pivot just renders flat brightness
        // when reference == sliceCenterY.
        private readonly ushort _sliceCenterY;
        private bool _tileDirty;
        private bool _explDirty;

        public ImageTexture TileTexture => _tileTexture;
        public ImageTexture ExplorationTexture => _explorationTexture;

        public SliceLayer(int width, int height, int sliceLevel)
        {
            _width = width;
            _height = height;
            _sliceLevel = sliceLevel;
            _sliceCenterY = (ushort)(sliceLevel * MinimapData.PlateauHeight + MinimapData.PlateauHeight / 2);
            _tileData = new byte[width * height * BytesPerPixel];
            _exploration = new byte[width * height];
            _tileImage = Image.CreateFromData(width, height, false, Image.Format.Rgba8, _tileData);
            _explorationImage = Image.CreateFromData(width, height, false, Image.Format.R8, _exploration);
            _tileTexture = ImageTexture.CreateFromImage(_tileImage);
            _explorationTexture = ImageTexture.CreateFromImage(_explorationImage);
        }

        public void ApplyChunkSlice(
            Vector3I chunkCoord,
            Vector2I chunkOriginXZ,
            MinimapData.SliceCell[] cells,
            MinimapFoliageColors foliagePalette)
        {
            int chunkPxOriginX = (chunkCoord.X - chunkOriginXZ.X) * MinimapData.IndoorPixelsPerChunk;
            int chunkPxOriginZ = (chunkCoord.Z - chunkOriginXZ.Y) * MinimapData.IndoorPixelsPerChunk;
            if (chunkPxOriginX < 0 || chunkPxOriginZ < 0
                || chunkPxOriginX + MinimapData.IndoorPixelsPerChunk > _width
                || chunkPxOriginZ + MinimapData.IndoorPixelsPerChunk > _height)
            {
                return;
            }
            byte hLo = (byte)(_sliceCenterY & 0xFF);
            byte hHi = (byte)((_sliceCenterY >> 8) & 0xFF);
            for (int pz = 0; pz < MinimapData.IndoorPixelsPerChunk; pz++)
            {
                for (int px = 0; px < MinimapData.IndoorPixelsPerChunk; px++)
                {
                    MinimapData.SliceCell cell = cells[pz * MinimapData.IndoorPixelsPerChunk + px];
                    int gx = chunkPxOriginX + px;
                    int gz = chunkPxOriginZ + pz;
                    int idx = (gz * _width + gx) * BytesPerPixel;

                    bool hasContent = cell.TileId != 0 || (cell.Flags & MinimapData.SliceFlagFloor) != 0;
                    if (hasContent)
                    {
                        _tileData[idx + 0] = hLo;
                        _tileData[idx + 1] = hHi;
                        _tileData[idx + 2] = cell.TileId;
                        _tileData[idx + 3] = cell.FoliageId;
                    }
                    else
                    {
                        // Air column inside this slice — paint void.
                        _tileData[idx + 0] = 0;
                        _tileData[idx + 1] = 0;
                        _tileData[idx + 2] = 0;
                        _tileData[idx + 3] = 0;
                    }
                }
            }
            _tileDirty = true;
        }

        public void RevealCircle(Vector2I worldOriginXZ, Vector3 worldPosXZ, float radiusMeters, float innerFraction = 0.7f)
        {
            float pxRadius = radiusMeters / MinimapData.IndoorMetersPerPixel;
            int cx = Mathf.FloorToInt(worldPosXZ.X) - worldOriginXZ.X;
            int cz = Mathf.FloorToInt(worldPosXZ.Z) - worldOriginXZ.Y;
            int r = Mathf.CeilToInt(pxRadius);
            int x0 = Mathf.Max(cx - r, 0);
            int x1 = Mathf.Min(cx + r, _width - 1);
            int z0 = Mathf.Max(cz - r, 0);
            int z1 = Mathf.Min(cz + r, _height - 1);
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
                    int idx = z * _width + x;
                    if (target > _exploration[idx])
                    {
                        _exploration[idx] = target;
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                _explDirty = true;
            }
        }

        public void RevealCellAtWorld(Vector2I worldOriginXZ, int wx, int wz, byte value)
        {
            int x = wx - worldOriginXZ.X;
            int z = wz - worldOriginXZ.Y;
            if (x < 0 || z < 0 || x >= _width || z >= _height)
            {
                return;
            }
            int idx = z * _width + x;
            if (value > _exploration[idx])
            {
                _exploration[idx] = value;
                _explDirty = true;
            }
        }

        public void Flush()
        {
            if (_tileDirty)
            {
                _tileImage.SetData(_width, _height, false, Image.Format.Rgba8, _tileData);
                _tileTexture.Update(_tileImage);
                _tileDirty = false;
            }
            if (_explDirty)
            {
                _explorationImage.SetData(_width, _height, false, Image.Format.R8, _exploration);
                _explorationTexture.Update(_explorationImage);
                _explDirty = false;
            }
        }
    }
}
