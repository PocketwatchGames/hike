using Godot;

// Carves tunnels at a horizontal cross-section. AdjustLevel moves the active
// cross-section Y; Cycle changes how many voxels tall each stroke carves. The
// view shows the current slice and the two slices above/below for context.
public class TunnelTool : IWorldMapTool
{
    public string Name => "Tunnel";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 6f;

    // Active cross-section (world voxel Y) and corridor height carved per stroke.
    public int CrossSectionY = 4;
    public int CarveHeight = 2;

    public TunnelTool()
    {
        View = new TunnelView(this);
    }

    public string[] Options(WorldMapState ctx) => System.Array.Empty<string>();
    public Color[] OptionColors(WorldMapState ctx) => null;

    public int OptionIndex { get => 0; set { } }

    public string HintText(WorldMapState ctx) => "";

    public Color CursorColor(WorldMapState ctx) => Colors.White;

    public string StatusText(WorldMapState ctx) => $"Carve h={CarveHeight}";
    public string LevelText(WorldMapState ctx) => $"Section Y={CrossSectionY}";

    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
        // No eyedropper or constraint yet; this tool reads nothing off the map.
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            for (int i = 0; i < CarveHeight; i++)
            {
                ctx.SetTunnel(px, pz, CrossSectionY + i, !erase);
            }
        });
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;
    public void Cycle(WorldMapState ctx, int dir)
    {
        CarveHeight = Mathf.Clamp(CarveHeight + dir, 1, 8);
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        CrossSectionY += dir;
    }
}

// Cross-section view: white = land at the current slice, greys = land in the
// two slices below, blues = land in the two slices above (land overhead), red =
// an existing carve at the current slice, near-black = open space.
public class TunnelView : IWorldMapView
{
    public ESpawnPreview PreviewLayer => ESpawnPreview.None;

    // Colour is a cross-section slice, not a height.
    public bool ShowsAllSteps => true;
    public bool DrawsWater => false;

    private readonly TunnelTool _tool;

    public TunnelView(TunnelTool tool)
    {
        _tool = tool;
    }

    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        int y = _tool.CrossSectionY;
        if (ctx.IsTunnel(px, pz, y))
        {
            return new Color(0.85f, 0.2f, 0.2f);
        }
        if (ctx.SolidAt(px, pz, y))
        {
            return Colors.White;
        }
        if (ctx.SolidAt(px, pz, y - 1))
        {
            return new Color(0.62f, 0.62f, 0.62f);
        }
        if (ctx.SolidAt(px, pz, y - 2))
        {
            return new Color(0.38f, 0.38f, 0.38f);
        }
        if (ctx.SolidAt(px, pz, y + 1))
        {
            return new Color(0.35f, 0.5f, 0.85f);
        }
        if (ctx.SolidAt(px, pz, y + 2))
        {
            return new Color(0.2f, 0.3f, 0.6f);
        }
        return new Color(0.06f, 0.06f, 0.08f);
    }
}
