using Godot;

// Paints which SpawnSetData supplies a column's wildlife, and how much of its
// authored rate applies there.
//
// The same resource type and the same raster shape as the prop brush, on its own
// layer. Mobs and trees vary independently in a real world — the same pine stand
// runs from a safe valley into wolf country, and the wolves carry on out onto
// the bare ridge above the treeline — and one set per column means sharing a
// layer would make painting one erase the other.
//
// Difficulty is deliberately NOT here: it is its own scalar layer, so "which
// creatures" and "how dangerous" can be painted apart. Putting a level band on
// the set would need "wolves-easy" and "wolves-hard" as separate assets.
public class MobTool : IWorldMapTool
{
    public string Name => "Mobs";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 16f;

    public int SetIndex = 0;
    public float Density = 1f;

    public MobTool()
    {
        View = new MobView();
    }

    public string[] Options(WorldMapState ctx)
    {
        SpawnSetData[] sets = ctx.MobSets;
        var names = new string[sets.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = sets[i]?.Label ?? $"Set {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapState ctx)
    {
        SpawnSetData[] sets = ctx.MobSets;
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
        SpawnSetData[] sets = ctx.MobSets;
        return SetIndex >= 0 && SetIndex < sets.Length && sets[SetIndex] != null
            ? sets[SetIndex].mapColor
            : Colors.White;
    }

    public string HintText(WorldMapState ctx) => "";

    public string StatusText(WorldMapState ctx)
    {
        SpawnSetData[] sets = ctx.MobSets;
        string label = SetIndex >= 0 && SetIndex < sets.Length ? sets[SetIndex]?.Label : null;
        return string.IsNullOrEmpty(label) ? "No mob sets authored" : label;
    }

    public string LevelText(WorldMapState ctx) => $"Density {Mathf.RoundToInt(Density * 100f)}%";

    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        byte id = (byte)Mathf.Clamp(SetIndex + 1, 1, 255);
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            if (erase)
            {
                ctx.Mobs.SetPixel(px, pz, new Color(0f, 0f, 0f, 1f));
                return;
            }
            // Max, as the prop brush does: a stroke may only ever raise density,
            // or dots blink out at the rim as the brush moves on.
            float d = Mathf.Max(ctx.Mobs.GetPixel(px, pz).G, Density * weight);
            ctx.Mobs.SetPixel(px, pz, new Color(id / 255f, d, 0f, 1f));
        });
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;
    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = Mathf.Max(1, ctx.MobSets.Length);
        SetIndex = ((SetIndex + dir) % n + n) % n;
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        Density = Mathf.Clamp(Density + dir * 0.1f, 0f, 1f);
    }
}

// Ground underneath, mob colour in the dots — same reading as the prop view, so
// wildlife is judged against the terrain it lives on.
public class MobView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.Props | ESpawnPreview.Mobs;

    public Color ColorAt(WorldMapState ctx, int px, int pz) => ctx.GroundColorAt(px, pz);
}
