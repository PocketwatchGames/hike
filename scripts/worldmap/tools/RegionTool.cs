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

    public string[] Options(WorldMapState ctx)
    {
        var names = new string[ctx.RegionCount];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = ctx.RegionName(i);
        }
        return names;
    }

    public Color[] OptionColors(WorldMapState ctx) => null;

    public int OptionIndex
    {
        get => RegionIndex;
        set => RegionIndex = Mathf.Max(0, value);
    }

    public string HintText(WorldMapState ctx) => "";

    public Color CursorColor(WorldMapState ctx) => Colors.White;

    public string StatusText(WorldMapState ctx) => $"Region: {ctx.RegionName(RegionIndex)}";
    public string LevelText(WorldMapState ctx) => "";

    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
        // No eyedropper or constraint yet; this tool reads nothing off the map.
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        int max = Mathf.Max(0, ctx.RegionCount - 1);
        byte value = (byte)(erase ? 0 : Mathf.Clamp(RegionIndex, 0, max));

        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            Vector2I ct = ctx.Data.ColumnTexelToChunkTexel(px, pz);
            ctx.Region.SetPixel(ct.X, ct.Y, new Color(value / 255f, 0f, 0f, 1f));
        });
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
    public ESpawnPreview PreviewLayer => ESpawnPreview.None;
    public bool ShowsClimb => false;

    // Colour is a region index, so every step is worth a line.
    public bool ShowsAllSteps => true;
    public bool DrawsWater => false;

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
