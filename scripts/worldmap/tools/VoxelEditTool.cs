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
//
// A fill is built of a BLOCK (the option row, from BuildingBlocks) or of the
// column's own ground terrain, and it has two ways to place the box (X): hung
// off `PaintY`, so its top is level, or WALL mode, `Height` voxels stood on each
// column's own ground, so its top follows the slope.
public abstract class VoxelEditTool : IWorldMapTool
{
    public abstract string Name { get; }

    // What LMB leaves behind: true for ground, false for air.
    protected abstract bool PaintsSolid { get; }

    public IWorldMapView View { get; }
    public float Radius { get; set; } = 6f;

    // The FLOOR the edit leaves you standing on, and how many metres tall the box
    // is. A carve opens the Height metres ABOVE PaintY; a fill runs DOWN to it,
    // so PaintY is the new surface. Either way PaintY is the elevation of the
    // floor you end up with — which is what the eyedropper samples and the HUD
    // reports, so alt+clicking an existing passage and painting carries it on at
    // the same height rather than a metre below it.
    public int PaintY = 4;
    public int Height = 3;

    // The BuildingBlocks index a fill is built of, -1 for the column's ground
    // terrain. Fills only.
    public int BlockIndex = -1;

    // Fills only: stand the box on each column's painted ground instead of
    // hanging it off PaintY. Measured from the HEIGHT FIELD, not the top solid
    // voxel, so a drag over wall it has just built does not climb its own top.
    public bool WallMode;

    private bool Walls => WallMode && PaintsSolid;

    // Lowest voxel the box covers, in level mode.
    private int BottomY => PaintsSolid ? PaintY - Height + 1 : PaintY + 1;

    protected VoxelEditTool()
    {
        View = new CutawayElevationView();
    }

    // Row 0 is "Ground" — the column's own terrain — and the blocks follow it.
    // Q/E stays the brush height; a row is picked by clicking it or alt+click.
    public string[] Options(WorldMapState ctx)
    {
        if (!PaintsSolid)
        {
            return System.Array.Empty<string>();
        }
        BlockData[] blocks = ctx.BuildingBlocks;
        var names = new string[blocks.Length + 1];
        names[0] = "Ground";
        for (int i = 0; i < blocks.Length; i++)
        {
            names[i + 1] = blocks[i]?.blockName ?? $"Block {i}";
        }
        return names;
    }

    public Color[] OptionColors(WorldMapInk ink)
    {
        if (!PaintsSolid)
        {
            return null;
        }
        BlockData[] blocks = ink.Map.BuildingBlocks;
        var colors = new Color[blocks.Length + 1];
        colors[0] = ink.ElevationColorAt(PaintY - ink.Map.SeaLevel);
        for (int i = 0; i < blocks.Length; i++)
        {
            colors[i + 1] = blocks[i]?.minimapColor ?? Colors.White;
        }
        return colors;
    }

    public bool NumberKeys => false;

    public int OptionIndex
    {
        get => BlockIndex + 1;
        set => BlockIndex = Mathf.Max(0, value) - 1;
    }

    public string HintText(WorldMapState ctx)
    {
        string lmb = !PaintsSolid ? "tunnel (carves ABOVE the level)"
            : WallMode ? "wall (stands on the ground)"
            : "block (fills DOWN to the level)";
        return $"LMB {lmb}"
            + $"  |  RMB erase the whole {(PaintsSolid ? "slab" : "passage")} under the cut  |  "
            + (PaintsSolid ? "X level / wall  |  " : "")
            + "Q/E brush height  |  T/G cutaway  |  alt+LMB pick the level"
            + (PaintsSolid ? " and block" : "") + ", alt+RMB aim the cutaway";
    }

    // What a fill is built of, else the band of the floor being painted, so the
    // ring answers "what am I about to draw" against the map under it.
    public Color CursorColor(WorldMapInk ink)
    {
        BlockData block = Block(ink.Map);
        return block != null ? block.minimapColor : ink.ElevationColorAt(PaintY - ink.Map.SeaLevel);
    }

    public string StatusText(WorldMapState ctx, WorldMapView view)
    {
        if (!PaintsSolid)
        {
            return $"Brush h={Height}";
        }
        BlockData block = Block(ctx);
        return $"Brush h={Height}  |  {(block != null ? block.blockName?.ToString() ?? "Block" : "Ground")}";
    }

    // The plane itself is reported by the painter on every tool, so this names
    // only the box being written.
    public string LevelText(WorldMapState ctx, WorldMapView view) => Walls
        ? $"Wall {Height}m above the ground"
        : $"Y={PaintY} [{BottomY}..{BottomY + Height - 1}]";

    private BlockData Block(WorldMapState ctx)
    {
        BlockData[] blocks = ctx.BuildingBlocks;
        return PaintsSolid && BlockIndex >= 0 && BlockIndex < blocks.Length ? blocks[BlockIndex] : null;
    }

    public bool ToggleMode()
    {
        if (!PaintsSolid)
        {
            return false;
        }
        WallMode = !WallMode;
        return true;
    }

    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
        // Alt aims the brush at the floor under the cursor, the same eyedropper
        // the elevation and water tools have, and lands PaintY EXACTLY on the
        // elevation sampled — the number the HUD then shows is the one you
        // clicked, and because PaintY is the floor the edit leaves (BottomY),
        // painting from the pick continues that floor at the same height.
        //
        // Two things about WHICH floor. The highest one UNDER THE CUTAWAY,
        // because the floor you can see is the one you meant — sampling the
        // column's true top handed back the hilltop over a corridor instead of
        // the corridor's own floor. And a FLOOR, not merely the highest solid
        // voxel: on rock the latter is the cut plane itself, which is not a
        // surface anyone pointed at, so there the pick is a no-op.
        if ((mods & EStrokeMods.Pick) != 0)
        {
            int floor = ctx.CutawayFloor(texel.X, texel.Y, view.CutawayY, out _);
            if (floor >= ctx.Data.WorldMinY)
            {
                PaintY = floor;
                // Picking a built floor adopts what it is built of too, so a wall
                // is continued in its own stone; picking natural ground keeps the
                // block, since that is picking a level to build ON.
                if (PaintsSolid && ctx.IsAdded(texel.X, texel.Y, floor))
                {
                    BlockIndex = ctx.AddedBlockIndexAt(texel.X, texel.Y, floor);
                }
            }
        }
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Hard-edged, ignoring the falloff, for the reason Flatten is: a corridor
        // has one floor, and easing it in by weight would step its rim.
        bool wantsSolid = PaintsSolid;
        int clip = view.CutawayY;
        bool walls = Walls;
        int y0 = BottomY;
        int block = wantsSolid ? BlockIndex : -1;
        brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            if (erase)
            {
                EraseRun(ctx, px, pz, clip, wantsSolid);
                return;
            }
            int th = ctx.TerrainHeight(px, pz);
            int bottom = walls ? th + 1 : y0;
            for (int i = 0; i < Height; i++)
            {
                int wy = bottom + i;
                bool solidHere = wy <= th;
                byte edit = solidHere == wantsSolid
                    ? WorldMapState.EditNone
                    : wantsSolid ? WorldMapState.EditAdd : WorldMapState.EditCarve;
                ctx.SetVoxelEdit(px, pz, wy, edit, block);
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
    // A wall stands on the surface, so it leaves the plane where it is.
    public int? CutawayFor(int headroom) => Walls ? null : PaintY + headroom;

    // R/F moves the level, which a wall does not have.
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        if (Walls)
        {
            return;
        }
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
// valley, a wall. The same brush, the same keys, the same cutaway; only which
// way LMB writes differs, which is why it is a six-line subclass rather than a
// tool. Its box hangs DOWN from the level, so the level is the deck you are
// laying and the thickness goes under it out of sight — or, in wall mode, it
// stands on the ground.
public class BlockTool : VoxelEditTool
{
    public override string Name => "Block";
    protected override bool PaintsSolid => true;
}

// The elevation map, CUT AWAY at WorldMapView.CutawayY: every column draws the
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

    public Color ColorAt(WorldMapInk ink, int px, int pz)
    {
        return ink.CutawayColorAt(px, pz, ink.View.CutawayY, out _);
    }
}
