using Godot;

// Marks the walls that carry a climbing route.
//
// The map looks exactly like elevation painting, because that is the view the
// decision is made against: you are reading the terrain's steps and picking one
// to make climbable. Paint a wall the map draws in its tall-step ink and it turns
// magenta — the SAME line, recoloured, so a route is visibly a property of an
// edge that was already there rather than a mark floating over the terrain.
//
// A flag per column, not a coverage. ZoneGenData.climbCoverage asks "how much of
// this zone's rock is climbable" and worldgen answers it with cellular patches;
// this asks "where is the way up", and the two are different enough that sharing
// a value would make neither work. The bake dresses a marked column's whole
// exposed face — for now a plain vertical column of climbable surface.
public class ClimbTool : IWorldMapTool
{
    public string Name => "Climb";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 6f;

    public ClimbTool()
    {
        View = new ClimbView();
    }

    // No option row: a route is on or off, and the brush is the whole interface.
    public string[] Options(WorldMapState ctx) => System.Array.Empty<string>();

    public Color[] OptionColors(WorldMapState ctx) => null;

    public int OptionIndex { get => 0; set { } }

    public Color CursorColor(WorldMapState ctx) => ctx.Data.climbInk;

    public string HintText(WorldMapState ctx)
        => "Paint the tall edges to make them climbable; RMB clears";

    public string StatusText(WorldMapState ctx)
        => $"Routes on walls {ctx.Data.climbRouteMinWallVoxels}m and taller";

    public string LevelText(WorldMapState ctx) => "";

    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Binary, and NOT eased by the falloff: a route is a thing or it is not,
        // and half a route dressed is just a wall with holes in it. The brush
        // still decides WHICH columns — its radius is how wide a route is.
        //
        // Only columns that own a tall enough wall take the mark. That is the
        // whole point of painting against the elevation view: the marks land on
        // the edges you can see, and dragging across the flat ground between two
        // cliffs marks neither. Erase ignores the test, or a route would be
        // unclearable the moment the terrain under it changed.
        int minWall = ctx.Data.climbRouteMinWallVoxels;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            if (erase)
            {
                ctx.SetClimbRouteAt(px, pz, false);
                return;
            }
            if (ctx.WallDropAt(px, pz) >= minWall)
            {
                ctx.SetClimbRouteAt(px, pz, true);
            }
        });
    }

    public void Cycle(WorldMapState ctx, int dir)
    {
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
    }
}

// The elevation map, unchanged — with routed edges inked in climbInk instead of
// their height ink (ShowsClimb). Nothing else is recoloured: a route is judged
// against the terrain's shape, so the shape has to keep reading normally.
public class ClimbView : IWorldMapView
{
    public bool ShowsAllSteps => false;
    public bool DrawsWater => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.None;
    public bool ShowsClimb => true;

    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        return ctx.WithWater(ctx.ElevationColor(px, pz), px, pz);
    }
}
