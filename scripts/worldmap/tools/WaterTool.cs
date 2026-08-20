using Godot;

// Paints standing water bodies (lakes, rivers) by writing a per-column water
// surface height, independent of the global ocean elevation. AdjustLevel sets
// the surface Y the brush paints; erase removes painted water (back to ocean).
public class WaterTool : IWorldMapTool
{
    public string Name => "Water";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 10f;

    // Painted water surface, in voxels ABOVE sea level — same frame as
    // ElevationTool.TargetVoxels, so it defaults to 0 = the shore no matter what
    // Y the document puts the waterline at. Never negative: water below sea
    // level is just ocean, which every column already gets for free, so 0 and
    // "unpainted" mean the same thing and this layer only expresses water held
    // ABOVE the sea (a highland lake). Terrain is what you lower to make sea.
    public int SurfaceVoxels = 0;

    public WaterTool()
    {
        View = new WaterView();
    }

    public string[] Options(WorldMapState ctx) => System.Array.Empty<string>();
    public Color[] OptionColors(WorldMapState ctx) => null;

    public int OptionIndex { get => 0; set { } }

    public string HintText(WorldMapState ctx) => "";

    public Color CursorColor(WorldMapState ctx) => Colors.White;

    public string StatusText(WorldMapState ctx) => "Paint Water";
    public string LevelText(WorldMapState ctx)
        => $"Surface {SurfaceVoxels:+#;-#;0}v (Y={ctx.SeaLevel + SurfaceVoxels})";

    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
        // No eyedropper or constraint yet; this tool reads nothing off the map.
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Same encoding as the elevation layer: voxels relative to sea level.
        // 0 erases back to plain ocean (WaterSurface floors at the waterline).
        float voxels = erase
            ? 0f
            : Mathf.Clamp(SurfaceVoxels, 0f, ctx.Data.maxElevationVoxels);

        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            ctx.Water.SetPixel(px, pz, new Color(voxels, 0f, 0f, 1f));
        });
    }

    public void Cycle(WorldMapState ctx, int dir)
    {
        // No secondary parameter — AdjustLevel drives the surface height.
    }

    // Steps the lattice, so a painted surface lands on the same bands the
    // elevation map draws. Floors at the shore for the reason above.
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        SurfaceVoxels = Mathf.Clamp(
            SurfaceVoxels + dir * ctx.StepVoxels,
            0,
            Mathf.RoundToInt(ctx.Data.maxElevationVoxels));
    }
}

// Painted/ocean water shown as blue by depth; dry land as a faint elevation grey.
public class WaterView : IWorldMapView
{
    public ESpawnPreview PreviewLayer => ESpawnPreview.None;
    public bool ShowsClimb => false;

    // Dry land is drawn with the elevation palette, dimmed.
    public bool ShowsAllSteps => false;
    public bool DrawsWater => true;

    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        int th = ctx.TerrainHeight(px, pz);
        int surf = ctx.WaterSurface(px, pz);
        if (surf > th)
        {
            float t = Mathf.Clamp((surf - th) / 24f, 0.15f, 0.9f);
            return new Color(0.25f, 0.5f, 0.85f).Lerp(new Color(0.02f, 0.08f, 0.3f), t);
        }
        Color land = ctx.ElevationColor(px, pz);
        return new Color(land.R * 0.5f, land.G * 0.5f, land.B * 0.5f);
    }
}
