using Godot;

// Paints the per-chunk region index. Cycle selects the region to paint; erase
// clears to 0 (no region / border). The view shows region colours, darkened in
// ocean.
public class RegionTool : IWorldMapTool
{
    public string Name => "Region";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 24f;

    public int RegionIndex = 1;

    public RegionTool()
    {
        View = new RegionView();
    }

    public string StatusText(WorldMapState ctx) => $"Region {RegionIndex}";
    public string LevelText(WorldMapState ctx) => "";

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        int max = Mathf.Max(0, ctx.RegionCount - 1);
        byte value = (byte)(erase ? 0 : Mathf.Clamp(RegionIndex, 0, max));

        int minCx = int.MaxValue, minCz = int.MaxValue, maxCx = int.MinValue, maxCz = int.MinValue;
        bool any = false;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            Vector2I ct = ctx.Data.ColumnTexelToChunkTexel(px, pz);
            ctx.Region.SetPixel(ct.X, ct.Y, new Color(value / 255f, 0f, 0f, 1f));
            any = true;
            minCx = Mathf.Min(minCx, ct.X);
            minCz = Mathf.Min(minCz, ct.Y);
            maxCx = Mathf.Max(maxCx, ct.X);
            maxCz = Mathf.Max(maxCz, ct.Y);
        });
        if (any)
        {
            ctx.RebakeRegion(new Rect2I(minCx, minCz, maxCx - minCx + 1, maxCz - minCz + 1));
        }
    }

    public void Cycle(WorldMapState ctx, int dir)
    {
        if (ctx.RegionCount > 0)
        {
            RegionIndex = (RegionIndex + dir + ctx.RegionCount) % ctx.RegionCount;
        }
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
    }
}

// Region colour per chunk; 50% darker where the column is open ocean.
public class RegionView : IWorldMapView
{
    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        Vector2I ct = ctx.Data.ColumnTexelToChunkTexel(px, pz);
        int idx = Mathf.RoundToInt(ctx.Region.GetPixel(ct.X, ct.Y).R * 255f);
        Color c = WorldMapState.RegionColor(idx);
        if (ctx.Ocean(px, pz))
        {
            c = new Color(c.R * 0.5f, c.G * 0.5f, c.B * 0.5f);
        }
        return c;
    }
}
