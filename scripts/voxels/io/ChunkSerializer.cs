using System.Collections.Generic;
using System.IO;
using Godot;

// Single-chunk binary encode/decode. Layout per blob:
//   voxels   : 4096 bytes (raw VoxelType byte per cell, SIZE^3 row-major X,Y,Z)
//   shape    : 4096 bytes (SharpAxes byte per cell — the mesher's sharp-axis tag)
//   sunlight : 4096 bytes (one byte per cell, value 0-15)
//   fog      : 4096 bytes (one byte per cell, 0 = clear, 255 = thickest)
//   kitId    : 4096 bytes (environment-kit index per cell)
//   overlay  : 4096 bytes (authored per-voxel overlay id; 0 = none)
//   detailGroup    : 4096 bytes (1-based DetailGroups index; 0 = none)
//   detailStrength : 4096 bytes (0..255 scatter density)
//   regionIndex    : 1 byte (index into WorldState.Regions[])
//   entities : type-tagged list (see EntitySerializer)
//
// BlockLight is NOT serialized — it's the additive sum of contributions from
// LightSources, and it's recomputed on world load when each torch entity
// spawns and registers itself.
//
// Wire-format additions must be APPENDED, never inserted mid-blob, so the
// WorldFile's per-chunk (offset, length) index remains valid and old chunk
// payloads stay readable after a version bump. See WorldFile.VERSION.
public static class ChunkSerializer
{
    public const int VOXEL_BYTES = ChunkState.SIZE * ChunkState.SIZE * ChunkState.SIZE;
    public const int SHAPE_BYTES = ChunkState.SIZE * ChunkState.SIZE * ChunkState.SIZE;
    public const int SUNLIGHT_BYTES = ChunkState.SIZE * ChunkState.SIZE * ChunkState.SIZE;
    public const int FOG_BYTES = ChunkState.SIZE * ChunkState.SIZE * ChunkState.SIZE;
    public const int KIT_BYTES = ChunkState.SIZE * ChunkState.SIZE * ChunkState.SIZE;
    public const int OVERLAY_BYTES = ChunkState.SIZE * ChunkState.SIZE * ChunkState.SIZE;
    public const int DETAIL_GROUP_BYTES = ChunkState.SIZE * ChunkState.SIZE * ChunkState.SIZE;
    public const int DETAIL_STRENGTH_BYTES = ChunkState.SIZE * ChunkState.SIZE * ChunkState.SIZE;

    public static void Write(BinaryWriter w, ChunkState chunk, List<EntitySimState> entities)
    {
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

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    w.Write(chunk.Shape[x, y, z]);
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    w.Write(chunk.Sunlight[x, y, z]);
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    w.Write(chunk.FogDensity[x, y, z]);
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    w.Write(chunk.KitId[x, y, z]);
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    w.Write(chunk.OverlayId[x, y, z]);
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    w.Write(chunk.DetailGroup[x, y, z]);
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    w.Write(chunk.DetailStrength[x, y, z]);
                }
            }
        }

        w.Write(chunk.RegionIndex);

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
                    chunk.Shape[x, y, z] = r.ReadByte();
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    chunk.Sunlight[x, y, z] = r.ReadByte();
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    chunk.FogDensity[x, y, z] = r.ReadByte();
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    chunk.KitId[x, y, z] = r.ReadByte();
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    chunk.OverlayId[x, y, z] = r.ReadByte();
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    chunk.DetailGroup[x, y, z] = r.ReadByte();
                }
            }
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    chunk.DetailStrength[x, y, z] = r.ReadByte();
                }
            }
        }

        chunk.RegionIndex = r.ReadByte();

        entities = EntitySerializer.ReadList(r);
    }
}
