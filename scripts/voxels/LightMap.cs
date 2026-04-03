using System;
using Godot;

public class LightMap
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _depth;
    private readonly int _originX;
    private readonly int _originY;
    private readonly int _originZ;
    private readonly byte[][] _slicePixels;
    private readonly Image[] _slices;
    private readonly ImageTexture3D _texture;

    public Vector3 Origin { get; }
    public Vector3 Size { get; }
    public ImageTexture3D Texture => _texture;

    public LightMap(WorldData world)
    {
        _originX = world.Min.X * ChunkData.SIZE;
        _originY = world.Min.Y * ChunkData.SIZE;
        _originZ = world.Min.Z * ChunkData.SIZE;

        _width = (world.Max.X + 1) * ChunkData.SIZE - _originX;
        _height = (world.Max.Y + 1) * ChunkData.SIZE - _originY;
        _depth = (world.Max.Z + 1) * ChunkData.SIZE - _originZ;

        Origin = new Vector3(_originX, _originY, _originZ);
        Size = new Vector3(_width, _height, _depth);

        _slicePixels = new byte[_depth][];
        _slices = new Image[_depth];
        for (int z = 0; z < _depth; z++)
        {
            _slicePixels[z] = new byte[_width * _height];
        }

        _texture = new ImageTexture3D();
        Update(world);
    }

    public void Update(WorldData world)
    {
        for (int cz = world.Min.Z; cz <= world.Max.Z; cz++)
        {
            for (int cy = world.Min.Y; cy <= world.Max.Y; cy++)
            {
                for (int cx = world.Min.X; cx <= world.Max.X; cx++)
                {
                    ChunkData chunk = world.GetChunk(new Vector3I(cx, cy, cz));
                    if (chunk == null)
                    {
                        continue;
                    }

                    int baseX = cx * ChunkData.SIZE - _originX;
                    int baseY = cy * ChunkData.SIZE - _originY;
                    int baseZ = cz * ChunkData.SIZE - _originZ;

                    for (int lz = 0; lz < ChunkData.SIZE; lz++)
                    {
                        byte[] pixels = _slicePixels[baseZ + lz];
                        for (int ly = 0; ly < ChunkData.SIZE; ly++)
                        {
                            int rowOffset = (baseY + ly) * _width + baseX;
                            for (int lx = 0; lx < ChunkData.SIZE; lx++)
                            {
                                int sun = chunk.GetSunlight(lx, ly, lz);
                                int block = chunk.GetBlockLight(lx, ly, lz);
                                int light = Math.Max(sun, block);
                                pixels[rowOffset + lx] = (byte)(light * 17);
                            }
                        }
                    }
                }
            }
        }

        var images = new Godot.Collections.Array<Image>();
        for (int z = 0; z < _depth; z++)
        {
            _slices[z] = Image.CreateFromData(_width, _height, false, Image.Format.R8, _slicePixels[z]);
            images.Add(_slices[z]);
        }
        _texture.Create(Image.Format.R8, _width, _height, _depth, false, images);
    }
}
