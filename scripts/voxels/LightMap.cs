using System.Collections.Generic;
using Godot;

public class LightMap
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _depth;
    private readonly int _originX;
    private readonly int _originY;
    private readonly int _originZ;
    // RGBA8 per voxel: R = sun mask, G/B/A = block light R/G/B (post-pow at
    // deposit, summed per channel, byte-saturated for the GPU).
    private readonly byte[][] _slicePixels;
    private readonly Image[] _slices;
    private readonly Godot.Collections.Array<Image> _imageList;
    private readonly ImageTexture3D _texture;
    private bool _textureCreated;

    // Chunks whose stored pixel data needs re-encoding before the next upload.
    // Populated via MarkChunkDirty; drained by Flush. Kept across Flush calls
    // for any chunks that weren't visible at flush time so they encode later
    // when they come into view.
    private readonly HashSet<Vector3I> _dirtyChunks = new();

    public Vector3 Origin { get; }
    public Vector3 Size { get; }
    public ImageTexture3D Texture => _texture;

    public LightMap(WorldState world)
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
            _slicePixels[z] = new byte[_width * _height * 4];
            _slices[z] = Image.CreateFromData(_width, _height, false, Image.Format.Rgba8, _slicePixels[z]);
            _imageList.Add(_slices[z]);
        }

        _texture = new ImageTexture3D();
        // Initial encode of every chunk that exists in WorldState. Subsequent
        // updates go through Flush so only dirty + visible chunks re-encode.
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

    // Encodes any dirty chunk that's also in `visibleChunks`, then re-uploads
    // the texture. Chunks marked dirty but currently invisible stay in the
    // dirty set so they re-encode the first time they become visible.
    // visibleChunks must offer O(1) Contains — Dictionary.KeyCollection or
    // HashSet are appropriate. A linear-scan List would be quadratic here.
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
            // Outside the texture's extent; nothing to encode here.
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
                int rowOffset = ((baseY + ly) * _width + baseX) * 4;
                for (int lx = 0; lx < ChunkState.SIZE; lx++)
                {
                    int sun = (chunk.GetSunlight(lx, ly, lz) * 255) / LightEngine.MAX_LIGHT;
                    chunk.GetBlockLight(lx, ly, lz, out int br, out int bg, out int bb);
                    if (br > 255) { br = 255; }
                    if (bg > 255) { bg = 255; }
                    if (bb > 255) { bb = 255; }
                    int o = rowOffset + lx * 4;
                    pixels[o + 0] = (byte)sun;
                    pixels[o + 1] = (byte)br;
                    pixels[o + 2] = (byte)bg;
                    pixels[o + 3] = (byte)bb;
                }
            }
        }

        // Image objects are thin wrappers over the pixel arrays we own, so
        // a fresh CreateFromData picks up the mutated bytes without copying.
        // Replacing the slot in _slices keeps the Array<Image> in sync.
        _slices[baseZ] = Image.CreateFromData(_width, _height, false, Image.Format.Rgba8, _slicePixels[baseZ]);
        for (int lz = 1; lz < ChunkState.SIZE; lz++)
        {
            int sliceIdx = baseZ + lz;
            _slices[sliceIdx] = Image.CreateFromData(_width, _height, false, Image.Format.Rgba8, _slicePixels[sliceIdx]);
        }
        for (int lz = 0; lz < ChunkState.SIZE; lz++)
        {
            int sliceIdx = baseZ + lz;
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
            // Update reuses the existing GPU texture; cheaper than Create
            // (no reallocation), but still pushes the full slice set.
            _texture.Update(_imageList);
        }
    }
}
