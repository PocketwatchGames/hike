using System;
using Godot;

// The 2D paint surface: a full-screen Control that shows the active layer's
// colourised image (the "visualized as image" view) and turns mouse strokes
// into texel-space paint callbacks. Deliberately dumb — the painter owns the
// layer data, colouring, and bake; the canvas only fits the image, draws the
// brush cursor, and reports where the user painted.
[GlobalClass]
public partial class WorldMapCanvas : Control
{
    // Set by the painter; the canvas just blits it. Updated in place by the
    // painter (ImageTexture.Update) on each stroke.
    public Texture2D DisplayTex;

    // Brush radius in texels, for the on-screen cursor ring.
    public float CursorRadiusTexels = 12f;

    // Ring colour, set by the painter from the active tool.
    public Color CursorColor = Colors.White;

    // Pixels per map metre in the IMAGE — the resolution the painter rasterised
    // at, which is what keeps a step outline a thin line on a voxel edge rather
    // than a metre-wide block.
    public int PixelsPerTexel = 3;

    // Integer magnification applied at DRAW time. Split from the raster because
    // zooming in changes nothing about the image, only how big it is drawn: at
    // this end a notch is one QueueRedraw instead of a reallocation, a full
    // repaint and a texture upload of up to 72 MB. Nearest filtering (set in
    // _Ready) is what makes the magnified pixels stay crisp squares.
    public int Zoom = 1;

    // Screen pixels per map metre — the two multiplied.
    public float ScreenPerTexel => Mathf.Max(1, PixelsPerTexel) * Mathf.Max(1, Zoom);

    private Vector2 DisplaySize => new Vector2(_imgW, _imgH) * Mathf.Max(1, Zoom);

    // (texel coord, isErase) — isErase is the right mouse button.
    public Action<Vector2I, bool> OnPaint;

    // (texel, modifiers) on button press, before the stroke paints. Returns
    // false when the host consumed the press itself (alt+RMB aiming the
    // cutaway): the stroke then ends there, so a drag before release cannot
    // turn it into an erase.
    public Func<Vector2I, EStrokeMods, bool> OnStrokeStart;

    // Button released (or the press ended any other way). The whole drag is one
    // undoable edit, so this is where it closes.
    public Action OnStrokeEnd;

    // Texel coord under the cursor on mouse motion (for the HUD readout).
    public Action<Vector2I> OnHover;

    // One wheel notch, +1 up / -1 down. The painter decides what it means.
    public Action<int> OnAdjustRadius;

    // Same, with ctrl held: the painter re-renders the map at a new scale and
    // assigns PixelsPerTexel before returning.
    public Action<int> OnZoom;

    // Same, with alt held: one metre of cutaway per notch. The plane spans the
    // whole world's height and Q/E walks it a metre a press, so on a tall
    // document reaching the ground from the top took most of a hundred presses
    // — which reads as the control doing nothing at all.
    public Action<int> OnAdjustCutaway;

    // Display-image size in PIXELS (texels * PixelsPerTexel).
    private int _imgW;
    private int _imgH;
    // Pan offset from centred, in pixels. Only meaningful once the image is
    // bigger than the control; clamped so it can never be dragged off-screen.
    private Vector2 _pan;
    private bool _panning;
    // Held while pressing, turns the press into a bucket fill. Read as a held
    // key at the press rather than as a key event, so it is a modifier like
    // shift and needs no state of its own.
    public const Key FillKey = Key.B;

    private bool _painting;
    private bool _erase;
    // An alt stroke holds its first stamp until the cursor leaves the metre it
    // sampled, so a plain alt+click is a pure eyedropper and only an actual drag
    // starts spreading that height.
    private bool _holdingPick;
    private Vector2I _pickTexel;
    private bool _hasMouse;
    // The pointer is over the map image, not just the control: the gauge shows
    // the hovered column, and off the image there is none.
    private bool _overImage;
    private Vector2 _mouseLocal;

    // The hovered column's vertical readout, drawn beside the cursor. Captured
    // by the painter; the canvas only places it.
    public WorldMapColumnGauge Gauge;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    public void SetDisplay(Texture2D tex, int imgWidth, int imgHeight)
    {
        DisplayTex = tex;
        _imgW = imgWidth;
        _imgH = imgHeight;
        QueueRedraw();
    }

    public void Refresh()
    {
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown)
            {
                // A notch reports both a press and a release; act on one of them.
                if (mb.Pressed)
                {
                    int dir = mb.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                    if (mb.CtrlPressed)
                    {
                        ZoomAbout(dir, mb.Position);
                    }
                    else if (mb.AltPressed)
                    {
                        OnAdjustCutaway?.Invoke(dir);
                    }
                    else
                    {
                        OnAdjustRadius?.Invoke(dir);
                    }
                }
                AcceptEvent();
                return;
            }
            if (mb.ButtonIndex == MouseButton.Middle)
            {
                _panning = mb.Pressed;
                AcceptEvent();
                return;
            }
            if (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right)
            {
                if (mb.Pressed)
                {
                    bool pick = mb.AltPressed && TryTexel(mb.Position, out _pickTexel);
                    EStrokeMods mods = (pick ? EStrokeMods.Pick : EStrokeMods.None)
                        | (mb.ShiftPressed ? EStrokeMods.Constrain : EStrokeMods.None)
                        | (mb.CtrlPressed ? EStrokeMods.ConstrainAbove : EStrokeMods.None)
                        | (mb.ButtonIndex == MouseButton.Right
                            ? EStrokeMods.Secondary : EStrokeMods.None)
                        | (Input.IsKeyPressed(FillKey) ? EStrokeMods.Fill : EStrokeMods.None);
                    _painting = true;
                    _erase = mb.ButtonIndex == MouseButton.Right;
                    _holdingPick = pick;
                    if (TryTexel(mb.Position, out Vector2I start)
                        && OnStrokeStart != null && !OnStrokeStart(start, mods))
                    {
                        _painting = false;
                        _holdingPick = false;
                        AcceptEvent();
                        return;
                    }
                    if (!pick)
                    {
                        PaintAt(mb.Position);
                    }
                }
                else
                {
                    bool was = _painting;
                    _painting = false;
                    _holdingPick = false;
                    if (was)
                    {
                        OnStrokeEnd?.Invoke();
                    }
                }
                AcceptEvent();
            }
        }
        else if (e is InputEventMouseMotion mm)
        {
            _hasMouse = true;
            _mouseLocal = mm.Position;
            if (_panning)
            {
                _pan += mm.Relative;
                QueueRedraw();
                return;
            }
            if (_painting && _holdingPick)
            {
                if (TryTexel(mm.Position, out Vector2I moved) && moved != _pickTexel)
                {
                    _holdingPick = false;
                    PaintAt(mm.Position);
                }
            }
            else if (_painting)
            {
                PaintAt(mm.Position);
            }
            _overImage = TryTexel(mm.Position, out Vector2I hovered);
            if (OnHover != null && _overImage)
            {
                OnHover(hovered);
            }
            QueueRedraw();
        }
    }

    // Zoom about the cursor: whichever metre is under the pointer stays under it,
    // so zooming in on a feature does not throw it off screen.
    private void ZoomAbout(int dir, Vector2 local)
    {
        float oldScale = ScreenPerTexel;
        Vector2 texel = (local - ImageRect().Position) / oldScale;
        OnZoom?.Invoke(dir);
        float newScale = ScreenPerTexel;
        if (!Mathf.IsEqualApprox(newScale, oldScale))
        {
            // PixelsPerTexel / Zoom / _imgW were all replaced by the painter
            // before this returned.
            _pan = local - texel * newScale - (Size - DisplaySize) * 0.5f;
        }
        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        if (what == (int)Control.NotificationMouseExit)
        {
            _hasMouse = false;
            _painting = false;
            _holdingPick = false;
            _panning = false;
            QueueRedraw();
        }
    }

    private void PaintAt(Vector2 local)
    {
        if (!TryTexel(local, out Vector2I texel))
        {
            return;
        }
        OnPaint?.Invoke(texel, _erase);
    }

    public override void _Draw()
    {
        Rect2 fit = ImageRect();
        if (DisplayTex != null)
        {
            DrawTextureRect(DisplayTex, fit, false);
            DrawRect(fit, new Color(0f, 0f, 0f, 0.6f), false, 2f);
        }
        if (_hasMouse && _imgW > 0)
        {
            float radius = Mathf.Max(2f, CursorRadiusTexels * ScreenPerTexel);
            if (_overImage && Gauge != null)
            {
                Gauge.Draw(this, Size);
            }
            // Dark backing arc on BOTH sides of the coloured one: a band colour
            // can be as dark as 0.2 and would otherwise vanish into the terrain
            // it is being compared against.
            DrawArc(_mouseLocal, radius - 1f, 0f, Mathf.Tau, 48, new Color(0f, 0f, 0f, 0.7f), 1f, true);
            DrawArc(_mouseLocal, radius, 0f, Mathf.Tau, 48, new Color(CursorColor.R, CursorColor.G, CursorColor.B, 0.95f), 2f, true);
            DrawArc(_mouseLocal, radius + 1.5f, 0f, Mathf.Tau, 48, new Color(0f, 0f, 0f, 0.7f), 1f, true);
        }
    }

    // The image at an INTEGER multiple of its native pixel size, centred, plus
    // any pan. Deliberately never fitted to the control: a fitted map resamples
    // metres to fractional pixels, which smears the one-pixel step outlines
    // away. An integer multiple cannot, so magnifying stays exact.
    private Rect2 ImageRect()
    {
        Vector2 c = Size;
        if (_imgW <= 0 || _imgH <= 0 || c.X <= 0f || c.Y <= 0f)
        {
            return new Rect2(Vector2.Zero, c);
        }
        Vector2 size = DisplaySize;
        // Pan only has room where the image overflows; otherwise it stays put.
        float slackX = Mathf.Max(0f, (size.X - c.X) * 0.5f);
        float slackY = Mathf.Max(0f, (size.Y - c.Y) * 0.5f);
        _pan = new Vector2(
            Mathf.Clamp(_pan.X, -slackX, slackX),
            Mathf.Clamp(_pan.Y, -slackY, slackY));
        return new Rect2(((c - size) * 0.5f + _pan).Round(), size);
    }

    private bool TryTexel(Vector2 local, out Vector2I texel)
    {
        texel = default;
        Rect2 fit = ImageRect();
        if (fit.Size.X <= 0f || fit.Size.Y <= 0f)
        {
            return false;
        }
        Vector2 rel = local - fit.Position;
        if (rel.X < 0f || rel.X >= fit.Size.X || rel.Y < 0f || rel.Y >= fit.Size.Y)
        {
            return false;
        }
        float scale = ScreenPerTexel;
        texel = new Vector2I(Mathf.FloorToInt(rel.X / scale), Mathf.FloorToInt(rel.Y / scale));
        return true;
    }
}

// The hovered column seen from the side: every voxel of it from the world's
// floor to its roof, in the colours the map uses (elevation bands, built blocks,
// water by depth), with the sea level, the cutaway plane and what the active
// tool would write marked on it. The plan view shows one floor per column; these
// are the heights the 3D-positioned tools are aimed by.
//
// Captured by the painter on hover and on every HUD update (so R/F, Q/E and a
// tool switch redraw it without the mouse moving); drawn by the canvas. A plain
// draw helper, not a node.
public sealed class WorldMapColumnGauge
{
    private struct Mark
    {
        public float Y;
        public string Text;
        public Color Color;
    }

    private WorldMapInkData _ink;
    private Color[] _cells;
    private int _minY;
    private int _seaLevel;
    private bool _cuts;
    private int _cutY;
    private PaintSpan? _span;
    private bool _valid;
    private readonly System.Collections.Generic.List<Mark> _marks = new();

    public void Capture(WorldMapState map, WorldMapInk ink, WorldMapView view, IWorldMapTool tool, Vector2I t)
    {
        WorldMapData data = map.Data;
        if (t.X < 0 || t.Y < 0 || t.X >= data.ImageWidth || t.Y >= data.ImageHeight)
        {
            _valid = false;
            return;
        }
        _ink = ink.Data;
        _minY = data.WorldMinY;
        _seaLevel = map.SeaLevel;
        _cuts = view.IsCutAway;
        _cutY = view.CutawayY;
        _span = tool.PaintSpanAt(map, t.X, t.Y);

        int height = data.VoxelHeight;
        if (_cells == null || _cells.Length != height)
        {
            _cells = new Color[height];
        }
        // Water per voxel, as the bake makes it: open water where the voxel comes
        // out empty, shaded by its depth below the top of its own body; buried
        // water (latent in ground) tints the land it is in.
        float deepAt = Mathf.Max(1, _ink.waterDeepAtVoxels);
        int bodyTop = int.MinValue;
        for (int i = height - 1; i >= 0; i--)
        {
            int wy = _minY + i;
            bool water = map.WaterAt(t.X, t.Y, wy);
            if (map.SolidAt(t.X, t.Y, wy))
            {
                bodyTop = int.MinValue;
                BlockData built = map.AddedBlockAt(t.X, t.Y, wy);
                Color land = built != null ? built.minimapColor : ink.ElevationColorAt(wy - _seaLevel);
                _cells[i] = water ? land.Lerp(_ink.columnGaugeLatentWaterInk, 0.35f) : land;
            }
            else if (water)
            {
                if (bodyTop == int.MinValue)
                {
                    bodyTop = wy;
                }
                float k = Mathf.Clamp((bodyTop - wy + 1) / deepAt, 0f, 1f);
                _cells[i] = _ink.shallowWaterColor.Lerp(_ink.deepWaterColor, k);
            }
            else
            {
                bodyTop = int.MinValue;
                _cells[i] = _ink.columnGaugeAirColor;
            }
        }
        _valid = true;
    }

    // Pinned to the right edge of `area`, vertically centred, labels to its left.
    public void Draw(CanvasItem ci, Vector2 area)
    {
        if (!_valid || _cells == null || _ink == null)
        {
            return;
        }
        const float TICK = 5f;

        int height = _cells.Length;
        int maxY = _minY + height - 1;
        float colH = Mathf.Max(height * 0.25f, area.Y * _ink.columnGaugeHeightFraction);
        float ppv = colH / height;
        float w = _ink.columnGaugeWidth;

        float x = area.X - _ink.columnGaugeRightMargin - TICK - w;
        float top = (area.Y - colH) * 0.5f;

        // Screen Y of the TOP edge of voxel wy, which is where a level of wy sits.
        float TopEdge(int wy) => top + (maxY - wy) * ppv;

        ci.DrawRect(new Rect2(x - 2f, top - 2f, w + 4f, colH + 4f), new Color(0f, 0f, 0f, 0.75f));

        // Runs of one colour as one rect: a column is mostly long runs.
        int runStart = 0;
        for (int i = 1; i <= height; i++)
        {
            bool split = i == height || _cells[i] != _cells[runStart]
                || (_cuts && (_minY + i > _cutY) != (_minY + runStart > _cutY));
            if (!split)
            {
                continue;
            }
            Color c = _cells[runStart];
            if (_cuts && _minY + runStart > _cutY)
            {
                c.A *= _ink.columnGaugeAboveCutAlpha;
            }
            int hiY = _minY + i - 1;
            int loY = _minY + runStart;
            ci.DrawRect(new Rect2(x, TopEdge(hiY), w, (hiY - loY + 1) * ppv), c);
            runStart = i;
        }

        _marks.Clear();
        float lineL = x - TICK;
        float lineR = x + w + TICK;

        if (_span is PaintSpan box && box.HasRange)
        {
            var rect = new Rect2(lineL, TopEdge(box.Hi), lineR - lineL, (box.Hi - box.Lo + 1) * ppv);
            ci.DrawRect(rect, _ink.columnGaugeRangeFill);
            ci.DrawRect(rect, _ink.columnGaugePaintInk, false, 1f);
        }

        DrawLevel(ci, TopEdge(_seaLevel), lineL, lineR, _ink.columnGaugeSeaInk, 1f, "0");
        if (_cuts)
        {
            DrawLevel(ci, TopEdge(_cutY), lineL, lineR, _ink.columnGaugeCutawayInk, 2f, $"cut {_cutY}");
        }
        if (_span is PaintSpan s)
        {
            DrawLevel(ci, TopEdge(s.Surface), lineL, lineR, _ink.columnGaugePaintInk, 2f, $"paint {s.Surface}");
            // The far end of the box: a carve's ceiling, a fill's underside.
            if (s.HasRange && s.Hi > s.Surface)
            {
                _marks.Add(new Mark { Y = TopEdge(s.Hi), Text = $"top {s.Hi}", Color = _ink.columnGaugePaintInk });
            }
            else if (s.HasRange && s.Lo < s.Surface)
            {
                _marks.Add(new Mark { Y = TopEdge(s.Lo - 1), Text = $"bottom {s.Lo}", Color = _ink.columnGaugePaintInk });
            }
        }

        DrawLabels(ci, lineL, true);
    }

    private void DrawLevel(CanvasItem ci, float y, float l, float r, Color ink, float width, string label)
    {
        ci.DrawLine(new Vector2(l - 1f, y), new Vector2(r + 1f, y), new Color(0f, 0f, 0f, 0.8f), width + 2f);
        ci.DrawLine(new Vector2(l, y), new Vector2(r, y), ink, width);
        _marks.Add(new Mark { Y = y, Text = label, Color = ink });
    }

    // Levels a few metres apart would print over each other, so labels are
    // pushed apart top to bottom and a displaced one gets a leader to its line.
    private void DrawLabels(CanvasItem ci, float anchorX, bool leftSide)
    {
        Font font = ThemeDB.FallbackFont;
        int size = _ink.columnGaugeFontSize;
        float lineH = size + 2f;
        _marks.Sort((a, b) => a.Y.CompareTo(b.Y));
        float next = float.MinValue;
        foreach (Mark m in _marks)
        {
            float y = Mathf.Max(m.Y, next);
            next = y + lineH;
            float textW = font.GetStringSize(m.Text, HorizontalAlignment.Left, -1, size).X;
            float end = leftSide ? anchorX - 5f : anchorX + 5f;
            ci.DrawLine(new Vector2(anchorX, m.Y), new Vector2(end, y), m.Color, 1f);
            float lx = leftSide ? end - 2f - textW : end + 2f;
            var pos = new Vector2(lx, y + size * 0.35f);
            ci.DrawStringOutline(font, pos, m.Text, HorizontalAlignment.Left, -1, size, 4, Colors.Black);
            ci.DrawString(font, pos, m.Text, HorizontalAlignment.Left, -1, size, m.Color);
        }
    }
}
