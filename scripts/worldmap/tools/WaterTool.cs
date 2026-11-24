using Godot;

// Paints water as a BOX, like the tunnel tool: `Depth` voxels (T/G) hung down
// from the surface level (R/F), in every column the brush covers. Water is per
// voxel, so a pool in a cave and the lake above it are separate bodies with their
// own levels and types. The box writes buried voxels too — that water is latent,
// and carving the land away later reveals it. X cycles Box / To bottom (the
// whole column below the surface) / Retype (only retypes water in the box).
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

    // Which water TYPE the brush lays down — an index into
    // the document's water palette, or -1 for "the zone's", which is entry 0 of the
    // option row and what an unpainted column already means.
    private int _typeIndex = -1;

    // How many voxels the box reaches down from the surface (T/G).
    public int Depth = 4;
    private const int MaxDepth = 64;

    public enum EWaterMode
    {
        Box,
        // Every voxel from the surface to the world floor: a sea or a lake whose
        // bed is wherever the ground is.
        ToBottom,
        // Only retypes water already in the box; never creates or removes any.
        // Recolouring a lake must not move its surface.
        Retype,
    }

    public EWaterMode Mode = EWaterMode.Box;

    public WaterTool()
    {
        View = new WaterView();
    }

    // Entry 0 is "the zone's water" — the unpainted state — so the row always
    // has a way back to it, and it is not a special case anywhere else.
    public string[] Options(WorldMapState ctx)
    {
        BlockData[] types = ctx.WaterTypes;
        var names = new string[types.Length + 1];
        names[0] = "Zone's";
        for (int i = 0; i < types.Length; i++)
        {
            names[i + 1] = types[i] != null ? types[i].blockName.ToString() : "<empty>";
        }
        return names;
    }

    // Each type's own minimapColor, which is what the block already authors for
    // "what does this look like from above" — a second palette would only drift
    // from it.
    public Color[] OptionColors(WorldMapInk ink)
    {
        BlockData[] types = ink.Map.WaterTypes;
        var colors = new Color[types.Length + 1];
        colors[0] = ink.Data.shallowWaterColor;
        for (int i = 0; i < types.Length; i++)
        {
            colors[i + 1] = types[i] != null ? types[i].minimapColor : Colors.Magenta;
        }
        return colors;
    }

    // No 1-9: water types come from the block catalog, so the first nine rows are an arbitrary prefix that
    // moves whenever one is added.
    public bool NumberKeys => false;

    public int OptionIndex
    {
        get => _typeIndex + 1;
        set => _typeIndex = Mathf.Max(-1, value - 1);
    }

    public bool ToggleMode(WorldMapState ctx)
    {
        Mode = (EWaterMode)(((int)Mode + 1) % 3);
        return true;
    }

    public void AdjustBrushHeight(WorldMapState ctx, int dir)
    {
        Depth = Mathf.Clamp(Depth + dir, 1, MaxDepth);
    }

    private int TopY(WorldMapState ctx) => ctx.SeaLevel + SurfaceVoxels;

    private int BottomY(WorldMapState ctx)
        => Mode == EWaterMode.ToBottom ? ctx.Data.WorldMinY : TopY(ctx) - Depth + 1;

    // The type about to be laid down, so the brush ring answers "what am I
    // painting" against the map under it — the same thing the elevation tool's
    // ring does with its target band.
    public Color CursorColor(WorldMapInk ink)
    {
        BlockData[] types = ink.Map.WaterTypes;
        return _typeIndex >= 0 && types != null && _typeIndex < types.Length && types[_typeIndex] != null
            ? types[_typeIndex].minimapColor
            : ink.Data.shallowWaterColor;
    }

    public string HintText(WorldMapState ctx)
        => "X box / to bottom / retype  |  R/F surface  |  T/G depth  |  Q/E cutaway  |  "
        + "alt+LMB samples a height, alt+RMB aims the cutaway  |  "
        + "RMB removes the water in the box (retype: back to the zone's)";

    public string StatusText(WorldMapState ctx, WorldMapView view)
        => Mode switch
        {
            EWaterMode.ToBottom => "Fills from the surface to the bottom",
            EWaterMode.Retype => $"Retypes water in the box, depth {Depth}",
            _ => $"Fills a box {Depth} deep",
        };

    public string LevelText(WorldMapState ctx, WorldMapView view)
    {
        BlockData[] types = ctx.WaterTypes;
        string type = _typeIndex >= 0 && types != null && _typeIndex < types.Length
            && types[_typeIndex] != null
            ? types[_typeIndex].blockName.ToString()
            : "the zone's";
        return $"Type {type}  |  Surface {SurfaceVoxels:+#;-#;0}v (Y={TopY(ctx)})";
    }

    public PaintSpan? PaintSpanAt(WorldMapState ctx, int px, int pz)
        => new PaintSpan(TopY(ctx), BottomY(ctx), TopY(ctx));

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;

    // alt+click aims the brush at a height already on the map — the same
    // eyedropper the elevation tool has, and for the same reason: picking the
    // terrace you want the water to meet beats stepping R/F forty times.
    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
        if ((mods & EStrokeMods.Pick) != 0)
        {
            // The water under the cursor, or the ground where it has none —
            // pointing at the terrace you want the surface to meet is the fast
            // way to aim, the same as the elevation tool's pick. Read UNDER the
            // cutaway, so pointing into a tunnel picks that tunnel's floor
            // rather than the hilltop over it.
            int clip = view.CutawayY;
            int floor = ctx.CutawayFloor(texel.X, texel.Y, clip, out _);
            if (floor < ctx.Data.WorldMinY)
            {
                return;   // solid rock at the cut: nothing to sample
            }
            // The water standing on that floor, so aiming into a tunnel picks
            // the pool in it rather than the lake above.
            int ws = ctx.WaterTopOver(texel.X, texel.Y, floor);
            int picked = ws > floor && ws <= clip ? ws : floor;
            SurfaceVoxels = ClampSurface(ctx, picked - ctx.SeaLevel);
        }
    }

    // LMB fills the box, RMB empties it. Hard-edged, ignoring the falloff: a
    // water surface is LEVEL, and a type is an index with no such thing as half
    // of one.
    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        int top = TopY(ctx);
        int bottom = Mathf.Max(BottomY(ctx), ctx.Data.WorldMinY);
        int code = WorldMapState.WaterCodeFor(_typeIndex);
        bool retype = Mode == EWaterMode.Retype;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            for (int wy = bottom; wy <= top; wy++)
            {
                if (retype)
                {
                    if (ctx.WaterAt(px, pz, wy))
                    {
                        ctx.SetWaterAt(px, pz, wy, erase ? WorldMapState.ZoneWaterCode : code);
                    }
                    continue;
                }
                ctx.SetWaterAt(px, pz, wy, erase ? WorldMapState.NoWaterCode : code);
            }
        });
    }

    private static int ClampSurface(WorldMapState ctx, int voxels)
    {
        return Mathf.Clamp(voxels,
            Mathf.RoundToInt(ctx.Data.minElevationVoxels),
            Mathf.RoundToInt(ctx.Data.maxElevationVoxels));
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        SurfaceVoxels = ClampSurface(ctx, SurfaceVoxels + dir * ctx.StepVoxels);
    }
}

// Water shaded by depth over dimmed land — the map for deciding where water goes
// and how deep it is.
//
// It CUTS AWAY like the voxel-edit views, so the same Q/E that drops the plane
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

    public Color ColorAt(WorldMapInk ink, int px, int pz)
    {
        int clip = ink.View.CutawayY;
        int floor = ink.Map.CutawayFloor(px, pz, clip, out bool roofed);
        if (floor < ink.Map.Data.WorldMinY)
        {
            return ink.Data.cutawayRockColor;
        }
        Color band = ink.ElevationColorAt(floor - ink.Map.SeaLevel);
        if (roofed)
        {
            return band;   // the painter dithers it
        }
        return Mathf.Min(ink.Map.WaterTopOver(px, pz, floor), clip) > floor
            ? ink.WithWaterOver(band, px, pz, floor, clip)
            : new Color(band.R * DryLandShade, band.G * DryLandShade, band.B * DryLandShade);
    }
}
