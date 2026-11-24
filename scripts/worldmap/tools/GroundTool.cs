using Godot;

// Paints which TerrainKitData ground is stamped from, on the first floor under
// the cut. Where that floor is a column's SURFACE it writes the column layer, so
// the kit follows the ground as it is reshaped; where something is over it (a
// passage, under an arch) it writes that voxel, and the bake carries the kit down
// through the solid run beneath. Erase clears back to the document's default.
public class GroundTool : IWorldMapTool
{
    public string Name => "Terrain override";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 12f;

    public int SetIndex = 0;

    public GroundTool()
    {
        View = new GroundView();
    }

    public string[] Options(WorldMapState ctx)
    {
        TerrainKitData[] sets = ctx.Terrains;
        var names = new string[sets.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = sets[i]?.Label ?? $"Ground {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapInk ink)
    {
        TerrainKitData[] sets = ink.Map.Terrains;
        var colors = new Color[sets.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = sets[i]?.mapColor ?? Colors.White;
        }
        return colors;
    }

    // No 1-9: ground sets are a directory, so the first nine rows are an arbitrary prefix that
    // moves whenever one is added.
    public bool NumberKeys => false;

    public int OptionIndex
    {
        get => SetIndex;
        set => SetIndex = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapInk ink)
    {
        TerrainKitData[] sets = ink.Map.Terrains;
        return SetIndex >= 0 && SetIndex < sets.Length && sets[SetIndex] != null
            ? sets[SetIndex].mapColor
            : Colors.White;
    }

    public string HintText(WorldMapState ctx)
        => "Paints the first floor under the cutaway  |  RMB clears back to the default ground  |  Q/E cutaway";

    public string StatusText(WorldMapState ctx, WorldMapView view)
    {
        TerrainKitData[] sets = ctx.Terrains;
        string label = SetIndex >= 0 && SetIndex < sets.Length ? sets[SetIndex]?.Label : null;
        return string.IsNullOrEmpty(label) ? "No ground sets authored" : label;
    }

    public string LevelText(WorldMapState ctx, WorldMapView view) => "";

    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Hard-edged like the index layers it resembles: a half-painted ground
        // index is not a blend, it is a different material.
        int kit = erase ? -1 : SetIndex;
        int clip = view.ClipFor(true);
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            if (ctx.TryPaintFloor(px, pz, clip, out int floor, out bool onSurface))
            {
                ctx.SetFloorKit(px, pz, floor, onSurface, kit);
            }
        });
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
    }
}

// Ground type and nothing else. Height comes entirely from the outlines, which
// is why this view asks for every step including 1m ones. Cuts away, showing
// the kit of the first floor under the plane.
public class GroundView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public bool CutsAway => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.Props;

    public Color ColorAt(WorldMapInk ink, int px, int pz)
        => ink.View.IsCutAway
            ? ink.CutawayGroundColorAt(px, pz, ink.View.CutawayY, out _)
            : ink.GroundColorAt(px, pz);
}
