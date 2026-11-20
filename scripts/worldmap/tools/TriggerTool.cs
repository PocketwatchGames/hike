using Godot;

// Authors trigger boxes (TriggerSpawnEntry): axis-aligned regions that fire
// actions and world-script hooks as actors walk into and out of them.
//
// Drag on empty ground to draw a box. Drag inside one to move it; drag on its
// border — the outermost ring of cells or the cell just outside — to move that
// edge, or a corner to move two. R/F changes its height, shift+R/F its depth
// below the floor, RMB deletes it. Shift+drag always moves, for a box too thin
// to have an inside. Everything else a trigger does is set in the property
// panel, which edits the box's own entry.
//
// Every box is its own entry (no palette), like the scene editor's roofs, so
// the placement holds only `custom`. Seated like an entity: on the floor the
// map shows under its CENTRE, which a lowered cutaway can make a passage.
public class TriggerTool : IWorldMapTool
{
    public string Name => "Triggers";
    public IWorldMapView View { get; }

    public float Radius { get; set; } = 1f;

    public EntityPlacement Selected;

    public EntityPlacement SelectedEntity => Selected;

    // What the press is doing to the box under it.
    private enum EGrab
    {
        None,
        Create,
        Move,
        Resize,
    }

    private EGrab _grab;
    private EntityPlacement _pressHit;
    private Vector2I _pressTexel;
    private Rect2I _pressRect;
    // Which edges a resize moves: -1 the min edge, +1 the max edge, 0 neither.
    private int _edgeX;
    private int _edgeZ;
    private bool _strokeActed;
    private Rect2I? _dirty;

    // Height and depth a new box starts with, in metres.
    private const int NewHeightAbove = 4;
    private const int NewDepthBelow = 2;

    public TriggerTool()
    {
        View = new CutawayGroundView();
    }

    public string[] Options(WorldMapState ctx) => System.Array.Empty<string>();

    public Color[] OptionColors(WorldMapInk ink) => null;

    public bool NumberKeys => false;

    public int OptionIndex
    {
        get => 0;
        set { }
    }

    public Color CursorColor(WorldMapInk ink) => ink.Data.triggerSelectedInk;

    public string HintText(WorldMapState ctx)
        => "Drag on ground to draw a box  |  drag inside to move, on its border to resize "
            + "(Shift: always move)  |  R/F height, Shift+R/F depth  |  RMB delete  |  "
            + "name, actors and actions in the panel";

    public string StatusText(WorldMapState ctx, WorldMapView view)
    {
        TriggerSpawnEntry t = WorldMapState.TriggerOf(Selected);
        if (t == null)
        {
            return $"{ctx.Placements.triggers.Length} trigger(s)";
        }
        return $"{Selected.Name ?? "unnamed"}: {t.sizeX}x{t.sizeZ} m, +{t.heightAbove}/-{t.depthBelow}, {t.actors}";
    }

    public string LevelText(WorldMapState ctx, WorldMapView view)
    {
        if (Selected == null)
        {
            return "";
        }
        return Selected.floorY == EntityPlacement.OnTheGround ? "Seated on the ground" : $"Seated at y={Selected.floorY}";
    }

    // The box under the cursor, its border ring included, so hovering a handle
    // names the box it would resize.
    public EntityPlacement EntityUnder(WorldMapState ctx, WorldMapView view, Vector2I texel)
        => ctx.TriggerAt(texel.X, texel.Y, Clip(view), grow: 1);

    private int Clip(WorldMapView view) => view.ClipFor(View.CutsAway);

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;

    public Rect2I? LastPaintRect => _dirty;

    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
        _strokeActed = false;
        _dirty = null;
        _pressTexel = texel;
        _edgeX = 0;
        _edgeZ = 0;
        int clip = Clip(view);
        _pressHit = ctx.TriggerAt(texel.X, texel.Y, clip, grow: 1);
        if (_pressHit == null)
        {
            _grab = EGrab.Create;
            return;
        }
        _pressRect = ctx.TriggerTexelRect(_pressHit);
        if ((mods & EStrokeMods.Constrain) == 0)
        {
            _edgeX = EdgeOf(texel.X, _pressRect.Position.X, _pressRect.End.X);
            _edgeZ = EdgeOf(texel.Y, _pressRect.Position.Y, _pressRect.End.Y);
        }
        _grab = _edgeX != 0 || _edgeZ != 0 ? EGrab.Resize : EGrab.Move;
    }

    // Which edge of [min, end) a coordinate grabs. The cell just outside always
    // does; the box's own outer cell does only where the box is wide enough to
    // still have an inside to move it by.
    private static int EdgeOf(int at, int min, int end)
    {
        bool hasInside = end - min >= 3;
        if (at < min || (hasInside && at == min))
        {
            return -1;
        }
        if (at >= end || (hasInside && at == end - 1))
        {
            return 1;
        }
        return 0;
    }

    public void Paint(WorldMapState ctx, WorldMapView view, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        _dirty = null;

        if (erase)
        {
            if (!_strokeActed && _pressHit != null)
            {
                _strokeActed = true;
                _dirty = ctx.TriggerTexelRect(_pressHit).Grow(1);
                if (Selected == _pressHit)
                {
                    Selected = null;
                }
                ctx.RemoveTrigger(_pressHit);
            }
            return;
        }

        switch (_grab)
        {
            case EGrab.Create:
                PaintCreate(ctx, view, texel);
                return;
            case EGrab.Move:
            case EGrab.Resize:
                if (!_strokeActed)
                {
                    _strokeActed = true;
                    Selected = _pressHit;
                    // The selection's handles and wash change even when the
                    // box does not move.
                    _dirty = ctx.TriggerTexelRect(Selected).Grow(1);
                }
                if (texel == _pressTexel)
                {
                    return;
                }
                Reshape(ctx, view, _grab == EGrab.Move
                    ? new Rect2I(_pressRect.Position + (texel - _pressTexel), _pressRect.Size)
                    : Resized(texel));
                return;
        }
    }

    // A drag from empty ground draws a new box from the press to the cursor. A
    // click that never drags only clears the selection — a one-metre trigger is
    // never what a stray click meant.
    private void PaintCreate(WorldMapState ctx, WorldMapView view, Vector2I texel)
    {
        if (!_strokeActed)
        {
            _strokeActed = true;
            if (Selected != null)
            {
                _dirty = ctx.TriggerTexelRect(Selected).Grow(1);
                Selected = null;
            }
        }
        if (texel == _pressTexel && Selected == null)
        {
            return;
        }
        Vector2I min = new(Mathf.Min(texel.X, _pressTexel.X), Mathf.Min(texel.Y, _pressTexel.Y));
        Vector2I max = new(Mathf.Max(texel.X, _pressTexel.X), Mathf.Max(texel.Y, _pressTexel.Y));
        var rect = new Rect2I(min, max - min + Vector2I.One);
        if (Selected == null)
        {
            Selected = new EntityPlacement
            {
                custom = new TriggerSpawnEntry
                {
                    heightAbove = NewHeightAbove,
                    depthBelow = NewDepthBelow,
                },
            };
            ApplyRect(ctx, view, Selected, rect);
            ctx.AddTrigger(Selected);
            _dirty = rect.Grow(1).Merge(_dirty ?? rect);
            return;
        }
        Reshape(ctx, view, rect);
    }

    // The press rect with the grabbed edges moved to the cursor, never inverted
    // and never narrower than one metre.
    private Rect2I Resized(Vector2I texel)
    {
        int x0 = _pressRect.Position.X;
        int x1 = _pressRect.End.X - 1;
        int z0 = _pressRect.Position.Y;
        int z1 = _pressRect.End.Y - 1;
        if (_edgeX < 0)
        {
            x0 = Mathf.Min(texel.X, x1);
        }
        else if (_edgeX > 0)
        {
            x1 = Mathf.Max(texel.X, x0);
        }
        if (_edgeZ < 0)
        {
            z0 = Mathf.Min(texel.Y, z1);
        }
        else if (_edgeZ > 0)
        {
            z1 = Mathf.Max(texel.Y, z0);
        }
        return new Rect2I(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
    }

    private void Reshape(WorldMapState ctx, WorldMapView view, Rect2I texelRect)
    {
        Rect2I before = ctx.TriggerTexelRect(Selected);
        if (before == texelRect)
        {
            return;
        }
        ApplyRect(ctx, view, Selected, texelRect);
        _dirty = before.Grow(1).Merge(texelRect.Grow(1));
    }

    // Write a texel rect onto a box and re-seat it under its new centre, so a
    // box dragged along a passage keeps that passage's floor and one dragged
    // out of the mouth goes back to the ground.
    private static void ApplyRect(WorldMapState ctx, WorldMapView view, EntityPlacement placement, Rect2I texelRect)
    {
        var t = (TriggerSpawnEntry)placement.EditableEntry();
        placement.anchorXZ = ctx.WorldXZ(texelRect.Position);
        t.sizeX = texelRect.Size.X;
        t.sizeZ = texelRect.Size.Y;
        Vector2I seat = ctx.TexelXZ(t.SeatCell(placement.anchorXZ));
        placement.floorY = ctx.FloorForEntity(seat.X, seat.Y, view.CutawayY);
    }

    public void Cycle(WorldMapState ctx, int dir) { }

    // R/F: the selected box's height above its floor.
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        if (WorldMapState.TriggerOf(Selected) == null)
        {
            return;
        }
        var t = (TriggerSpawnEntry)Selected.EditableEntry();
        t.heightAbove = Mathf.Max(1, t.heightAbove + dir);
    }

    // Shift+R/F: how far it reaches below its floor.
    public bool AdjustSecondaryLevel(WorldMapState ctx, int dir)
    {
        if (WorldMapState.TriggerOf(Selected) == null)
        {
            return true;
        }
        var t = (TriggerSpawnEntry)Selected.EditableEntry();
        t.depthBelow = Mathf.Max(0, t.depthBelow + dir);
        return true;
    }
}
