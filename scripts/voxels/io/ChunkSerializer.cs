using System.Collections.Generic;
using System.IO;
using Godot;

// Single-chunk binary encode/decode. Layout per blob:
//   voxels  : 4096 bytes  (raw VoxelType byte per cell, SIZE^3 row-major X,Y,Z)
//   light   : 4096 bytes  (raw packed nibble byte per cell — matches ChunkState.Light)
//   entities: type-tagged list (see EntitySerializer)
public static class ChunkSerializer
{
    public const int VOXEL_BYTES = ChunkState.SIZE * ChunkState.SIZE * ChunkState.SIZE;
    public const int LIGHT_BYTES = ChunkState.SIZE * ChunkState.SIZE * ChunkState.SIZE;

    public static void Write(BinaryWriter w, ChunkState chunk, List<EntitySimState> entities)
    {
        // Voxels
        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    w.Write((byte)chunk.Voxels[x, y, z]);
                }
            }
        }

        // Light (packed nibbles already)
        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    w.Write(chunk.Light[x, y, z]);
                }
            }
        }

        EntitySerializer.WriteList(w, entities);
    }

    public static void Read(BinaryReader r, Vector3I coord, out ChunkState chunk, out List<EntitySimState> entities)
    {
        chunk = new ChunkState(coord);

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    chunk.Voxels[x, y, z] = (VoxelType)r.ReadByte();
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    chunk.Light[x, y, z] = r.ReadByte();
                }
            }
        }

        entities = EntitySerializer.ReadList(r);
    }
}
