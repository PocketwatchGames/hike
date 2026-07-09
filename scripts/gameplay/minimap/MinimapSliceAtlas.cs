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
// Slice layers are never evicted — they're persistent state (the dictionary
// holds only the few slices the player has visited).
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
    //
    // Writes into both the display layer and the active member's per-slice buffer
    // (lazily allocated in `individual`, may be null before a roster exists).
    public void RevealCircle(int sliceLevel, Vector3 worldPosXZ, float radiusMeters, float innerFraction, ExplorationMask individual, WorldState ws, float eyeY, in MinimapLos los)
    {
        if (!_layers.TryGetValue(sliceLevel, out SliceLayer layer))
        {
            return;
        }
        byte[] indiv = individual?.EnsureSlice(sliceLevel, _widthPixels * _heightPixels);
        layer.RevealCircle(_worldOriginXZ, worldPosXZ, radiusMeters, innerFraction, indiv, ws, eyeY, los);
    }

    // Reveal a single (wx, wz) cell at the given slice level. Used by the
    // outdoor heightmap-driven reveal pass to set per-column visibility on
    // whatever slice a column's ground falls in. No-ops if the slice has no
    // allocated layer.
    public void RevealCellAtWorld(int sliceLevel, int wx, int wz, byte value, ExplorationMask individual)
    {
        if (!_layers.TryGetValue(sliceLevel, out SliceLayer layer))
        {
            return;
        }
        byte[] indiv = individual?.EnsureSlice(sliceLevel, _widthPixels * _heightPixels);
        layer.RevealCellAtWorld(_worldOriginXZ, wx, wz, value, indiv);
    }

    // Recompose every allocated display slice: minimap buffer = party ∪ active
    // (the controlled player's un-banked indoor reveal is shown), world-map
    // buffer = party only. Called on bank, member switch, and revive. Slices
    // absent from a pool are treated as null (fully fogged for that source).
    public void RebuildExploration(ExplorationMask party, ExplorationMask active)
    {
        foreach (KeyValuePair<int, SliceLayer> kv in _layers)
        {
            byte[] p = party != null && party.Slices.TryGetValue(kv.Key, out byte[] pb) ? pb : null;
            byte[] a = active != null && active.Slices.TryGetValue(kv.Key, out byte[] ab) ? ab : null;
            kv.Value.RebuildExploration(p, a);
        }
    }

    // Fold a member's per-slice field reveal into the WORLD MAP's banked display
    // buffers as a one-shot snapshot (tree-climb scout). Only iterates already-
    // allocated layers, matching RebuildExploration; a slice the member revealed
    // has a layer, so nothing charted is missed.
    public void MergeActiveIntoBanked(ExplorationMask active)
    {
        if (active == null)
        {
            return;
        }
        foreach (KeyValuePair<int, SliceLayer> kv in _layers)
        {
            byte[] a = active.Slices.TryGetValue(kv.Key, out byte[] ab) ? ab : null;
            kv.Value.MergeActiveIntoBanked(a);
        }
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
        // Minimap display (party ∪ active) and world-map display (party only).
        private readonly byte[] _exploration;
        private readonly byte[] _explorationBanked;
        private readonly Image _tileImage;
        private readonly Image _explorationImage;
        private readonly Image _explorationBankedImage;
        private readonly ImageTexture _tileTexture;
        private readonly ImageTexture _explorationTexture;
        private readonly ImageTexture _explorationBankedTexture;
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
        private bool _explBankedDirty;

        public ImageTexture TileTexture => _tileTexture;
        public ImageTexture ExplorationTexture => _explorationTexture;
        public ImageTexture ExplorationBankedTexture => _explorationBankedTexture;

        public SliceLayer(int width, int height, int sliceLevel)
        {
            _width = width;
            _height = height;
            _sliceLevel = sliceLevel;
            _sliceCenterY = (ushort)(sliceLevel * MinimapData.PlateauHeight + MinimapData.PlateauHeight / 2);
            _tileData = new byte[width * height * BytesPerPixel];
            _exploration = new byte[width * height];
            _explorationBanked = new byte[width * height];
            _tileImage = Image.CreateFromData(width, height, false, Image.Format.Rgba8, _tileData);
            _explorationImage = Image.CreateFromData(width, height, false, Image.Format.R8, _exploration);
            _explorationBankedImage = Image.CreateFromData(width, height, false, Image.Format.R8, _explorationBanked);
            _tileTexture = ImageTexture.CreateFromImage(_tileImage);
            _explorationTexture = ImageTexture.CreateFromImage(_explorationImage);
            _explorationBankedTexture = ImageTexture.CreateFromImage(_explorationBankedImage);
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

        // Max-merge one reveal sample into the caller's provisional slice buffer
        // (banked at a campfire) and into the live minimap display buffer
        // (party ∪ active, shown immediately). The banked world-map buffer is
        // untouched — un-banked indoor reveal stays off the world map until
        // recorded at a campfire.
        private void WriteReveal(byte[] individual, int idx, byte target)
        {
            if (target > individual[idx])
            {
                individual[idx] = target;
            }
            if (target > _exploration[idx])
            {
                _exploration[idx] = target;
                _explDirty = true;
            }
        }

        // Writes into the caller's per-member `individual` slice buffer (may be
        // null before a roster exists) AND the live minimap display buffer
        // (party ∪ active). The banked world-map buffer is untouched.
        public void RevealCircle(Vector2I worldOriginXZ, Vector3 worldPosXZ, float radiusMeters, float innerFraction, byte[] individual, WorldState ws, float eyeY, in MinimapLos los)
        {
            if (individual == null)
            {
                return;
            }
            bool losOn = los.Enabled && ws != null;
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
            // Sightline is cast at the player's real eye height (not the generous
            // outdoor LOS eye) so a normal 2-block wall reliably blocks the view.
            int eyeYi = Mathf.FloorToInt(eyeY);
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
                    float vis = 1f;
                    if (losOn)
                    {
                        vis = ComputeVisibility(ws, worldPosXZ.X, worldPosXZ.Z, eyeYi,
                            worldOriginXZ.X + x, worldOriginXZ.Y + z, los);
                        if (vis <= 0f)
                        {
                            continue;
                        }
                    }
                    float falloff;
                    if (distSq <= innerSq)
                    {
                        falloff = 1f;
                    }
                    else
                    {
                        falloff = (outerSq - distSq) / (outerSq - innerSq);
                    }
                    byte target = (byte)Mathf.Clamp((int)(falloff * vis * 255f), 0, 255);
                    int idx = z * _width + x;
                    WriteReveal(individual, idx, target);
                }
            }
        }

        // 2D raymarch for indoor reveal: marches from the player toward the
        // target cell at eye height and returns visibility [0..1]. A solid voxel
        // hard-blocks (returns 0); otherwise volumetric fog is accumulated along
        // the ray the same way the outdoor viewshed does. Stops one step short of
        // the target so a wall pixel doesn't self-occlude (we still chart the
        // wall face the player can see).
        private static float ComputeVisibility(WorldState ws, float eyeX, float eyeZ, int eyeY, int wx, int wz, in MinimapLos los)
        {
            float dxw = (wx + 0.5f) - eyeX;
            float dzw = (wz + 0.5f) - eyeZ;
            float dist = Mathf.Sqrt(dxw * dxw + dzw * dzw);
            if (dist < 1.5f)
            {
                return 1f;
            }
            float step = Mathf.Max(los.StepMeters, dist / MinimapData.LosMaxStepsPerRay);
            float invDist = 1f / dist;
            float nx = dxw * invDist;
            float nz = dzw * invDist;
            bool fogOn = los.FogFullBlockMeters > 0f;
            float fogDepth = 0f;
            for (float t = step; t < dist - 0.5f; t += step)
            {
                int sx = Mathf.RoundToInt(eyeX + nx * t);
                int sz = Mathf.RoundToInt(eyeZ + nz * t);
                if (VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(sx, eyeY, sz)))
                {
                    return 0f;
                }
                if (fogOn)
                {
                    int fog = ws.GetFogWorld(sx, eyeY, sz);
                    if (fog > 0)
                    {
                        fogDepth += (fog / 255f) * step / los.FogFullBlockMeters;
                    }
                }
            }
            return fogOn ? Mathf.Clamp(1f - fogDepth, 0f, 1f) : 1f;
        }

        public void RevealCellAtWorld(Vector2I worldOriginXZ, int wx, int wz, byte value, byte[] individual)
        {
            if (individual == null)
            {
                return;
            }
            int x = wx - worldOriginXZ.X;
            int z = wz - worldOriginXZ.Y;
            if (x < 0 || z < 0 || x >= _width || z >= _height)
            {
                return;
            }
            int idx = z * _width + x;
            WriteReveal(individual, idx, value);
        }

        // Recompose this slice's display buffers: minimap = party ∪ active,
        // world map = party only (see MinimapTextures). Either buffer may be
        // null (this slice not present in that pool → fully-fogged source).
        public void RebuildExploration(byte[] party, byte[] active)
        {
            for (int i = 0; i < _exploration.Length; i++)
            {
                byte p = (party != null && i < party.Length) ? party[i] : (byte)0;
                byte a = (active != null && i < active.Length) ? active[i] : (byte)0;
                _exploration[i] = a > p ? a : p;
                _explorationBanked[i] = p;
            }
            _explDirty = true;
            _explBankedDirty = true;
        }

        // Per-slice counterpart of MinimapTextures.MergeActiveIntoBanked — folds
        // `active` into this slice's world-map (banked) buffer via per-pixel max.
        public void MergeActiveIntoBanked(byte[] active)
        {
            if (active == null)
            {
                return;
            }
            int n = System.Math.Min(_explorationBanked.Length, active.Length);
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                if (active[i] > _explorationBanked[i])
                {
                    _explorationBanked[i] = active[i];
                    changed = true;
                }
            }
            if (changed)
            {
                _explBankedDirty = true;
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
            if (_explBankedDirty)
            {
                _explorationBankedImage.SetData(_width, _height, false, Image.Format.R8, _explorationBanked);
                _explorationBankedTexture.Update(_explorationBankedImage);
                _explBankedDirty = false;
            }
        }
    }
}
