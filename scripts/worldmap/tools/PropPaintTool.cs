using Godot;

// Paints props DIRECTLY: every column a stroke covers is furnished from the
// chosen list. No noise, no spacing, no per-column chance — the point of
// painting props is to say where the player cannot walk, and anything short of
// one per column leaves lanes through the barrier.
//
// Two tools rather than one with a mode, because the two layers answer two
// different questions about a region and an author picks between them the way
// they pick between ground and water:
//
//   COLLIDABLE   — a wall of the world. Trees, boulders. Nothing gets through.
//   DESTRUCTIBLE — a wall until it is cleared. Thickets, brambles, crates.
//
// Both draw from the SAME palette (WorldMapPaletteSource.PropLists): what a
// list is for is which layer it was painted on, not a property of the list, so
// a boulder field can be either. Where both layers cover one column the
// collidable one takes it — see WorldMapState.PreviewDestructibleAt.
public abstract class PropPaintTool : IWorldMapTool
{
    public IWorldMapView View { get; } = new PropView();
    public float Radius { get; set; } = 8f;

    public int ListIndex = 0;

    public abstract string Name { get; }

    // The layer this tool writes, and the salt its lattice is picked with.
    protected abstract Image Layer(WorldMapState ctx);

    public string[] Options(WorldMapState ctx)
    {
        PropListData[] lists = ctx.PropLists;
        var names = new string[lists.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = lists[i]?.Label ?? $"List {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapInk ink)
    {
        PropListData[] lists = ink.Map.PropLists;
        var colors = new Color[lists.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = lists[i]?.mapColor ?? Colors.White;
        }
        return colors;
    }

    // No 1-9: the palette is a directory, so the first nine rows are an
    // arbitrary prefix that moves whenever a list is added.
    public bool NumberKeys => false;

    public int OptionIndex
    {
        get => ListIndex;
        set => ListIndex = Mathf.Max(0, value);
    }

    // The list's own swatch, so the ring says which list is about to go down.
    // The MAP's dots are inked per layer instead (black for collidable, white
    // for destructible): which list furnished a region is the palette's answer,
    // while whether the region stops you is the map's.
    public Color CursorColor(WorldMapInk ink)
    {
        PropListData[] lists = ink.Map.PropLists;
        return ListIndex >= 0 && ListIndex < lists.Length && lists[ListIndex] != null
            ? lists[ListIndex].mapColor
            : Colors.White;
    }

    public string HintText(WorldMapState ctx) => "RMB clears; alt+click samples the list under it";

    public string StatusText(WorldMapState ctx, WorldMapView view)
    {
        PropListData list = Active(ctx);
        return list == null ? "No prop lists authored" : list.Label;
    }

    public string LevelText(WorldMapState ctx, WorldMapView view)
    {
        PropListData list = Active(ctx);
        return list == null ? "" : $"one per column  ({list.scenes.Length} scenes)";
    }

    // Alt+click adopts whatever list is under it, which is how a region gets
    // extended without hunting its entry in a palette that is a directory long.
    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
        if (!mods.HasFlag(EStrokeMods.Pick))
        {
            return;
        }
        int idx = Mathf.RoundToInt(Layer(ctx).GetPixel(ctx.ClampX(texel.X), ctx.ClampZ(texel.Y)).R * 255f) - 1;
        if (idx >= 0 && idx < ctx.PropLists.Length)
        {
            ListIndex = idx;
        }
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Hard-edged, like every other index layer: a half-painted list index is
        // not a thinner wood, it is a different list.
        float value = erase ? 0f : Mathf.Clamp(ListIndex + 1, 1, 255) / 255f;
        Image layer = Layer(ctx);
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            layer.SetPixel(px, pz, new Color(value, 0f, 0f, 1f));
        });
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;

    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = Mathf.Max(1, ctx.PropLists.Length);
        ListIndex = ((ListIndex + dir) % n + n) % n;
    }

    // Nothing to step: a painted column is furnished or it is not, and the only
    // thing a brush decides is which columns those are.
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
    }

    private PropListData Active(WorldMapState ctx)
    {
        PropListData[] lists = ctx.PropLists;
        return ListIndex >= 0 && ListIndex < lists.Length ? lists[ListIndex] : null;
    }
}

public class CollidablePropTool : PropPaintTool
{
    public override string Name => "Blocking";

    protected override Image Layer(WorldMapState ctx) => ctx.CollidableProps;
}

public class DestructiblePropTool : PropPaintTool
{
    public override string Name => "Breakable";

    protected override Image Layer(WorldMapState ctx) => ctx.DestructibleProps;
}

// Ground type as the base, exactly as the ground view draws it — what a prop
// stands on is the context you judge it against. The props themselves appear
// only as dots, so they read as objects on the ground rather than as a second
// wash competing with it.
public class PropView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.Props;

    public Color ColorAt(WorldMapInk ink, int px, int pz) => ink.GroundColorAt(px, pz);
}
