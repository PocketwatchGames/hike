using System.Collections.Generic;
using Godot;

// The tunnel carve mask, snapshotted as whole-height columns over a tile.
//
// A separate aspect from the images because the mask is the one layer that is
// 3D — and the one where a whole-layer snapshot would be genuinely expensive
// (a 288x256 map is ~6MB of mask). A tile is one chunk square by the world's
// full height: the tunnel brush carves a slice at a cross-section and the tool
// can move that cross-section mid-stroke, so taking the column entire costs one
// snapshot instead of a per-slice bookkeeping problem.
public sealed class TunnelTilesAspect : IMapEditAspect
{
    private Dictionary<Vector2I, byte[,,]> _before = new Dictionary<Vector2I, byte[,,]>();
    private readonly Dictionary<Vector2I, byte[,,]> _after = new Dictionary<Vector2I, byte[,,]>();

    public void Touch(WorldMapState ctx, Rect2I texelRect)
    {
        if (ctx.Tunnels == null)
        {
            return;
        }
        int tx0 = Mathf.Max(0, texelRect.Position.X / ChunkState.SIZE);
        int tz0 = Mathf.Max(0, texelRect.Position.Y / ChunkState.SIZE);
        int tx1 = Mathf.Min((ctx.Data.ImageWidth - 1) / ChunkState.SIZE, (texelRect.Position.X + texelRect.Size.X - 1) / ChunkState.SIZE);
        int tz1 = Mathf.Min((ctx.Data.ImageHeight - 1) / ChunkState.SIZE, (texelRect.Position.Y + texelRect.Size.Y - 1) / ChunkState.SIZE);
        for (int tx = tx0; tx <= tx1; tx++)
        {
            for (int tz = tz0; tz <= tz1; tz++)
            {
                var tile = new Vector2I(tx, tz);
                if (!_before.ContainsKey(tile))
                {
                    _before[tile] = Capture(ctx, tile);
                }
            }
        }
    }

    public bool CaptureAfter(WorldMapState ctx)
    {
        var changed = new Dictionary<Vector2I, byte[,,]>(_before.Count);
        foreach (KeyValuePair<Vector2I, byte[,,]> kvp in _before)
        {
            byte[,,] now = Capture(ctx, kvp.Key);
            if (Same(now, kvp.Value))
            {
                continue;
            }
            changed[kvp.Key] = kvp.Value;
            _after[kvp.Key] = now;
        }
        _before = changed;
        return _before.Count > 0;
    }

    public void Restore(WorldMapState ctx, bool redo)
    {
        Dictionary<Vector2I, byte[,,]> source = redo ? _after : _before;
        foreach (KeyValuePair<Vector2I, byte[,,]> kvp in source)
        {
            Write(ctx, kvp.Key, kvp.Value);
        }
    }

    private static byte[,,] Capture(WorldMapState ctx, Vector2I tile)
    {
        int h = ctx.Data.VoxelHeight;
        var slab = new byte[ChunkState.SIZE, h, ChunkState.SIZE];
        int ox = tile.X * ChunkState.SIZE;
        int oz = tile.Y * ChunkState.SIZE;
        for (int x = 0; x < ChunkState.SIZE && ox + x < ctx.Data.ImageWidth; x++)
        {
            for (int z = 0; z < ChunkState.SIZE && oz + z < ctx.Data.ImageHeight; z++)
            {
                for (int y = 0; y < h; y++)
                {
                    slab[x, y, z] = ctx.Tunnels[ox + x, y, oz + z];
                }
            }
        }
        return slab;
    }

    private static void Write(WorldMapState ctx, Vector2I tile, byte[,,] slab)
    {
        int h = ctx.Data.VoxelHeight;
        int ox = tile.X * ChunkState.SIZE;
        int oz = tile.Y * ChunkState.SIZE;
        for (int x = 0; x < ChunkState.SIZE && ox + x < ctx.Data.ImageWidth; x++)
        {
            for (int z = 0; z < ChunkState.SIZE && oz + z < ctx.Data.ImageHeight; z++)
            {
                for (int y = 0; y < h; y++)
                {
                    ctx.Tunnels[ox + x, y, oz + z] = slab[x, y, z];
                }
            }
        }
    }

    private static bool Same(byte[,,] a, byte[,,] b)
    {
        for (int x = 0; x < a.GetLength(0); x++)
        {
            for (int y = 0; y < a.GetLength(1); y++)
            {
                for (int z = 0; z < a.GetLength(2); z++)
                {
                    if (a[x, y, z] != b[x, y, z])
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }
}
