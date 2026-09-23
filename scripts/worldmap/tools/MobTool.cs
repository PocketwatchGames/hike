using Godot;

// Paints which SpawnScatterData supplies a column's wildlife, and how much of
// its authored rate applies there.
//
// The same raster shape as the prop brush, on its own layer. Mobs and trees vary
// independently in a real world — the same pine stand
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
        SpawnScatterData[] sets = ctx.ScatterSets;
        var names = new string[sets.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = sets[i]?.Label ?? $"Set {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapInk ink)
    {
        SpawnScatterData[] sets = ink.Map.ScatterSets;
        var colors = new Color[sets.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = sets[i]?.mapColor ?? Colors.White;
        }
        return colors;
    }

    // No 1-9: scatter sets are a directory, so the first nine rows are an
    // arbitrary prefix that moves whenever one is added.
    public bool NumberKeys => false;

    public int OptionIndex
    {
        get => SetIndex;
        set => SetIndex = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapInk ink)
    {
        SpawnScatterData[] sets = ink.Map.ScatterSets;
        return SetIndex >= 0 && SetIndex < sets.Length && sets[SetIndex] != null
            ? sets[SetIndex].mapColor
            : Colors.White;
    }

    public SpawnScatterData SelectedScatter(WorldMapState ctx)
    {
        SpawnScatterData[] sets = ctx.ScatterSets;
        return SetIndex >= 0 && SetIndex < sets.Length ? sets[SetIndex] : null;
    }

    public string HintText(WorldMapState ctx)
        => "T/G lower the cutaway to scatter into the PASSAGES under it (RMB clears)";

    public string StatusText(WorldMapState ctx, WorldMapView view)
    {
        SpawnScatterData[] sets = ctx.ScatterSets;
        string label = SetIndex >= 0 && SetIndex < sets.Length ? sets[SetIndex]?.Label : null;
        if (string.IsNullOrEmpty(label))
        {
            return "No scatter sets authored";
        }
        return view.IsCutAway ? $"{label} (passages under the cut)" : label;
    }

    public string LevelText(WorldMapState ctx, WorldMapView view) => $"Density {Mathf.RoundToInt(Density * 100f)}%";

    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        byte id = (byte)Mathf.Clamp(SetIndex + 1, 1, 255);
        bool cut = view.IsCutAway;
        int clip = view.CutawayY;
        if (cut && !erase && SetIndex >= WorldMapState.MaxPassageSets)
        {
            GD.PrintErr($"Mobs: a passage can hold scatter slots 0..{WorldMapState.MaxPassageSets - 1}; "
                + $"slot {SetIndex} does not fit the tunnel mask");
            return;
        }
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            // Under a lowered cutaway, a column whose exposed floor has a passage
            // over it paints THAT passage's scatter, the same way the danger
            // tool paints its level; anywhere else, the surface layer.
            if (cut && ctx.CutawayPassage(px, pz, clip, out int floor, out int top))
            {
                // Same Max rule as the surface, within one set: a stroke only
                // raises density. Density is 15 steps in the carve, so a
                // feathered rim rounds to them.
                float pd = erase ? 0f : Density * weight;
                if (!erase && ctx.PassageScatterSlotAt(px, pz, floor + 1) == SetIndex)
                {
                    ctx.PassageScatterAt(px, pz, floor + 1, out float had);
                    pd = Mathf.Max(pd, had);
                }
                for (int wy = floor + 1; wy <= top; wy++)
                {
                    ctx.SetPassageScatter(px, pz, wy, erase ? -1 : SetIndex, pd);
                }
                return;
            }
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
        int n = Mathf.Max(1, ctx.ScatterSets.Length);
        SetIndex = ((SetIndex + dir) % n + n) % n;
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        Density = Mathf.Clamp(Density + dir * 0.1f, 0f, 1f);
    }
}

// Ground underneath, mob colour in the dots — same reading as the prop view, so
// wildlife is judged against the terrain it lives on.
//
// Cuts away like CutawayGroundView: with the plane lowered it draws the floors
// the cut exposes, and the painter dots the scatter of whatever it exposed —
// a passage's own, or the surface's where the floor is the surface
// (WorldMapState.PreviewMobUnderCut).
public class MobView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public bool CutsAway => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.Props | ESpawnPreview.Mobs;

    public Color ColorAt(WorldMapInk ink, int px, int pz)
    {
        return ink.View.IsCutAway
            ? ink.CutawayColorAt(px, pz, ink.View.CutawayY, out _)
            : ink.GroundColorAt(px, pz);
    }
}
