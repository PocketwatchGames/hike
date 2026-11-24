using Godot;

// Paves a column's surface with a specific block — roads, plazas, floors.
//
// A BLOCK, not an overlay, which is where this parts company with worldgen's
// road pass: that lays a BlockSurfaceData tread as an additive skin over the
// terrain's block, so it blends softly into the terrain but carries no material
// properties at all (no footstep sound, no speed multiplier, no dig yield) and
// occupies the one overlay slot climbing routes and moss also want. A painted
// road is a deliberate, hand-placed thing, so it gets to BE its material — the
// same call StampDirtPatches makes.
//
// Only ONE voxel is paved, and WHICH one is the floor the map is showing:
// paving lands on ctx.CutawayFloor, so with the plane parked over the world it
// is the surface, and lowering it into a passage or under an arch paves the
// floor down there instead. That is why the tool needs no level of its own —
// Q/E (and alt+RMB) already aim the cutaway, and a level you cannot see is a
// level you cannot aim. A road on a column's surface goes in the column layer,
// so it keeps following ground repainted under it; a floor with something above
// it is a repaved voxel at that height — the same split EntityPlacement.floorY
// makes. A column carries one surface road and any number of repaved floors.
//
// The rock under a road is still the hillside's, and the terrain channel keeps its
// own value: it says what the column is made of, which a road laid over it does
// not change.
public class PaveTool : IWorldMapTool
{
    public string Name => "Paving";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 3f;

    public int BlockIndex = 0;

    public PaveTool()
    {
        View = new CutawayGroundView();
    }

    public string[] Options(WorldMapState ctx)
    {
        BlockData[] blocks = ctx.BuildingBlocks;
        var names = new string[blocks.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = blocks[i]?.blockName ?? $"Block {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapInk ink)
    {
        BlockData[] blocks = ink.Map.BuildingBlocks;
        var colors = new Color[blocks.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = blocks[i]?.minimapColor ?? Colors.White;
        }
        return colors;
    }

    // No 1-9: paving blocks come from the block catalog, so the first nine rows are an arbitrary prefix that
    // moves whenever one is added.
    public bool NumberKeys => false;

    public int OptionIndex
    {
        get => BlockIndex;
        set => BlockIndex = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapInk ink) => Shade(ink.Map, BlockIndex);

    // A block already authors what it looks like from above, for the minimap.
    // Reusing it means a painted road reads the same colour in both places
    // without a second palette to keep in sync.
    public static Color Shade(WorldMapState ctx, int index)
    {
        BlockData[] blocks = ctx.BuildingBlocks;
        return index >= 0 && index < blocks.Length && blocks[index] != null
            ? blocks[index].minimapColor
            : Colors.White;
    }

    public string HintText(WorldMapState ctx) =>
        "LMB pave the floor the map is showing  |  RMB lift the paving there  |  "
        + "Q/E cutaway, alt+RMB aim it";

    public string StatusText(WorldMapState ctx, WorldMapView view)
    {
        BlockData[] blocks = ctx.BuildingBlocks;
        string label = BlockIndex >= 0 && BlockIndex < blocks.Length ? blocks[BlockIndex]?.blockName : null;
        return string.IsNullOrEmpty(label) ? "No paving blocks authored" : label;
    }

    // Which floor the stroke would land on, since that is now the tool's real
    // parameter and it lives on the shared cutaway rather than on the tool.
    public string LevelText(WorldMapState ctx, WorldMapView view) =>
        view.IsCutAway ? "Floors under the cutaway" : "Surface";

    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Hard-edged, ignoring the falloff: a road has a kerb. Feathering an
        // index layer only means the rim of every stroke is a random scattering
        // of paved and unpaved columns.
        //
        // Paints the first floor under the cut: a column's surface writes the
        // surface road (which follows the ground), a floor with something over
        // it repaves that voxel. Erase lifts whichever is on the floor shown.
        int clip = view.CutawayY;
        int block = erase ? -1 : BlockIndex;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            PaveFloor(ctx, px, pz, clip, block);
        });
    }

    private static void PaveFloor(WorldMapState ctx, int px, int pz, int clip, int block)
    {
        if (!ctx.TryPaintFloor(px, pz, clip, out int floor, out bool onSurface))
        {
            return;
        }
        if (onSurface)
        {
            ctx.SetSurfacePaving(px, pz, block);
            // The surface road wins over a repave left on the same voxel.
            ctx.SetFloorPaving(px, pz, floor, -1);
            return;
        }
        ctx.SetFloorPaving(px, pz, floor, block);
    }

    // Floods the floor the map shows: same floor height, same paving there,
    // same ground kit - so a fill stops at a terrace step, a wall, the edge of
    // a road or a change of ground, which are the edges the map draws.
    // Columns with nothing pavable are flooded through but written nothing,
    // exactly as a brush stroke over them would be.
    public WorldMapFill Fill(WorldMapState ctx, WorldMapView view, bool erase)
    {
        int clip = view.CutawayY;
        return new WorldMapFill
        {
            KeyAt = (px, pz) =>
            {
                int floor = ctx.CutawayFloor(px, pz, clip, out _);
                BlockData paved = ctx.PavingAtFloor(px, pz, floor);
                int pavedIdx = paved != null ? System.Array.IndexOf(ctx.BuildingBlocks, paved) : -1;
                return ((long)floor << 32) | ((long)(pavedIdx + 1) << 16) | (long)(ctx.FloorKitAt(px, pz, floor) + 1);
            },
            Write = (px, pz) => PaveFloor(ctx, px, pz, clip, erase ? -1 : BlockIndex),
        };
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
    }
}

// The ground map, which cuts away once the plane comes down.
//
// Above ground it is the plain ground map — it resolves paving itself, so a road
// looks the same here as it does while painting props or mobs, and the ground
// either side of it is the context the road is being laid through. Lowering the
// plane switches it to the cutaway, because underground there is no ground TYPE
// to show: the ground layer says what the SURFACE is made of, and the question
// down there is which floor is exposed and whether it is paved already
// (CutawayColorAt resolves that).
//
// Shared by the tools that place things ON a floor — paving and entities. They
// differ in what they write, not in how the map is drawn, so one view rather
// than copies that drift.
public class CutawayGroundView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public bool CutsAway => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.Props;

    public Color ColorAt(WorldMapInk ink, int px, int pz)
    {
        return ink.View.IsCutAway
            ? ink.CutawayColorAt(px, pz, ink.View.CutawayY, out _)
            : ink.GroundColorAt(px, pz);
    }
}
