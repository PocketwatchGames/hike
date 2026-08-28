using Godot;

// Plain block drawing against the voxel-edit layer, shared by the two tools that
// do it. The brush is a BOX — `Radius` wide, `Height` tall, hung off `PaintY` —
// and it writes one direction only:
//
//   LMB  makes the box what this tool is FOR (air for Tunnel, ground for Block)
//   RMB  reverts the box to the height field
//
// One direction per tool, because that is the painter's convention everywhere
// else: LMB does the thing and RMB undoes it (water fills / removes, climb marks
// / unmarks, paving lays / lifts). Putting carve on one button and build on the
// other made RMB a second POSITIVE action, which is the one shape none of the
// other tools have.
//
// **The layer records only a DISAGREEMENT with the height field.** Carving a
// voxel that is already air writes nothing, and neither does filling one the
// terrain already fills; RMB writes `EditNone` outright. So erasing a tunnel
// restores the hillside and CANNOT leave blocks standing where the height field
// has none, drawing a block back into a hole you cut leaves the mask genuinely
// empty rather than holding a cancelling pair, and `CanSpawnAt` stays honest
// because it asks whether the top solid voxel is still the painted ground.
public abstract class VoxelEditTool : IWorldMapTool
{
    public abstract string Name { get; }

    // What LMB leaves behind: true for ground, false for air.
    protected abstract bool PaintsSolid { get; }

    public IWorldMapView View { get; }
    public float Radius { get; set; } = 6f;

    // The elevation the brush is aimed at, and how many metres tall the box is.
    //
    // The box hangs off it in the direction the tool writes: a carve runs UP from
    // it (so PaintY is the first metre removed and you are left standing on the
    // one below), a fill runs DOWN from it (so PaintY is the new surface). That
    // is the voxel-editor rule — RMB takes the voxel you point at, LMB puts one
    // on top of it — and it is what makes alt+click land the same way for both:
    // the pick sets PaintY to the first free metre over the floor you clicked, so
    // carving keeps that floor and blocking raises it by one.
    public int PaintY = 4;
    public int Height = 3;

    // Lowest voxel the box covers.
    private int BottomY => PaintsSolid ? PaintY - Height + 1 : PaintY;

    protected VoxelEditTool()
    {
        View = new CutawayElevationView();
    }

    public string[] Options(WorldMapState ctx) => System.Array.Empty<string>();
    public Color[] OptionColors(WorldMapState ctx) => null;

    public int OptionIndex { get => 0; set { } }

    public string HintText(WorldMapState ctx) =>
        $"LMB {(PaintsSolid ? "block (fills DOWN from the level)" : "tunnel (carves UP from the level)")}"
        + $"  |  RMB erase the whole {(PaintsSolid ? "slab" : "passage")} under the cut  |  "
        + "Q/E brush height  |  T/G cutaway  |  alt+LMB pick the level, "
        + "alt+RMB aim the cutaway";

    // The band of the floor being painted, so the ring answers "what height am I
    // drawing at" against the map it is hovering over.
    public Color CursorColor(WorldMapState ctx) => ctx.ElevationColorAt(PaintY - ctx.SeaLevel);

    public string StatusText(WorldMapState ctx) => $"Brush h={Height}";

    // The plane itself is reported by the painter on every tool, so this names
    // only the box being written.
    public string LevelText(WorldMapState ctx) =>
        $"Y={PaintY} [{BottomY}..{BottomY + Height - 1}]";

    public void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods)
    {
        // Alt aims the brush at the floor under the cursor, the same eyedropper
        // the elevation and water tools have, and lands PaintY EXACTLY on the
        // elevation sampled — the number the HUD then shows is the one you
        // clicked. It briefly picked one metre above (so a carve would preserve
        // the floor it sampled rather than take it); that made the readout
        // disagree with every pick, and an eyedropper whose value is not the
        // value you pointed at is not an eyedropper.
        //
        // Two things about WHICH floor. The highest one UNDER THE CUTAWAY,
        // because the floor you can see is the one you meant — sampling the
        // column's true top handed back the hilltop over a corridor instead of
        // the corridor's own floor. And a FLOOR, not merely the highest solid
        // voxel: on rock the latter is the cut plane itself, which is not a
        // surface anyone pointed at, so there the pick is a no-op.
        if ((mods & EStrokeMods.Pick) != 0)
        {
            int floor = ctx.CutawayFloor(texel.X, texel.Y, ctx.CutawayY, out _);
            if (floor >= ctx.Data.WorldMinY)
            {
                PaintY = floor;
            }
        }
    }

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Hard-edged, ignoring the falloff, for the reason Flatten is: a corridor
        // has one floor, and easing it in by weight would step its rim.
        bool wantsSolid = PaintsSolid;
        int clip = ctx.CutawayY;
        int y0 = BottomY;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            if (erase)
            {
                EraseRun(ctx, px, pz, clip, wantsSolid);
                return;
            }
            int th = ctx.TerrainHeight(px, pz);
            for (int i = 0; i < Height; i++)
            {
                int wy = y0 + i;
                bool solidHere = wy <= th;
                byte edit = solidHere == wantsSolid
                    ? WorldMapState.EditNone
                    : wantsSolid ? WorldMapState.EditAdd : WorldMapState.EditCarve;
                ctx.SetVoxelEdit(px, pz, wy, edit);
            }
        });
    }

    // RMB removes the WHOLE thing you made at this column — the contiguous run of
    // this tool's own edit touching the floor the cut exposes, however far it
    // reaches ABOVE the cut. A box-shaped bite out of a passage leaves a metre of
    // it behind and needs the brush aimed at a height you may not know; "undo
    // what is here" needs neither, and it is the same gesture whatever the brush
    // happens to be set to.
    //
    // Only where the cut is OPEN to that floor. A passage under rock draws dimmed
    // precisely because you are seeing it through something, and erasing what you
    // cannot see the top of is how a network loses a corridor silently. Lower the
    // cutaway into it and it is erasable like anything else.
    private static void EraseRun(WorldMapState ctx, int px, int pz, int clipY, bool wantsSolid)
    {
        int floor = ctx.CutawayFloor(px, pz, clipY, out bool roofed);
        if (roofed || floor < ctx.Data.WorldMinY)
        {
            return;
        }
        // A carve stands ABOVE the floor it left; an added slab IS the floor and
        // stacks below it.
        byte mine = wantsSolid ? WorldMapState.EditAdd : WorldMapState.EditCarve;
        int step = wantsSolid ? -1 : 1;
        for (int wy = wantsSolid ? floor : floor + 1;
            ctx.VoxelEdit(px, pz, wy) == mine;
            wy += step)
        {
            ctx.SetVoxelEdit(px, pz, wy, WorldMapState.EditNone);
        }
    }

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;
    public Rect2I? LastPaintRect => null;

    public void Cycle(WorldMapState ctx, int dir)
    {
        Height = Mathf.Clamp(Height + dir, 1, 16);
    }

    // Picking this tool up drops the plane just over the level it paints at, so
    // the map is showing the ground you are about to work on rather than
    // whatever slice was left over from the last tool.
    public int? CutawayFor(int headroom) => PaintY + headroom;

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        PaintY = Mathf.Clamp(PaintY + dir, ctx.Data.WorldMinY, ctx.Data.WorldMaxY);
    }
}

// Bores passages: LMB turns the box to air, RMB puts the hillside back.
public class TunnelTool : VoxelEditTool
{
    public override string Name => "Tunnel";
    protected override bool PaintsSolid => false;
}

// Builds ground where there is none — a bridge deck, a ledge, an arch over a
// valley. The same brush, the same keys, the same cutaway; only which way LMB
// writes differs, which is why it is a six-line subclass rather than a tool.
// Its box hangs DOWN from the level, so the level is the deck you are laying and
// the thickness goes under it out of sight.
public class BlockTool : VoxelEditTool
{
    public override string Name => "Block";
    protected override bool PaintsSolid => true;
}

// The elevation map, CUT AWAY at WorldMapState.CutawayY: every column draws the
// band of the highest floor under the cut, so the map sees THROUGH a mountain to
// the passage beneath it, and only rock with nothing hollow anywhere below draws
// flat cutawayRockColor. A floor found through rock keeps its exact band and is
// dithered against the rock colour by the painter.
//
// Shared by the tools whose subject is under the ground — the voxel-edit pair
// and the climb tool. They differ in what they PAINT and in the ink the outline
// pass lays over them, not in how the terrain is drawn, so one view rather than
// copies that drift.
public class CutawayElevationView : IWorldMapView
{
    public ESpawnPreview PreviewLayer => ESpawnPreview.None;

    // The bands say the height here, exactly as they do on the elevation map.
    public bool ShowsAllSteps => false;
    public bool DrawsWater => true;
    public bool CutsAway => true;

    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        return ctx.CutawayColorAt(px, pz, ctx.CutawayY, out _);
    }
}
