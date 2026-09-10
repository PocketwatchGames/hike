using Godot;

// Paints props DIRECTLY: every column a stroke covers is furnished from the
// chosen list. No noise, no spacing, no per-column chance — the point of
// painting props is to say where the player cannot walk, and anything short of
// one per column leaves lanes through the barrier.
//
// ONE layer, and one tool. There used to be a second ("Breakable") over the same
// palette, and the pair could not state a difference: both filled a region from
// a PropListData, and whether the region can be cleared is decided by what the
// scenes in that list are built out of, not by which raster the author painted
// them on. A list of breakable rocks is a barrier until it is broken and belongs
// here like any other.
//
// So a painted region is always a BARRIER, and always a no-spawn region — see
// WorldMapState.InBlockingRegion. Passable ground cover is scenery rather than a
// region, and it is what a list's UNDERSTORY tier is for: put the bushes in the
// list beside the trees and the fill stands them between the trunks.
public class PropPaintTool : IWorldMapTool
{
    public string Name => "Blocking";

    public IWorldMapView View { get; } = new PropView();
    public float Radius { get; set; } = 8f;

    public int ListIndex = 0;

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

    // The list's own swatch — the same colour its regions are drawn in on the
    // map, so the ring says what the region you are about to paint will do to
    // movement.
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
        int idx = ctx.PaintedPropIndexAt(ctx.ClampX(texel.X), ctx.ClampZ(texel.Y));
        if (idx >= 0)
        {
            ListIndex = idx;
        }
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Hard-edged, like every other index layer: a half-painted list index is
        // not a thinner wood, it is a different list.
        float value = erase ? 0f : Mathf.Clamp(ListIndex + 1, 1, 255) / 255f;
        Image layer = ctx.BlockingProps;
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
