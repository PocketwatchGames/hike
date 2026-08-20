using Godot;

// Paints which SpawnSetData covers a column, and how much of its authored rate
// applies there. The raster is (set index + 1, density); index 0 means nothing
// painted, which is why the palette is stored one-based.
//
// Density is a MULTIPLIER on each entry's authored rate, not a per-column
// chance. That distinction is the whole fix for the forest nothing could walk
// through: a chance tops out at one spawn per square metre, while a rate says
// "one tree per 40 m" and means it.
public class ScatterTool : IWorldMapTool
{
    public string Name => "Props";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 16f;

    public int SetIndex = 0;
    public float Density = 1f;

    public ScatterTool()
    {
        View = new ScatterView();
    }

    public string[] Options(WorldMapState ctx)
    {
        SpawnSetData[] sets = ctx.PropSets;
        var names = new string[sets.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = sets[i]?.Label ?? $"Set {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapState ctx)
    {
        SpawnSetData[] sets = ctx.PropSets;
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

    // The palette entry's own colour, so the toolbar button doubles as the map
    // legend rather than something to memorise.
    public Color CursorColor(WorldMapState ctx)
    {
        SpawnSetData[] sets = ctx.PropSets;
        return SetIndex >= 0 && SetIndex < sets.Length && sets[SetIndex] != null
            ? sets[SetIndex].mapColor
            : Colors.White;
    }

    public string HintText(WorldMapState ctx) => "";

    public string StatusText(WorldMapState ctx)
    {
        SpawnSetData[] sets = ctx.PropSets;
        string label = SetIndex >= 0 && SetIndex < sets.Length ? sets[SetIndex]?.Label : null;
        return string.IsNullOrEmpty(label) ? "No prop sets authored" : label;
    }

    public string LevelText(WorldMapState ctx)
    {
        SpawnSetData[] sets = ctx.PropSets;
        SpawnSetData set = SetIndex >= 0 && SetIndex < sets.Length ? sets[SetIndex] : null;
        if (set == null)
        {
            return "";
        }
        string trees = set.treeScenes.Length > 0
            ? $"forest {set.forestDensity:0.##}@{set.forestThreshold:0.##} +{set.treesPerChunkMin}-{set.treesPerChunkMax}/chunk "
            : "";
        string grass = set.foliageScenes.Length > 0 ? $"grass @{set.grassThreshold:0.##}" : "";
        return $"Density {Mathf.RoundToInt(Density * 100f)}%  ({trees}{grass})".TrimEnd();
    }

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
                ctx.Scatter.SetPixel(px, pz, new Color(0f, 0f, 0f, 1f));
                return;
            }
            // Max regardless of which set was here: density is a multiplier on
            // the set's own rate, so it carries over harmlessly, and a stroke
            // that can only raise it never makes dots blink out mid-drag.
            float d = Mathf.Max(ctx.Scatter.GetPixel(px, pz).G, Density * weight);
            ctx.Scatter.SetPixel(px, pz, new Color(id / 255f, d, 0f, 1f));
        });
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;
    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = Mathf.Max(1, ctx.PropSets.Length);
        SetIndex = ((SetIndex + dir) % n + n) % n;
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        Density = Mathf.Clamp(Density + dir * 0.1f, 0f, 1f);
    }
}

// Ground type as the base, exactly as the ground view draws it — what a prop
// stands on is the context you judge it against. The prop set's colour appears
// ONLY in the spawn dots, so the dots read as objects on the ground rather than
// as a second wash competing with it.
public class ScatterView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.Props;

    public Color ColorAt(WorldMapState ctx, int px, int pz) => ctx.GroundColorAt(px, pz);
}
