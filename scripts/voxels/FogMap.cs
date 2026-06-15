using System.Collections.Generic;
using Godot;

// Mirrors LightMap but encodes ChunkState.FogDensity into a single-channel
// ImageTexture3D that the fog raymarch shader samples via the fog_map
// shader global. R8 format — one byte per voxel, 1/4 the memory of RGBA8,
// and our shader reads .r directly so we control the sampling path. Full-
// world extent matches LightMap.
public class FogMap
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _depth;
    private readonly int _originX;
    private readonly int _originY;
    private readonly int _originZ;
    private readonly byte[][] _slicePixels;
    private readonly Image[] _slices;
    private readonly Godot.Collections.Array<Image> _imageList;
    private readonly ImageTexture3D _texture;
    private bool _textureCreated;

    private readonly HashSet<Vector3I> _dirtyChunks = new();

    public Vector3 Origin { get; }
    public Vector3 Size { get; }
    public ImageTexture3D Texture => _texture;

    public FogMap(WorldState world)
    {
        _originX = world.Min.X * ChunkState.SIZE;
        _originY = world.Min.Y * ChunkState.SIZE;
        _originZ = world.Min.Z * ChunkState.SIZE;

        _width = (world.Max.X + 1) * ChunkState.SIZE - _originX;
        _height = (world.Max.Y + 1) * ChunkState.SIZE - _originY;
        _depth = (world.Max.Z + 1) * ChunkState.SIZE - _originZ;

        Origin = new Vector3(_originX, _originY, _originZ);
        Size = new Vector3(_width, _height, _depth);

        _slicePixels = new byte[_depth][];
        _slices = new Image[_depth];
        _imageList = new Godot.Collections.Array<Image>();
        for (int z = 0; z < _depth; z++)
        {
            _slicePixels[z] = new byte[_width * _height];
            _slices[z] = Image.CreateFromData(_width, _height, false, Image.Format.R8, _slicePixels[z]);
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

        int baseX = coord.X * ChunkState.SIZE - _originX;
        int baseY = coord.Y * ChunkState.SIZE - _originY;
        int baseZ = coord.Z * ChunkState.SIZE - _originZ;

        for (int lz = 0; lz < ChunkState.SIZE; lz++)
        {
            byte[] pixels = _slicePixels[baseZ + lz];
            for (int ly = 0; ly < ChunkState.SIZE; ly++)
            {
                int rowOffset = (baseY + ly) * _width + baseX;
                for (int lx = 0; lx < ChunkState.SIZE; lx++)
                {
                    pixels[rowOffset + lx] = chunk.FogDensity[lx, ly, lz];
                }
            }
        }

        for (int lz = 0; lz < ChunkState.SIZE; lz++)
        {
            int sliceIdx = baseZ + lz;
            _slices[sliceIdx] = Image.CreateFromData(_width, _height, false, Image.Format.R8, _slicePixels[sliceIdx]);
            _imageList[sliceIdx] = _slices[sliceIdx];
        }
    }

    private void Upload(bool initialCreate)
    {
        if (initialCreate || !_textureCreated)
        {
            _texture.Create(Image.Format.R8, _width, _height, _depth, false, _imageList);
            _textureCreated = true;
        }
        else
        {
            _texture.Update(_imageList);
        }
    }
}
