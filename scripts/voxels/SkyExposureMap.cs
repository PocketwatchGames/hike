using System.Collections.Generic;
using Godot;

// Mirrors FogMap but encodes ChunkState.SkyExposure (the non-leaky VERTICAL
// sky reach — see ChunkState.SkyExposure) into a single-channel R8
// ImageTexture3D, exposed to shaders via the `sky_exposure_map` global. The
// rain shader samples it to clip falling drops at the true overhead-cover line
// (roof / overhang / cave ceiling / dense canopy) instead of the BFS sun mask,
// whose horizontal leak bleeds drops into cave mouths. SkyExposure is stored
// 0..LightEngine.MAX_LIGHT; encoded here to 0..255 so the GPU reads a smooth
// 0..1. Full-world extent matches LightMap; the roadmap's sliding-window
// refactor applies to both equally.
public class SkyExposureMap
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

    public SkyExposureMap(WorldState world)
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
                    int sky = (chunk.GetSkyExposure(lx, ly, lz) * 255) / LightEngine.MAX_LIGHT;
                    pixels[rowOffset + lx] = (byte)sky;
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
