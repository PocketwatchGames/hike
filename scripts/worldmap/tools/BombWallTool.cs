using Godot;

// Authors bomb walls (BombWallSpawnEntry) as boxes of voxels: every cell of the
// box gives way when the wall is broken. A wall MARKS rock the world already has
// there and adds none, so the box is drawn over rock the other tools built.
//
// The box gestures are BoxPlacementTool's. R/F moves the box's TOP, shift+R/F
// its BOTTOM (R reaches further down), so the box is edited in Y the way it is in
// X and Z — by its faces. A new box stands on the floor the map shows under the
// press, NewHeight tall; where the press is on rock with no floor under the
// cut, it hangs down from the plane instead, which is where alt+RMB puts the
// plane over a passage. Moving or resizing never re-seats it: it is an absolute
// volume of voxels.
//
// A wall is an ordinary ENTITY placement from the palette's bomb-wall entry, so
// it is in the entities list, names, hooks and its disabled gate are set in the
// property panel, and the entity tool selects it too. The box is not stored:
// it is derived from the cells (WorldMapState.BombWallBox), which are the one
// thing the bake reads. The placement keeps an ABSOLUTE floorY — a ground-seated
// one is re-seated from the terrain at every bake and would slide its cells off
// the rock the moment the hill was repainted.
public class BombWallTool : BoxPlacementTool
{
    public override string Name => "Bomb walls";

    private int _entryIndex;

    private const int NewHeight = 3;

    // The palette's bomb-wall entries: what a new box is made from.
    private static BombWallSpawnEntry[] Entries(WorldMapState ctx)
    {
        var list = new System.Collections.Generic.List<BombWallSpawnEntry>();
        foreach (SpawnEntryData entry in ctx.EntityPalette)
        {
            if (entry is BombWallSpawnEntry wall)
            {
                list.Add(wall);
            }
        }
        return list.ToArray();
    }

    public override string[] Options(WorldMapState ctx)
    {
        BombWallSpawnEntry[] entries = Entries(ctx);
        var names = new string[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            names[i] = SpawnEntryData.PaletteName(entries[i]).Capitalize();
        }
        return names;
    }

    public override int OptionIndex
    {
        get => _entryIndex;
        set => _entryIndex = Mathf.Max(0, value);
    }

    public override Color CursorColor(WorldMapInk ink) => ink.Data.bombWallSelectedInk;

    public override string HintText(WorldMapState ctx)
        => "Drag over rock to draw a wall  |  drag inside to move, on its border to resize "
            + "(Shift: always move)  |  R/F top, Shift+R/F bottom  |  RMB delete  |  "
            + "name, hooks and gate in the panel";

    public override string StatusText(WorldMapState ctx, WorldMapView view)
    {
        if (WorldMapState.BombWallOf(Selected) == null)
        {
            int count = 0;
            foreach (EntityPlacement p in ctx.Placements.entities)
            {
                count += WorldMapState.BombWallOf(p) != null ? 1 : 0;
            }
            return Entries(ctx).Length == 0
                ? "No bomb wall entry in the entity palette"
                : $"{count} bomb wall(s)";
        }
        (Vector3I min, Vector3I size) = ctx.BombWallBox(Selected);
        WorldMapState.BombWallSurvey s = ctx.SurveyBombWall(Selected);
        string warn = s.OpenFaced == 0 ? "  -  NO EXPOSED ROCK" : s.Wet > 0 ? "  -  TOUCHES WATER" : "";
        return $"{Selected.Name ?? "unnamed"}: {size.X}x{size.Z} m, {s.Cells} cells, "
            + $"{s.Solid} rock, {s.OpenFaced} exposed{warn}";
    }

    public override string LevelText(WorldMapState ctx, WorldMapView view)
    {
        if (WorldMapState.BombWallOf(Selected) == null)
        {
            return "";
        }
        (Vector3I min, Vector3I size) = ctx.BombWallBox(Selected);
        return $"y={min.Y}..{min.Y + size.Y - 1}";
    }

    protected override EntityPlacement BoxAt(WorldMapState ctx, Vector2I texel, int clipY, int grow)
        => ctx.BombWallAt(texel.X, texel.Y, clipY, grow);

    protected override Rect2I BoxTexelRect(WorldMapState ctx, EntityPlacement box) => ctx.BombWallTexelRect(box);

    protected override EntityPlacement CreateBox(WorldMapState ctx, WorldMapView view, Rect2I texelRect)
    {
        BombWallSpawnEntry[] entries = Entries(ctx);
        if (entries.Length == 0)
        {
            return null;
        }
        var placement = new EntityPlacement { source = entries[Mathf.Clamp(_entryIndex, 0, entries.Length - 1)] };
        Vector2I press = PressTexel;
        int floor = ctx.PlacementFloor(press.X, press.Y, view.CutawayY);
        int bottom = floor >= ctx.Data.WorldMinY
            ? floor + 1
            : Mathf.Min(view.CutawayY, ctx.TerrainHeight(press.X, press.Y)) - NewHeight + 1;
        WriteBox(ctx, placement, new Rect2I(ctx.WorldXZ(texelRect.Position), texelRect.Size), bottom, NewHeight);
        ctx.AddEntity(placement);
        return placement;
    }

    protected override void RemoveBox(WorldMapState ctx, EntityPlacement box) => ctx.RemoveEntity(box);

    protected override void ApplyRect(WorldMapState ctx, WorldMapView view, EntityPlacement box, Rect2I texelRect)
    {
        (Vector3I min, Vector3I size) = ctx.BombWallBox(box);
        WriteBox(ctx, box, new Rect2I(ctx.WorldXZ(texelRect.Position), texelRect.Size), min.Y, size.Y);
    }

    // R/F: the top face.
    public override void AdjustLevel(WorldMapState ctx, int dir)
    {
        if (WorldMapState.BombWallOf(Selected) == null)
        {
            return;
        }
        (Vector3I min, Vector3I size) = ctx.BombWallBox(Selected);
        WriteBox(ctx, Selected, FootprintOf(min, size), min.Y, Mathf.Max(1, size.Y + dir));
    }

    // Shift+R/F: the bottom face, R reaching further down.
    public override bool AdjustSecondaryLevel(WorldMapState ctx, int dir)
    {
        if (WorldMapState.BombWallOf(Selected) == null)
        {
            return true;
        }
        (Vector3I min, Vector3I size) = ctx.BombWallBox(Selected);
        int height = Mathf.Max(1, size.Y + dir);
        WriteBox(ctx, Selected, FootprintOf(min, size), min.Y + size.Y - height, height);
        return true;
    }

    private static Rect2I FootprintOf(Vector3I min, Vector3I size)
        => new(new Vector2I(min.X, min.Z), new Vector2I(size.X, size.Z));

    // Write a world box onto a wall: anchored at its min corner, the floor
    // absolute and just under its bottom, unturned, and every voxel of the box as
    // a cell offset from the exact position the bake will spawn it at. A NEW
    // cells array every time — undo captures the old one by reference, and the
    // fork is shallow, so writing in place would also reach the palette file.
    private static void WriteBox(WorldMapState ctx, EntityPlacement placement, Rect2I worldRect, int bottomY, int height)
    {
        var wall = (BombWallSpawnEntry)placement.EditableEntry();
        placement.anchorXZ = worldRect.Position;
        placement.floorY = bottomY - 1;
        placement.facing = EEntityFacing.Deg0;
        Vector3 at = ctx.EntitySpawnPosition(placement);
        float facing = placement.FacingRadians;
        var cells = new Godot.Collections.Array<Vector3I>();
        for (int x = worldRect.Position.X; x < worldRect.End.X; x++)
        {
            for (int z = worldRect.Position.Y; z < worldRect.End.Y; z++)
            {
                for (int y = bottomY; y < bottomY + height; y++)
                {
                    cells.Add(BombWallSpawnEntry.LocalOffset(at, facing, new Vector3I(x, y, z)));
                }
            }
        }
        wall.cells = cells;
    }
}
