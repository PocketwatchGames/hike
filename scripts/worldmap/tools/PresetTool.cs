using Godot;

// The composite brush: one stroke writing every per-column layer a
// PaintPresetData names (ground and props; zone is painted on its own).
//
// It exists because the layers are independent. Splitting ground, props and
// zone apart is what makes a pine stand reusable across grounds — but it also
// means the ORDINARY stroke ("this is boreal forest") would otherwise be three
// strokes that have to agree with each other across the whole map. The preset
// restores that one stroke; every layer stays independently repaintable after
// it, which is the half a bundled zone could never give.
//
// Null slots on the preset are skipped rather than cleared, so a preset can
// deliberately touch only some layers.
public class PresetTool : IWorldMapTool
{
    public string Name => "Preset";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 16f;

    public int PresetIndex = 0;

    public PresetTool()
    {
        View = new PresetView();
    }

    public string[] Options(WorldMapState ctx)
    {
        PaintPresetData[] sets = ctx.Presets;
        var names = new string[sets.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = sets[i]?.Label ?? $"Preset {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapInk ink)
    {
        PaintPresetData[] sets = ink.Map.Presets;
        var colors = new Color[sets.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = sets[i]?.SwatchColor ?? Colors.White;
        }
        return colors;
    }

    // No 1-9: presets are a directory, so the first nine rows are an arbitrary prefix that
    // moves whenever one is added.
    public bool NumberKeys => false;

    public int OptionIndex
    {
        get => PresetIndex;
        set => PresetIndex = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapInk ink)
    {
        PaintPresetData preset = Active(ink.Map);
        return preset?.SwatchColor ?? Colors.White;
    }

    public string HintText(WorldMapState ctx)
        => "Writes ground and mobs together; repaint either after. Props and zone are their own tools";

    public string StatusText(WorldMapState ctx, WorldMapView view)
    {
        PaintPresetData preset = Active(ctx);
        if (preset == null)
        {
            return "No presets authored";
        }
        string layers = (preset.ground != null ? "ground " : "")
            + (preset.mobs != null ? "mobs" : "");
        return $"{preset.Label}  [{layers.Trim()}]";
    }

    public string LevelText(WorldMapState ctx, WorldMapView view) => "";

    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        PaintPresetData preset = Active(ctx);
        if (preset == null)
        {
            return;
        }

        int groundValue = IndexOf(ctx.Terrains, preset.ground);
        int mobValue = IndexOf(ctx.ScatterSets, preset.mobs);

        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            if (preset.ground != null || erase)
            {
                float v = erase ? 0f : Mathf.Clamp(groundValue + 1, 1, 255) / 255f;
                ctx.Ground.SetPixel(px, pz, new Color(v, 0f, 0f, 1f));
            }
            if (preset.mobs != null || erase)
            {
                // At the set's OWN authored rate. A per-preset multiplier existed
                // and no preset ever set it: the mob brush already carries a
                // density on R/F, which is where scaling one down belongs — you
                // decide it while looking at the dots, not once per biome.
                WriteSpawn(ctx.Mobs, mobValue, 1f, weight, px, pz, erase);
            }
        });
    }

    // The ground layer is a plain index layer and hard-edged: a half-painted set
    // index is not a thinner ground, it is a different set.
    private static void WriteIndex(Image layer, int index, int px, int pz, bool erase)
    {
        float value = erase ? 0f : Mathf.Clamp(index + 1, 1, 255) / 255f;
        layer.SetPixel(px, pz, new Color(value, 0f, 0f, 1f));
    }

    // Density is taken as the MAX against what is already there, never the raw
    // falloff weight: writing the weight outright means a column under the brush
    // centre one moment and at its rim the next has its density DROP as the
    // stroke moves on, so its dots blink out and return as you drag. Carried
    // across a set change too, because density is a MULTIPLIER on the set's own
    // rate rather than an absolute count — resetting it would let the rim thin a
    // dense region it overpaints.
    private static void WriteSpawn(Image layer, int index, float density, float weight, int px, int pz, bool erase)
    {
        if (erase)
        {
            layer.SetPixel(px, pz, new Color(0f, 0f, 0f, 1f));
            return;
        }
        int id = Mathf.Clamp(index + 1, 1, 255);
        float d = Mathf.Max(layer.GetPixel(px, pz).G, density * weight);
        layer.SetPixel(px, pz, new Color(id / 255f, d, 0f, 1f));
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;
    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = Mathf.Max(1, ctx.Presets.Length);
        PresetIndex = ((PresetIndex + dir) % n + n) % n;
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
    }

    private PaintPresetData Active(WorldMapState ctx)
    {
        PaintPresetData[] sets = ctx.Presets;
        return PresetIndex >= 0 && PresetIndex < sets.Length ? sets[PresetIndex] : null;
    }

    private static int IndexOf<T>(T[] array, T value) where T : class
    {
        if (value == null || array == null)
        {
            return -1;
        }
        for (int i = 0; i < array.Length; i++)
        {
            if (ReferenceEquals(array[i], value))
            {
                return i;
            }
        }
        return -1;
    }
}

// Shows the ground layer, since that is the layer a preset always paints and
// the one whose regions read as the map's shape.
public class PresetView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.Props | ESpawnPreview.Mobs;

    public Color ColorAt(WorldMapInk ink, int px, int pz) => ink.GroundColorAt(px, pz);
}
