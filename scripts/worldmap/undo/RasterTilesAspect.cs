using System.Collections.Generic;
using Godot;

// Every layer IMAGE, snapshotted a tile at a time.
//
// Tiled rather than whole-layer because a stroke is local: a brush touching a
// few tiles should cost a few kilobytes, not a copy of every layer in the
// document. The tile is one chunk square, which is the grid the painter already
// rebuilds and expands rects against.
//
// All layers are snapshotted over the touched region, not just the one the
// active tool writes, because the host does the touching and does not know which
// that is — and that ignorance is the point: a tool cannot forget to declare
// something it never had to declare. Commit throws away every tile that did not
// change, so the memory kept is proportional to what actually moved.
public sealed class RasterTilesAspect : IMapEditAspect
{
    // Layer index and tile coordinate. The index is positional in
    // WorldMapState.RasterLayers, so it is only ever valid inside one session's
    // history — which is all an undo stack is.
    private readonly struct Key
    {
        public readonly int Layer;
        public readonly Vector2I Tile;

        public Key(int layer, Vector2I tile)
        {
            Layer = layer;
            Tile = tile;
        }

        public override int GetHashCode() => System.HashCode.Combine(Layer, Tile);

        public override bool Equals(object obj) => obj is Key k && k.Layer == Layer && k.Tile == Tile;
    }

    private Dictionary<Key, Image> _before = new Dictionary<Key, Image>();
    private readonly Dictionary<Key, Image> _after = new Dictionary<Key, Image>();

    public void Touch(WorldMapState ctx, Rect2I texelRect)
    {
        RasterLayer[] layers = ctx.RasterLayers();
        for (int i = 0; i < layers.Length; i++)
        {
            RasterLayer layer = layers[i];
            if (layer.Image == null)
            {
                continue;
            }
            // A per-chunk layer is indexed in chunks, so the touched texel
            // region has to be divided down to reach the same ground.
            Rect2I rect = Scale(texelRect, layer.TexelsPerPixel);
            foreach (Vector2I tile in Tiles(rect, layer.Image))
            {
                var key = new Key(i, tile);
                if (_before.ContainsKey(key))
                {
                    continue;
                }
                _before[key] = layer.Image.GetRegion(TileRect(tile, layer.Image));
            }
        }
    }

    public bool CaptureAfter(WorldMapState ctx)
    {
        RasterLayer[] layers = ctx.RasterLayers();
        var changed = new Dictionary<Key, Image>(_before.Count);
        foreach (KeyValuePair<Key, Image> kvp in _before)
        {
            Image img = layers[kvp.Key.Layer].Image;
            if (img == null)
            {
                continue;
            }
            Image now = img.GetRegion(TileRect(kvp.Key.Tile, img));
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
        RasterLayer[] layers = ctx.RasterLayers();
        Dictionary<Key, Image> source = redo ? _after : _before;
        foreach (KeyValuePair<Key, Image> kvp in source)
        {
            Image img = layers[kvp.Key.Layer].Image;
            if (img == null)
            {
                continue;
            }
            Image tile = kvp.Value;
            img.BlitRect(tile, new Rect2I(Vector2I.Zero, tile.GetSize()), TileRect(kvp.Key.Tile, img).Position);
        }
    }

    private static bool Same(Image a, Image b)
    {
        byte[] da = a.GetData();
        byte[] db = b.GetData();
        if (da.Length != db.Length)
        {
            return false;
        }
        for (int i = 0; i < da.Length; i++)
        {
            if (da[i] != db[i])
            {
                return false;
            }
        }
        return true;
    }

    private static Rect2I Scale(Rect2I rect, int texelsPerPixel)
    {
        if (texelsPerPixel <= 1)
        {
            return rect;
        }
        int x0 = rect.Position.X / texelsPerPixel;
        int z0 = rect.Position.Y / texelsPerPixel;
        int x1 = (rect.Position.X + rect.Size.X + texelsPerPixel - 1) / texelsPerPixel;
        int z1 = (rect.Position.Y + rect.Size.Y + texelsPerPixel - 1) / texelsPerPixel;
        return new Rect2I(x0, z0, Mathf.Max(1, x1 - x0), Mathf.Max(1, z1 - z0));
    }

    // Tiles are clipped to the image, so an edge tile is smaller and BlitRect
    // still lands inside.
    private static Rect2I TileRect(Vector2I tile, Image img)
    {
        int x = tile.X * ChunkState.SIZE;
        int z = tile.Y * ChunkState.SIZE;
        return new Rect2I(x, z,
            Mathf.Min(ChunkState.SIZE, img.GetWidth() - x),
            Mathf.Min(ChunkState.SIZE, img.GetHeight() - z));
    }

    private static IEnumerable<Vector2I> Tiles(Rect2I rect, Image img)
    {
        int tx0 = Mathf.Max(0, rect.Position.X / ChunkState.SIZE);
        int tz0 = Mathf.Max(0, rect.Position.Y / ChunkState.SIZE);
        int tx1 = Mathf.Min((img.GetWidth() - 1) / ChunkState.SIZE, (rect.Position.X + rect.Size.X - 1) / ChunkState.SIZE);
        int tz1 = Mathf.Min((img.GetHeight() - 1) / ChunkState.SIZE, (rect.Position.Y + rect.Size.Y - 1) / ChunkState.SIZE);
        for (int tx = tx0; tx <= tx1; tx++)
        {
            for (int tz = tz0; tz <= tz1; tz++)
            {
                yield return new Vector2I(tx, tz);
            }
        }
    }
}
