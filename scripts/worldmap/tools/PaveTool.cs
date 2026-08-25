using Godot;

// Paves a column's surface with a specific block — roads, plazas, floors.
//
// A BLOCK, not an overlay, which is where this parts company with worldgen's
// road pass: that lays a BlockSurfaceData tread as an additive skin over the
// kit's block, so it blends softly into the terrain but carries no material
// properties at all (no footstep sound, no speed multiplier, no dig yield) and
// occupies the one overlay slot climbing routes and moss also want. A painted
// road is a deliberate, hand-placed thing, so it gets to BE its material — the
// same call StampDirtPatches makes.
//
// Only ONE voxel is paved, and WHICH one is the floor the map is showing:
// paving lands on ctx.CutawayFloor, so with the plane parked over the world it
// is the surface, and lowering it into a passage or under an arch paves the
// floor down there instead. That is why the tool needs no level of its own —
// T/G (and alt+RMB) already aim the cutaway, and a level you cannot see is a
// level you cannot aim. A road on open ground records the surface SENTINEL
// rather than that Y, so it keeps following ground repainted under it; only a
// floor with something above it stores an absolute Y, which is the same split
// EntityPlacement.floorY makes.
//
// The rock under a road is still the hillside's, and the kit channel keeps its
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
        BlockData[] blocks = ctx.PavingBlocks;
        var names = new string[blocks.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = blocks[i]?.blockName ?? $"Block {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapState ctx)
    {
        BlockData[] blocks = ctx.PavingBlocks;
        var colors = new Color[blocks.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = blocks[i]?.minimapColor ?? Colors.White;
        }
        return colors;
    }

    public int OptionIndex
    {
        get => BlockIndex;
        set => BlockIndex = Mathf.Max(0, value);
    }

    public Color CursorColor(WorldMapState ctx) => Shade(ctx, BlockIndex);

    // A block already authors what it looks like from above, for the minimap.
    // Reusing it means a painted road reads the same colour in both places
    // without a second palette to keep in sync.
    public static Color Shade(WorldMapState ctx, int index)
    {
        BlockData[] blocks = ctx.PavingBlocks;
        return index >= 0 && index < blocks.Length && blocks[index] != null
            ? blocks[index].minimapColor
            : Colors.White;
    }

    public string HintText(WorldMapState ctx) =>
        "LMB pave the floor the map is showing  |  RMB lift the column's paving  |  "
        + "T/G cutaway, alt+RMB aim it";

    public string StatusText(WorldMapState ctx)
    {
        BlockData[] blocks = ctx.PavingBlocks;
        string label = BlockIndex >= 0 && BlockIndex < blocks.Length ? blocks[BlockIndex]?.blockName : null;
        return string.IsNullOrEmpty(label) ? "No paving blocks authored" : label;
    }

    // Which floor the stroke would land on, since that is now the tool's real
    // parameter and it lives on the shared cutaway rather than on the tool.
    public string LevelText(WorldMapState ctx) =>
        ctx.IsCutAway ? $"Floors under cutaway Y={ctx.CutawayY}" : "Surface";

    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Hard-edged, ignoring the falloff: a road has a kerb. Feathering an
        // index layer only means the rim of every stroke is a random scattering
        // of paved and unpaved columns.
        //
        // Erase clears the column outright rather than only the paving at the
        // floor on screen. There is one paving per column, so "lift what is
        // here" cannot be ambiguous — and a seat orphaned by terrain repainted
        // under it would otherwise be unreachable from every plane.
        int clip = ctx.CutawayY;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            if (erase)
            {
                ctx.SetPavingAt(px, pz, -1);
            }
            else if (ctx.TryPavingLevel(px, pz, clip, out int level))
            {
                ctx.SetPavingAt(px, pz, BlockIndex, level);
            }
        });
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;
    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = Mathf.Max(1, ctx.PavingBlocks.Length);
        BlockIndex = ((BlockIndex + dir) % n + n) % n;
    }

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

    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        return ctx.IsCutAway
            ? ctx.CutawayColorAt(px, pz, ctx.CutawayY, out _)
            : ctx.GroundColorAt(px, pz);
    }
}
