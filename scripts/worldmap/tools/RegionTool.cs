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

    // The same colour the view washes a chunk in, so the row you pick from and
    // the map you pick for cannot disagree.
    public Color[] OptionColors(WorldMapInk ink)
    {
        var colors = new Color[ink.Map.RegionCount];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = WorldMapInk.RegionColor(i);
        }
        return colors;
    }

    public int OptionIndex
    {
        get => RegionIndex;
        set => RegionIndex = Mathf.Max(0, value);
    }

    public string HintText(WorldMapState ctx) => "";

    public Color CursorColor(WorldMapInk ink) => WorldMapInk.RegionColor(RegionIndex);

    public string StatusText(WorldMapState ctx, WorldMapView view) => $"Region: {ctx.RegionName(RegionIndex)}";
    public string LevelText(WorldMapState ctx, WorldMapView view) => "";

    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
        // No eyedropper or constraint yet; this tool reads nothing off the map.
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        int max = Mathf.Max(0, ctx.RegionCount - 1);
        byte value = (byte)(erase ? 0 : Mathf.Clamp(RegionIndex, 0, max));

        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            Vector2I ct = ctx.Data.ColumnTexelToChunkTexel(px, pz);
            ctx.Region.SetPixel(ct.X, ct.Y, new Color(value / 255f, 0f, 0f, 1f));
        });
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;
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

    // Colour is a region index, so every step is worth a line.
    public bool ShowsAllSteps => true;
    // Water is not composited over the region wash, but it IS shown — every
    // submerged column is darkened below. That counts: the outlines follow the
    // water surface (the seabed's shape is no more readable under a flat
    // darkened wash than under an opaque blue one) and spill edges are inked,
    // so a region map answers "where does this water pour" like every other map
    // that shows water.
    public bool DrawsWater => true;

    public Color ColorAt(WorldMapInk ink, int px, int pz)
    {
        Vector2I ct = ink.Map.Data.ColumnTexelToChunkTexel(px, pz);
        int idx = Mathf.RoundToInt(ink.Map.Region.GetPixel(ct.X, ct.Y).R * 255f);
        Color c = WorldMapInk.RegionColor(idx);
        if (ink.Map.Underwater(px, pz))
        {
            c = new Color(c.R * 0.5f, c.G * 0.5f, c.B * 0.5f);
        }
        return c;
    }
}
