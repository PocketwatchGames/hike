using Godot;

// Paints water: every column the brush touches is filled from its ground up to
// the level you have selected.
//
// A BRUSH, not a fill. The fill that came before answered a question the author
// had already answered by clicking, and it could not tell a lake from a river
// without being told which it was looking at — a seed high in the mountains has
// no way to know whether it should pond to its own height or run downhill. A
// brush has nothing to decide: water goes where you put it, at the level you
// chose, and a lake and a river are the same act with a different shape of
// stroke.
//
// What the fill was really providing was FEEDBACK, and the map provides it
// directly instead: water is shaded by depth, so a shoreline reads pale and a
// bed dark, and any edge where water pours over a bare drop is inked as a
// waterfall. Between them you can see what you painted without having to run it.
public class WaterTool : IWorldMapTool
{
    public string Name => "Water";

    public IWorldMapView View { get; }

    public float Radius { get; set; } = 6f;

    // Surface the brush fills to, in voxels relative to sea level — the same
    // encoding the elevation layer uses, so the number in the HUD means the same
    // thing on both tools. Signed, like the elevation layer: the world is
    // prefilled with water at 0, and painting below that is how a basin holds
    // water lower than the sea around it.
    public int SurfaceVoxels = 1;

    public WaterTool()
    {
        View = new WaterView();
    }

    public string[] Options(WorldMapState ctx) => System.Array.Empty<string>();

    public Color[] OptionColors(WorldMapState ctx) => null;

    public int OptionIndex { get => 0; set { } }

    public Color CursorColor(WorldMapState ctx) => ctx.Data.shallowWaterColor;

    public string HintText(WorldMapState ctx)
        => "R/F set the surface  |  T/G cutaway (paint water inside a tunnel)  |  "
        + "alt+LMB samples a height, alt+RMB aims the cutaway  |  "
        + "RMB removes the water entirely";

    public string StatusText(WorldMapState ctx) => "Fills each column to the surface";

    public string LevelText(WorldMapState ctx)
        => $"Surface {SurfaceVoxels:+#;-#;0}v (Y={ctx.SeaLevel + SurfaceVoxels})"
        + $"  |  Cutaway Y={ctx.CutawayY}";

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;

    // alt+click aims the brush at a height already on the map — the same
    // eyedropper the elevation tool has, and for the same reason: picking the
    // terrace you want the water to meet beats stepping R/F forty times.
    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
        if ((mods & EStrokeMods.Pick) != 0)
        {
            // The water under the cursor, or the ground where it has none —
            // pointing at the terrace you want the surface to meet is the fast
            // way to aim, the same as the elevation tool's pick. Read UNDER the
            // cutaway, so pointing into a tunnel picks that tunnel's floor
            // rather than the hilltop over it.
            int clip = ctx.CutawayY;
            int floor = ctx.CutawayFloor(texel.X, texel.Y, clip, out _);
            if (floor < ctx.Data.WorldMinY)
            {
                return;   // solid rock at the cut: nothing to sample
            }
            // The column's water counts only where it is VISIBLE under the cut.
            // Its surface is otherwise a pool standing on top of the rock you are
            // looking through, and aiming at a tunnel would hand back the lake
            // above it — or, clamped to the plane, the plane itself.
            int ws = ctx.WaterSurface(texel.X, texel.Y);
            int picked = ws > floor && ws <= clip ? ws : floor;
            SurfaceVoxels = ClampSurface(ctx, picked - ctx.SeaLevel);
        }
    }

    // LMB sets the column's water surface, RMB removes its water outright.
    //
    // A stroke writes every column it covers, including ones whose ground stands
    // above the surface — that water is LATENT, and it is the point: carve the
    // land away later and the lake you painted is already there. (The map still
    // draws only water you could actually see, so it never shows a shoreline the
    // bake does not produce; the hover readout is where latent water shows up.)
    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Hard-edged, ignoring the falloff: a water surface is LEVEL, and easing
        // it in by weight would tilt the rim of every stroke into a ring of
        // half-steps — the same reason Flatten ignores it.
        float voxels = erase ? ctx.NoWaterVoxels : ClampSurface(ctx, SurfaceVoxels);
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            ctx.Water.SetPixel(px, pz, new Color(voxels, 0f, 0f, 1f));
        });
    }

    private static int ClampSurface(WorldMapState ctx, int voxels)
    {
        return Mathf.Clamp(voxels,
            Mathf.RoundToInt(ctx.Data.minElevationVoxels),
            Mathf.RoundToInt(ctx.Data.maxElevationVoxels));
    }

    public void Cycle(WorldMapState ctx, int dir)
    {
        AdjustLevel(ctx, dir);
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        SurfaceVoxels = ClampSurface(ctx, SurfaceVoxels + dir * ctx.StepVoxels);
    }
}

// Water shaded by depth over dimmed land — the map for deciding where water goes
// and how deep it is.
//
// It CUTS AWAY like the voxel-edit views, so the same T/G that drops the plane
// into a passage lets you paint water in it. Harmless on the surface: the cutaway
// starts at the top of the world, where every column's floor is its own ground
// and nothing reads as roofed, so the map is exactly what it always was until you
// lower the plane.
public class WaterView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public bool CutsAway => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.None;

    // How far dry land is dimmed, so the water reads as the subject of this map.
    private const float DryLandShade = 0.45f;

    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        int clip = ctx.CutawayY;
        int floor = ctx.CutawayFloor(px, pz, clip, out bool roofed);
        if (floor < ctx.Data.WorldMinY)
        {
            return ctx.Data.cutawayRockColor;
        }
        Color band = ctx.ElevationColorAt(floor - ctx.SeaLevel);
        if (roofed)
        {
            return band;   // the painter dithers it
        }
        return Mathf.Min(ctx.WaterSurface(px, pz), clip) > floor
            ? ctx.WithWaterOver(band, px, pz, floor, clip)
            : new Color(band.R * DryLandShade, band.G * DryLandShade, band.B * DryLandShade);
    }
}
