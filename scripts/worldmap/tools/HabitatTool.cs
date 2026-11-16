using Godot;

// Paints the per-chunk habitat — what lives in a place (fairies, the loot
// breakables and creatures give up), independent of the zone it looks like.
// Erase clears to none. The raster stores slot + 1.
public class HabitatTool : IWorldMapTool
{
    public string Name => "Habitat";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 24f;

    public int HabitatIndex = 0;

    public HabitatTool()
    {
        View = new HabitatView();
    }

    public string[] Options(WorldMapState ctx)
    {
        var names = new string[ctx.HabitatCount];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = ctx.HabitatName(i);
        }
        return names;
    }

    public Color[] OptionColors(WorldMapInk ink)
    {
        var colors = new Color[ink.Map.HabitatCount];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = WorldMapInk.HabitatColor(i);
        }
        return colors;
    }

    // A directory palette: the first nine rows are an arbitrary prefix.
    public bool NumberKeys => false;

    public int OptionIndex
    {
        get => HabitatIndex;
        set => HabitatIndex = Mathf.Max(0, value);
    }

    public string HintText(WorldMapState ctx) => "";

    public Color CursorColor(WorldMapInk ink) => WorldMapInk.HabitatColor(HabitatIndex);

    public string StatusText(WorldMapState ctx, WorldMapView view) => $"Habitat: {ctx.HabitatName(HabitatIndex)}";
    public string LevelText(WorldMapState ctx, WorldMapView view) => "";

    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        if (!erase && ctx.HabitatCount == 0)
        {
            return;
        }
        int slot = Mathf.Clamp(HabitatIndex, 0, ctx.HabitatCount - 1);
        byte value = (byte)(erase ? 0 : slot + 1);

        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            Vector2I ct = ctx.Data.ColumnTexelToChunkTexel(px, pz);
            ctx.Habitat.SetPixel(ct.X, ct.Y, new Color(value / 255f, 0f, 0f, 1f));
        });
    }

    // Floods whole chunks of the habitat under the press; RMB fills with none.
    public WorldMapFill Fill(WorldMapState ctx, WorldMapView view, bool erase)
    {
        if (!erase && ctx.HabitatCount == 0)
        {
            return null;
        }
        int slot = Mathf.Clamp(HabitatIndex, 0, Mathf.Max(0, ctx.HabitatCount - 1));
        var value = new Color((erase ? 0 : slot + 1) / 255f, 0f, 0f, 1f);
        return new WorldMapFill
        {
            CellSize = ChunkState.SIZE,
            KeyAt = (px, pz) => ctx.HabitatIndexAt(ctx.Data.ColumnTexelToChunkTexel(px, pz)),
            Write = (px, pz) =>
            {
                Vector2I ct = ctx.Data.ColumnTexelToChunkTexel(px, pz);
                ctx.Habitat.SetPixel(ct.X, ct.Y, value);
            },
        };
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;

    public void Cycle(WorldMapState ctx, int dir)
    {
        if (ctx.HabitatCount > 0)
        {
            HabitatIndex = (HabitatIndex + dir + ctx.HabitatCount) % ctx.HabitatCount;
        }
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
    }
}

// Habitat colour per chunk, unpainted chunks grey; 50% darker over open water
// so coastlines stay readable under the wash.
public class HabitatView : IWorldMapView
{
    public ESpawnPreview PreviewLayer => ESpawnPreview.None;
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;

    public Color ColorAt(WorldMapInk ink, int px, int pz)
    {
        int idx = ink.Map.HabitatIndexAt(ink.Map.Data.ColumnTexelToChunkTexel(px, pz));
        Color c = idx < 0 ? WorldMapInk.UnpaintedChunkColor : WorldMapInk.HabitatColor(idx);
        if (ink.Map.Underwater(px, pz))
        {
            c = new Color(c.R * 0.5f, c.G * 0.5f, c.B * 0.5f);
        }
        return c;
    }
}
