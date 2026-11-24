using Godot;

// The gesture shared by the painter's box tools (triggers, bomb walls): a box is
// an EntityPlacement whose footprint is an axis-aligned rect of columns.
//
// Drag on empty ground to draw a box. Drag inside one to move it; drag on its
// border — the outermost ring of cells or the cell just outside — to move that
// edge, or a corner to move two. RMB deletes the box under the press. Shift+drag
// always moves, for a box too thin to have an inside. What a box IS (which list
// it lives in, how its rect is stored, how it is seated) is the subclass's.
public abstract class BoxPlacementTool : IWorldMapTool
{
    public abstract string Name { get; }
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

    protected BoxPlacementTool()
    {
        View = new CutawayGroundView();
    }

    // The box of this tool's kind under a texel, its rect grown by `grow`, on the
    // slice a map cut at `clipY` shows.
    protected abstract EntityPlacement BoxAt(WorldMapState ctx, Vector2I texel, int clipY, int grow);

    // The texel rect a box covers.
    protected abstract Rect2I BoxTexelRect(WorldMapState ctx, EntityPlacement box);

    // Make a box over a texel rect and add it to the document, or return null
    // where the tool cannot make one.
    protected abstract EntityPlacement CreateBox(WorldMapState ctx, WorldMapView view, Rect2I texelRect);

    // Move / resize a box to a texel rect.
    protected abstract void ApplyRect(WorldMapState ctx, WorldMapView view, EntityPlacement box, Rect2I texelRect);

    protected abstract void RemoveBox(WorldMapState ctx, EntityPlacement box);

    public virtual string[] Options(WorldMapState ctx) => System.Array.Empty<string>();

    public virtual Color[] OptionColors(WorldMapInk ink) => null;

    public bool NumberKeys => false;

    public virtual int OptionIndex
    {
        get => 0;
        set { }
    }

    public abstract Color CursorColor(WorldMapInk ink);

    public abstract string HintText(WorldMapState ctx);

    public abstract string StatusText(WorldMapState ctx, WorldMapView view);

    public abstract string LevelText(WorldMapState ctx, WorldMapView view);

    // The box under the cursor, its border ring included, so hovering a handle
    // names the box it would resize.
    public EntityPlacement EntityUnder(WorldMapState ctx, WorldMapView view, Vector2I texel)
        => BoxAt(ctx, texel, Clip(view), 1);

    // Where the current stroke was pressed.
    protected Vector2I PressTexel => _pressTexel;

    protected int Clip(WorldMapView view) => view.ClipFor(View.CutsAway);

    public Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase) => null;

    public Rect2I? LastPaintRect => _dirty;

    public void BeginStroke(WorldMapState ctx, WorldMapView view, Vector2I texel, EStrokeMods mods)
    {
        _strokeActed = false;
        _dirty = null;
        _pressTexel = texel;
        _edgeX = 0;
        _edgeZ = 0;
        _pressHit = BoxAt(ctx, texel, Clip(view), 1);
        if (_pressHit == null)
        {
            _grab = EGrab.Create;
            return;
        }
        _pressRect = BoxTexelRect(ctx, _pressHit);
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
                _dirty = BoxTexelRect(ctx, _pressHit).Grow(1);
                if (Selected == _pressHit)
                {
                    Selected = null;
                }
                RemoveBox(ctx, _pressHit);
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
                    _dirty = BoxTexelRect(ctx, Selected).Grow(1);
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
    // click that never drags only clears the selection — a one-metre box is
    // never what a stray click meant.
    private void PaintCreate(WorldMapState ctx, WorldMapView view, Vector2I texel)
    {
        if (!_strokeActed)
        {
            _strokeActed = true;
            if (Selected != null)
            {
                _dirty = BoxTexelRect(ctx, Selected).Grow(1);
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
            Selected = CreateBox(ctx, view, rect);
            if (Selected != null)
            {
                _dirty = rect.Grow(1).Merge(_dirty ?? rect);
            }
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
        Rect2I before = BoxTexelRect(ctx, Selected);
        if (before == texelRect)
        {
            return;
        }
        ApplyRect(ctx, view, Selected, texelRect);
        _dirty = before.Grow(1).Merge(texelRect.Grow(1));
    }

    public abstract void AdjustLevel(WorldMapState ctx, int dir);

    public virtual bool AdjustSecondaryLevel(WorldMapState ctx, int dir) => false;
}
