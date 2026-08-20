using Godot;

// Paints how DANGEROUS ground is, independently of what lives on it.
//
// Separate from the mob layer on purpose: the same wolves belong in a quiet
// valley and in the deep woods, and a level band carried on the mob set would
// need "wolves-easy" and "wolves-hard" as separate assets. Painting the two
// apart means one set of creatures spans a difficulty gradient, which is what
// worldgen's per-zone bands do with a noise field.
//
// Deliberately NOT part of the preset brush. Difficulty does not follow biome —
// a hard region and a safe one can share ground, props and wildlife — so folding
// it into the composite stroke would tie together the one pair of layers that
// most wants to vary independently.
public class MobLevelTool : IWorldMapTool
{
    public string Name => "Danger";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 24f;

    public int Level;

    public MobLevelTool()
    {
        View = new MobLevelView();
    }

    public string[] Options(WorldMapState ctx)
    {
        var names = new string[ctx.MobLevelCount];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = $"Level {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapState ctx)
    {
        Color[] colors = ctx.Data.mobLevelColors;
        var swatches = new Color[ctx.MobLevelCount];
        for (int i = 0; i < swatches.Length; i++)
        {
            swatches[i] = colors != null && i < colors.Length ? colors[i] : Colors.White;
        }
        return swatches;
    }

    public int OptionIndex
    {
        get => Level;
        set => Level = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapState ctx) => Shade(ctx, Level);

    public string HintText(WorldMapState ctx)
        => "Eases toward the level you pick; RMB eases back to 0";

    public string StatusText(WorldMapState ctx) => $"Danger level {Level}";

    // The ramp sampled at a continuous level — stops lerped linearly, so the
    // brush's soft edge reads as a fade between bands instead of a hard ring.
    public static Color Shade(WorldMapState ctx, float level)
    {
        Color[] colors = ctx.Data.mobLevelColors;
        if (colors == null || colors.Length == 0)
        {
            return Colors.Gray;
        }
        float t = Mathf.Clamp(level, 0f, colors.Length - 1);
        int lo = Mathf.FloorToInt(t);
        int hi = Mathf.Min(lo + 1, colors.Length - 1);
        return colors[lo].Lerp(colors[hi], t - lo);
    }

    public string LevelText(WorldMapState ctx) => "";

    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // SOFT, unlike the index layers: this field is continuous, so the brush
        // eases toward its target and the falloff becomes the gradient. Painting
        // is where the smoothing happens — nothing re-smooths it at bake, so the
        // shades on the map are exactly the levels the mobs get.
        float target = erase ? 0f : Level;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            float k = brush.flow * weight;
            ctx.SetMobLevelAt(px, pz, Mathf.Lerp(ctx.MobLevelAt(px, pz), target, k));
        });
    }

    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = ctx.MobLevelCount;
        Level = ((Level + dir) % n + n) % n;
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        Level = Mathf.Clamp(Level + dir, 0, ctx.MobLevelCount - 1);
    }
}

// The terrain recoloured entirely by difficulty — one shade per level, so a
// glance answers "how dangerous is it here" and nothing else competes for the
// colour. Water still reads as water, for orientation.
public class MobLevelView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.None;
    public bool ShowsClimb => false;

    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        return ctx.WithWater(MobLevelTool.Shade(ctx, ctx.MobLevelAt(px, pz)), px, pz);
    }
}
