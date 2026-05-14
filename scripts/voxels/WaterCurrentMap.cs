using System.Collections.Generic;
using Godot;

// Coarse water-current 3D texture. Mirrors WindMap's ImageTexture3D
// pattern, but stores a 2-channel signed vector per cell (R = current X,
// G = current Z, byte-encoded with 128 = zero so the shader does
// `texture(...).rg * 2.0 - 1.0` to recover [-1, 1]). Sampled in world
// space by the water shader's ripple_normal to advect the surface
// pattern in the direction of flow.
//
// Sized to the full WorldState voxel extent — same origin/inv_size
// convention as LightMap / FogMap / WindMap so a single
// `(world_pos - origin) * inv_size` UVW expression works regardless of
// which map is sampled. Cell footprint is ENV_VOXELS_PER_CELL on each
// axis (currently 4³ voxels per cell).
public class WaterCurrentMap
{
    private const int CELL = ChunkState.ENV_VOXELS_PER_CELL;
    private const int CELLS_PER_CHUNK = ChunkState.ENV_SUBGRID_SIZE;

    private readonly int _width;
    private readonly int _height;
    private readonly int _depth;
    private readonly int _originCellX;
    private readonly int _originCellY;
    private readonly int _originCellZ;
    // Interleaved RG bytes per cell: pixel n at offset n*2 (R = currentX,
    // R+1 = currentZ).
    private readonly byte[][] _slicePixels;
    private readonly Image[] _slices;
    private readonly Godot.Collections.Array<Image> _imageList;
    private readonly ImageTexture3D _texture;
    private bool _textureCreated;

    private readonly HashSet<Vector3I> _dirtyChunks = new();

    public Vector3 Origin { get; }
    public Vector3 Size { get; }
    public ImageTexture3D Texture => _texture;

    public WaterCurrentMap(WorldState world)
    {
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
            // Two bytes per cell (R + G). Default 0 means "max negative
            // current" under the byte-128-is-zero convention, so seed
            // every byte to 128 = signed zero before first upload.
            _slicePixels[z] = new byte[_width * _height * 2];
            for (int i = 0; i < _slicePixels[z].Length; i++)
            {
                _slicePixels[z][i] = 128;
            }
            _slices[z] = Image.CreateFromData(_width, _height, false, Image.Format.Rg8, _slicePixels[z]);
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
                int rowOffset = ((baseY + sy) * _width + baseX) * 2;
                for (int sx = 0; sx < CELLS_PER_CHUNK; sx++)
                {
                    int o = rowOffset + sx * 2;
                    pixels[o + 0] = chunk.CurrentX[sx, sy, sz];
                    pixels[o + 1] = chunk.CurrentZ[sx, sy, sz];
                }
            }
        }

        for (int sz = 0; sz < CELLS_PER_CHUNK; sz++)
        {
            int sliceIdx = baseZ + sz;
            _slices[sliceIdx] = Image.CreateFromData(_width, _height, false, Image.Format.Rg8, _slicePixels[sliceIdx]);
            _imageList[sliceIdx] = _slices[sliceIdx];
        }
    }

    private void Upload(bool initialCreate)
    {
        if (initialCreate || !_textureCreated)
        {
            _texture.Create(Image.Format.Rg8, _width, _height, _depth, false, _imageList);
            _textureCreated = true;
        }
        else
        {
            _texture.Update(_imageList);
        }
    }
}
