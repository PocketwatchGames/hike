using System;
using System.Collections.Generic;
using System.IO;
using Godot;

// Single-chunk binary encode/decode. Layout per blob:
//   voxels   : 4096 bytes (raw int byte per cell, SIZE^3 row-major X,Y,Z)
//   shape    : 4096 bytes (SharpAxes byte per cell — the mesher's sharp-axis tag)
//   sunlight : 4096 bytes (one byte per cell, value 0-15)
//   fog      : 4096 bytes (one byte per cell, 0 = clear, 255 = thickest)
//   TerrainId    : 4096 bytes (environment-kit index per cell)
//   overlay  : 4096 bytes (authored per-voxel overlay id; 0 = none)
//   overlayFaces   : 1 byte present-flag, then 4096 bytes ONLY if set (which
//                    of each voxel's six faces the overlay dresses, EVoxelFace
//                    bits; 0 = all). Optional because it is sparse — see
//                    ChunkState.OverlayFaces. Written last, after the entity
//                    list, per the append-only rule below.
//   detailGroup    : 4096 bytes (1-based DetailGroups index; 0 = none)
//   detailStrength : 4096 bytes (0..255 scatter density)
//   windFactor     : 64 bytes (ENV_SUBGRID_SIZE^3 byte cells, 0 = no wind,
//                    255 = full ambient — coarse subgrid, X,Y,Z row-major)
//   envTag         : 64 bytes (ENV_SUBGRID_SIZE^3 byte cells, index into
//                    SimData.interiorAmbiences — same row-major layout as
//                    windFactor. Indices 0..3 are pinned to the original
//                    Outdoor/Building/Cave/Tunnel classes so files written
//                    before the palette existed still mean what they said)
//   currentX       : 64 bytes (ENV_SUBGRID_SIZE^3 byte cells, signed water-
//                    current X component encoded as (byte - 128) / 127 in
//                    world-XZ velocity normalized units)
//   currentZ       : 64 bytes (same as currentX, for the Z component)
//   windVelocityX  : 64 bytes (ENV_SUBGRID_SIZE^3 byte cells, signed wind
//                    velocity X component, same byte-128-zero encoding as
//                    currentX; multiplied by `wind_velocity_scale` global
//                    in shader to convert to world m/s)
//   windVelocityY  : 64 bytes (Y component — wind has updrafts unlike
//                    water currents, so all three axes are stored)
//   windVelocityZ  : 64 bytes (Z component)
//   zoneIndex    : 1 byte (index into WorldState.Zones[])
//   regionIndex  : 1 byte (index into WorldState.Regions[])
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
    public const int WIND_BYTES = ChunkState.ENV_SUBGRID_SIZE * ChunkState.ENV_SUBGRID_SIZE * ChunkState.ENV_SUBGRID_SIZE;
    public const int ENV_TAG_BYTES = ChunkState.ENV_SUBGRID_SIZE * ChunkState.ENV_SUBGRID_SIZE * ChunkState.ENV_SUBGRID_SIZE;
    public const int CURRENT_BYTES = ChunkState.ENV_SUBGRID_SIZE * ChunkState.ENV_SUBGRID_SIZE * ChunkState.ENV_SUBGRID_SIZE;
    public const int WIND_VELOCITY_BYTES = ChunkState.ENV_SUBGRID_SIZE * ChunkState.ENV_SUBGRID_SIZE * ChunkState.ENV_SUBGRID_SIZE;

    // One CHANNEL at a time, not one voxel at a time. A byte[,,] is contiguous
    // in exactly the X,Y,Z row-major order the per-voxel loops walked, so a
    // BlockCopy into a scratch buffer plus a single Write is the identical wire
    // format — where the loops cost ~57k BinaryWriter.Write calls per chunk,
    // across every chunk, on every write AND every load.
    [ThreadStatic] private static byte[] _scratch;

    private static byte[] Scratch(int bytes)
    {
        if (_scratch == null || _scratch.Length < bytes)
        {
            _scratch = new byte[bytes];
        }
        return _scratch;
    }

    private static void WriteChannel(BinaryWriter w, Array channel, int bytes)
    {
        byte[] buffer = Scratch(bytes);
        Buffer.BlockCopy(channel, 0, buffer, 0, bytes);
        w.Write(buffer, 0, bytes);
    }

    // Loops because a Stream may hand back a short read; BinaryReader does not
    // promise to fill the buffer in one call.
    private static void ReadChannel(BinaryReader r, Array channel, int bytes)
    {
        byte[] buffer = Scratch(bytes);
        int filled = 0;
        while (filled < bytes)
        {
            int got = r.Read(buffer, filled, bytes - filled);
            if (got <= 0)
            {
                throw new EndOfStreamException($"ChunkSerializer: chunk payload ended {bytes - filled} bytes short");
            }
            filled += got;
        }
        Buffer.BlockCopy(buffer, 0, channel, 0, bytes);
    }

    public static void Write(BinaryWriter w, ChunkState chunk, List<EntitySimState> entities)
    {
        WriteChannel(w, chunk.Voxels, VOXEL_BYTES);
        WriteChannel(w, chunk.Shape, SHAPE_BYTES);
        WriteChannel(w, chunk.Sunlight, SUNLIGHT_BYTES);
        WriteChannel(w, chunk.FogDensity, FOG_BYTES);
        WriteChannel(w, chunk.TerrainId, KIT_BYTES);
        WriteChannel(w, chunk.OverlayId, OVERLAY_BYTES);
        WriteChannel(w, chunk.DetailGroup, DETAIL_GROUP_BYTES);
        WriteChannel(w, chunk.DetailStrength, DETAIL_STRENGTH_BYTES);
        WriteChannel(w, chunk.Interiorness, WIND_BYTES);
        WriteChannel(w, chunk.EnvTag, ENV_TAG_BYTES);
        WriteChannel(w, chunk.CurrentX, CURRENT_BYTES);
        WriteChannel(w, chunk.CurrentZ, CURRENT_BYTES);
        WriteChannel(w, chunk.WindVelocityX, WIND_VELOCITY_BYTES);
        WriteChannel(w, chunk.WindVelocityY, WIND_VELOCITY_BYTES);
        WriteChannel(w, chunk.WindVelocityZ, WIND_VELOCITY_BYTES);

        w.Write(chunk.ZoneIndex);
        w.Write(chunk.RegionIndex);

        EntitySerializer.WriteList(w, entities);

        // Trails the entity list only because the append-only rule above puts
        // every addition at the end of the blob, never mid-stream.
        bool hasOverlayFaces = chunk.OverlayFaces != null;
        w.Write(hasOverlayFaces);
        if (hasOverlayFaces)
        {
            WriteChannel(w, chunk.OverlayFaces, OVERLAY_BYTES);
        }
    }

    // `pathTable` is the containing file's shared resource-path table when the
    // chunk was written under EntitySerializer.BeginSharedWrite; null means the
    // chunk's entity list carries its own.
    public static void Read(BinaryReader r, Vector3I coord, out ChunkState chunk, out List<EntitySimState> entities, EntitySerializer.ReadPathTable pathTable = null)
    {
        chunk = new ChunkState(coord);

        ReadChannel(r, chunk.Voxels, VOXEL_BYTES);
        ReadChannel(r, chunk.Shape, SHAPE_BYTES);
        ReadChannel(r, chunk.Sunlight, SUNLIGHT_BYTES);
        chunk.MarkSunlightChanged();
        ReadChannel(r, chunk.FogDensity, FOG_BYTES);
        ReadChannel(r, chunk.TerrainId, KIT_BYTES);
        ReadChannel(r, chunk.OverlayId, OVERLAY_BYTES);
        ReadChannel(r, chunk.DetailGroup, DETAIL_GROUP_BYTES);
        ReadChannel(r, chunk.DetailStrength, DETAIL_STRENGTH_BYTES);
        ReadChannel(r, chunk.Interiorness, WIND_BYTES);
        ReadChannel(r, chunk.EnvTag, ENV_TAG_BYTES);
        ReadChannel(r, chunk.CurrentX, CURRENT_BYTES);
        ReadChannel(r, chunk.CurrentZ, CURRENT_BYTES);
        ReadChannel(r, chunk.WindVelocityX, WIND_VELOCITY_BYTES);
        ReadChannel(r, chunk.WindVelocityY, WIND_VELOCITY_BYTES);
        ReadChannel(r, chunk.WindVelocityZ, WIND_VELOCITY_BYTES);

        chunk.ZoneIndex = r.ReadByte();
        chunk.RegionIndex = r.ReadByte();

        entities = EntitySerializer.ReadList(r, pathTable);

        if (r.ReadBoolean())
        {
            chunk.OverlayFaces = new byte[ChunkState.SIZE, ChunkState.SIZE, ChunkState.SIZE];
            ReadChannel(r, chunk.OverlayFaces, OVERLAY_BYTES);
        }
    }
}
