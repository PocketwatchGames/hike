using Godot;

// Paints the per-chunk zone (biome) index. Cycle selects the zone to paint.
// The view shows zone colours modulated by an elevation value gradient.
public class ZoneTool : IWorldMapTool
{
    public string Name => "Zone";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 24f;

    public int ZoneIndex = 0;

    public ZoneTool()
    {
        View = new ZoneView();
    }

    public string StatusText(WorldMapState ctx) => $"Zone {ZoneIndex}";
    public string LevelText(WorldMapState ctx) => "";

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        int max = Mathf.Max(0, ctx.ZoneCount - 1);
        byte value = (byte)(erase ? 0 : Mathf.Clamp(ZoneIndex, 0, max));

        int minCx = int.MaxValue, minCz = int.MaxValue, maxCx = int.MinValue, maxCz = int.MinValue;
        bool any = false;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            Vector2I ct = ctx.Data.ColumnTexelToChunkTexel(px, pz);
            ctx.Zone.SetPixel(ct.X, ct.Y, new Color(value / 255f, 0f, 0f, 1f));
            any = true;
            minCx = Mathf.Min(minCx, ct.X);
            minCz = Mathf.Min(minCz, ct.Y);
            maxCx = Mathf.Max(maxCx, ct.X);
            maxCz = Mathf.Max(maxCz, ct.Y);
        });
        if (any)
        {
            ctx.RebakeZone(new Rect2I(minCx, minCz, maxCx - minCx + 1, maxCz - minCz + 1));
        }
    }

    public void Cycle(WorldMapState ctx, int dir)
    {
        if (ctx.ZoneCount > 0)
        {
            ZoneIndex = (ZoneIndex + dir + ctx.ZoneCount) % ctx.ZoneCount;
        }
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
    }
}

// Zone colour per chunk, brightness modulated by the column's elevation.
public class ZoneView : IWorldMapView
{
    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        Vector2I ct = ctx.Data.ColumnTexelToChunkTexel(px, pz);
        int idx = Mathf.RoundToInt(ctx.Zone.GetPixel(ct.X, ct.Y).R * 255f);
        Color c = WorldMapState.ZoneColor(idx);
        float b = 0.35f + 0.65f * ctx.Elevation01(px, pz);
        return new Color(c.R * b, c.G * b, c.B * b);
    }
}
