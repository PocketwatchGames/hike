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

    public string[] Options(WorldMapState ctx)
    {
        var names = new string[ctx.ZoneCount];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = ctx.ZoneName(i);
        }
        return names;
    }

    public Color[] OptionColors(WorldMapState ctx) => null;

    public int OptionIndex
    {
        get => ZoneIndex;
        set => ZoneIndex = Mathf.Max(0, value);
    }

    public string HintText(WorldMapState ctx) => "";

    public Color CursorColor(WorldMapState ctx) => Colors.White;

    public string StatusText(WorldMapState ctx) => $"Zone: {ctx.ZoneName(ZoneIndex)}";
    public string LevelText(WorldMapState ctx) => "";

    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
        // No eyedropper or constraint yet; this tool reads nothing off the map.
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        int max = Mathf.Max(0, ctx.ZoneCount - 1);
        byte value = (byte)(erase ? 0 : Mathf.Clamp(ZoneIndex, 0, max));

        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            Vector2I ct = ctx.Data.ColumnTexelToChunkTexel(px, pz);
            ctx.Zone.SetPixel(ct.X, ct.Y, new Color(value / 255f, 0f, 0f, 1f));
        });
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

// Flat zone colour per chunk — the terrain underneath reads from the relief
// shading and step outlines the painter composites over every view.
public class ZoneView : IWorldMapView
{
    public ESpawnPreview PreviewLayer => ESpawnPreview.None;
    public bool ShowsClimb => false;

    // Colour is a zone index, so every step is worth a line.
    public bool ShowsAllSteps => true;
    public bool DrawsWater => false;

    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        Vector2I ct = ctx.Data.ColumnTexelToChunkTexel(px, pz);
        int idx = Mathf.RoundToInt(ctx.Zone.GetPixel(ct.X, ct.Y).R * 255f);
        return WorldMapState.ZoneColor(idx);
    }
}
