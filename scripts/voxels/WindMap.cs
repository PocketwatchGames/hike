using System.Collections.Generic;
using Godot;

// Coarse wind 3D texture, RGBA8. Each cell covers a 4x4x4 voxel block
// (ENV_VOXELS_PER_CELL on each axis). Sampled in world space by shaders
// and (eventually) by audio code via the `wind_map` global uniform; the
// hardware sampler's trilinear filter smooths cave-mouth transitions
// over the cell footprint.
//
// Channel layout:
//   R, G, B = WindVelocityX, Y, Z. Signed, byte-128-zero encoding —
//             shader does `texture(wind_map, uvw).rgb * 2.0 - 1.0` to
//             recover [-1, 1], then multiplies by `wind_velocity_scale`
//             to get world m/s. Carries per-zone wind direction by
//             default (baked by WindGen) plus any authored overrides
//             (mountain pass funnels, cave drafts, localized gusts).
//   A       = WindFactor. 0 = sealed (deep cave / building interior),
//             255 = full ambient wind. Existing damping multiplier
//             unchanged from when wind_map was R8 — consumers that only
//             need the factor read `.a` instead of the old `.r`.
//
// Sized to the full WorldState voxel extent so origin/inv_size match the
// LightMap and FogMap convention: shader does
//   uvw = (world_pos - wind_map_origin) * wind_map_inv_size
// regardless of which subgrid texture is being sampled.
public class WindMap
{
    private const int CELL = ChunkState.ENV_VOXELS_PER_CELL;
    private const int CELLS_PER_CHUNK = ChunkState.ENV_SUBGRID_SIZE;
    private const int BYTES_PER_PIXEL = 4;

    private readonly int _width;
    private readonly int _height;
    private readonly int _depth;
    private readonly int _originCellX;
    private readonly int _originCellY;
    private readonly int _originCellZ;
    private readonly byte[][] _slicePixels;
    private readonly Image[] _slices;
    private readonly Godot.Collections.Array<Image> _imageList;
    private readonly ImageTexture3D _texture;
    private bool _textureCreated;

    private readonly HashSet<Vector3I> _dirtyChunks = new();

    public Vector3 Origin { get; }
    public Vector3 Size { get; }
    public ImageTexture3D Texture => _texture;

    public WindMap(WorldState world)
    {
        // Origin/Size are in WORLD-VOXEL units — same convention as LightMap
        // and FogMap, so a shader can use a single `(pos - origin) * inv_size`
        // sampling expression no matter which subgrid texture it reads.
        int originVoxelX = world.Min.X * ChunkState.SIZE;
        int originVoxelY = world.Min.Y * ChunkState.SIZE;
        int originVoxelZ = world.Min.Z * ChunkState.SIZE;
        int sizeVoxelX = (world.Max.X + 1) * ChunkState.SIZE - originVoxelX;
        int sizeVoxelY = (world.Max.Y + 1) * ChunkState.SIZE - originVoxelY;
        int sizeVoxelZ = (world.Max.Z + 1) * ChunkState.SIZE - originVoxelZ;

        Origin = new Vector3(originVoxelX, originVoxelY, originVoxelZ);
        Size = new Vector3(sizeVoxelX, sizeVoxelY, sizeVoxelZ);

        _originCellX = originVoxelX / CELL;
        _originCellY = originVoxelY / CELL;
        _originCellZ = originVoxelZ / CELL;
        _width = sizeVoxelX / CELL;
        _height = sizeVoxelY / CELL;
        _depth = sizeVoxelZ / CELL;

        _slicePixels = new byte[_depth][];
        _slices = new Image[_depth];
        _imageList = new Godot.Collections.Array<Image>();
        for (int z = 0; z < _depth; z++)
        {
            _slicePixels[z] = new byte[_width * _height * BYTES_PER_PIXEL];
            // Seed velocity channels (RGB) to byte 128 = signed zero so
            // unauthored cells decode to zero wind, not max-negative. Alpha
            // (WindFactor) defaults to 0 = sealed, the safe pre-bake state.
            for (int i = 0; i < _slicePixels[z].Length; i += BYTES_PER_PIXEL)
            {
                _slicePixels[z][i + 0] = 128;
                _slicePixels[z][i + 1] = 128;
                _slicePixels[z][i + 2] = 128;
                _slicePixels[z][i + 3] = 0;
            }
            _slices[z] = Image.CreateFromData(_width, _height, false, Image.Format.Rgba8, _slicePixels[z]);
            _imageList.Add(_slices[z]);
        }

        _texture = new ImageTexture3D();
        for (int cz = world.Min.Z; cz <= world.Max.Z; cz++)
        {
            for (int cy = world.Min.Y; cy <= world.Max.Y; cy++)
            {
                for (int cx = world.Min.X; cx <= world.Max.X; cx++)
                {
                    EncodeChunkIfPresent(world, new Vector3I(cx, cy, cz));
                }
            }
        }
        Upload(initialCreate: true);
    }

    public void MarkChunkDirty(Vector3I coord)
    {
        _dirtyChunks.Add(coord);
    }

    public void Flush(WorldState world, ICollection<Vector3I> visibleChunks)
    {
        if (_dirtyChunks.Count == 0) { return; }

        bool anyEncoded = false;
        var processed = new List<Vector3I>();
        foreach (Vector3I coord in _dirtyChunks)
        {
            if (!visibleChunks.Contains(coord)) { continue; }
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
        if (coord.X < world.Min.X || coord.X > world.Max.X
            || coord.Y < world.Min.Y || coord.Y > world.Max.Y
            || coord.Z < world.Min.Z || coord.Z > world.Max.Z)
        {
            return;
        }

        int baseX = coord.X * CELLS_PER_CHUNK - _originCellX;
        int baseY = coord.Y * CELLS_PER_CHUNK - _originCellY;
        int baseZ = coord.Z * CELLS_PER_CHUNK - _originCellZ;

        for (int sz = 0; sz < CELLS_PER_CHUNK; sz++)
        {
            byte[] pixels = _slicePixels[baseZ + sz];
            for (int sy = 0; sy < CELLS_PER_CHUNK; sy++)
            {
                int rowOffset = ((baseY + sy) * _width + baseX) * BYTES_PER_PIXEL;
                for (int sx = 0; sx < CELLS_PER_CHUNK; sx++)
                {
                    int o = rowOffset + sx * BYTES_PER_PIXEL;
                    pixels[o + 0] = chunk.WindVelocityX[sx, sy, sz];
                    pixels[o + 1] = chunk.WindVelocityY[sx, sy, sz];
                    pixels[o + 2] = chunk.WindVelocityZ[sx, sy, sz];
                    pixels[o + 3] = chunk.WindFactor[sx, sy, sz];
                }
            }
        }

        for (int sz = 0; sz < CELLS_PER_CHUNK; sz++)
        {
            int sliceIdx = baseZ + sz;
            _slices[sliceIdx] = Image.CreateFromData(_width, _height, false, Image.Format.Rgba8, _slicePixels[sliceIdx]);
            _imageList[sliceIdx] = _slices[sliceIdx];
        }
    }

    private void Upload(bool initialCreate)
    {
        if (initialCreate || !_textureCreated)
        {
            _texture.Create(Image.Format.Rgba8, _width, _height, _depth, false, _imageList);
            _textureCreated = true;
        }
        else
        {
            _texture.Update(_imageList);
        }
    }
}
