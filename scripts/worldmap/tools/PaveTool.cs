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
// Only the top voxel is paved. The rock under a road is still the hillside's,
// and the kit channel keeps its own value: it says what the column is made of,
// which a road laid over it does not change.
public class PaveTool : IWorldMapTool
{
    public string Name => "Paving";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 3f;

    public int BlockIndex = 0;

    public PaveTool()
    {
        View = new PaveView();
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

    public string HintText(WorldMapState ctx) => "RMB clears back to the ground's own block";

    public string StatusText(WorldMapState ctx)
    {
        BlockData[] blocks = ctx.PavingBlocks;
        string label = BlockIndex >= 0 && BlockIndex < blocks.Length ? blocks[BlockIndex]?.blockName : null;
        return string.IsNullOrEmpty(label) ? "No paving blocks authored" : label;
    }

    public string LevelText(WorldMapState ctx) => "";

    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Hard-edged, ignoring the falloff: a road has a kerb. Feathering an
        // index layer only means the rim of every stroke is a random scattering
        // of paved and unpaved columns.
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            ctx.SetPavingAt(px, pz, erase ? -1 : BlockIndex);
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

// The ground map — which resolves paving itself, so a road looks the same here
// as it does while painting props or mobs, and the ground either side of it is
// the context the road is being laid through.
public class PaveView : IWorldMapView
{
    public bool ShowsAllSteps => true;
    public bool DrawsWater => true;
    public ESpawnPreview PreviewLayer => ESpawnPreview.Props;

    public Color ColorAt(WorldMapState ctx, int px, int pz) => ctx.GroundColorAt(px, pz);
}
