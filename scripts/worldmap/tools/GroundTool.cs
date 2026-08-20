using Godot;

// Paints which GroundSetData a column's voxels are stamped from. Erase clears
// back to 0, which means "inherit this column's zone", so a map can be zoned
// broadly and then have its ground detailed only where it matters.
public class GroundTool : IWorldMapTool
{
    public string Name => "Ground";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 12f;

    public int SetIndex = 0;

    public GroundTool()
    {
        View = new GroundView();
    }

    public string[] Options(WorldMapState ctx)
    {
        GroundSetData[] sets = ctx.GroundSets;
        var names = new string[sets.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = sets[i]?.Label ?? $"Ground {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapState ctx)
    {
        GroundSetData[] sets = ctx.GroundSets;
        var colors = new Color[sets.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = sets[i]?.mapColor ?? Colors.White;
        }
        return colors;
    }

    public int OptionIndex
    {
        get => SetIndex;
        set => SetIndex = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapState ctx)
    {
        GroundSetData[] sets = ctx.GroundSets;
        return SetIndex >= 0 && SetIndex < sets.Length && sets[SetIndex] != null
            ? sets[SetIndex].mapColor
            : Colors.White;
    }

    public string HintText(WorldMapState ctx) => "RMB clears back to the zone's own ground";

    public string StatusText(WorldMapState ctx)
    {
        GroundSetData[] sets = ctx.GroundSets;
        string label = SetIndex >= 0 && SetIndex < sets.Length ? sets[SetIndex]?.Label : null;
        return string.IsNullOrEmpty(label) ? "No ground sets authored" : label;
    }

    public string LevelText(WorldMapState ctx) => "";

    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Hard-edged like the index layers it resembles: a half-painted ground
        // index is not a blend, it is a different material.
        float value = erase ? 0f : Mathf.Clamp(SetIndex + 1, 1, 255) / 255f;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            ctx.Ground.SetPixel(px, pz, new Color(value, 0f, 0f, 1f));
        });
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;
    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = Mathf.Max(1, ctx.GroundSets.Length);
        SetIndex = ((SetIndex + dir) % n + n) % n;
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
    }
}

// Ground type and nothing else. Height comes entirely from the outlines, which
// is why this view asks for every step including 1m ones.
public class GroundView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.Props;

    public Color ColorAt(WorldMapState ctx, int px, int pz) => ctx.GroundColorAt(px, pz);
}
